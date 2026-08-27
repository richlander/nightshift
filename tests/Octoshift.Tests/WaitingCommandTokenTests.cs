namespace Octoshift.Tests;

using Octoshift;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// Serializes the console-capturing tests so two of them never redirect the process-wide
/// <see cref="System.Console"/> streams at once. Disabling parallelization on the collection keeps it from
/// running alongside any other collection too, so the redirect is always exclusive.
/// </summary>
[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection
{
}

/// <summary>
/// The first-line token contract of <c>octoshift waiting</c> when the pane history cannot be read. A
/// strict-load, lock, or persistence failure returns unavailable, and the human path must lead its first
/// stdout line with the stable PARTIAL token — not only a stderr diagnostic — so a shell loop sees the
/// disposition before the details. JSON stays a single error document.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class WaitingCommandTokenTests
{
    private static async Task<(int Exit, string Out, string Err)> RunWithCapturedConsoleAsync(Func<CancellationToken, Task<int>> run, CancellationToken ct)
    {
        TextWriter savedOut = Console.Out;
        TextWriter savedErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        try
        {
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            int exit = await run(ct);
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
        }
    }

    [Fact]
    public async Task RunAsync_LeadsWithAPartialTokenWhenTheHistoryIsMalformed()
    {
        // Blocker 3 (round 8): a strict-load failure leaves fleet ownership unknown, so the human output
        // leads its first stdout line with the stable PARTIAL token — matching the unavailable exit — and
        // the detail goes to stderr. The malformed history fails the load before any collection, so this
        // needs no ssh or GitHub; an empty host list keeps it entirely local.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-waittoken-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync("owner/name", [], all: false, json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL", stdout, StringComparison.Ordinal);
            Assert.Contains("pane history unavailable", stdout, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, stderr.Trim());
            Assert.Equal("{ not a history ]", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_LeadsWithAPartialTokenWhenTheHistoryLockCannotBeTaken()
    {
        // The same aligned failure headline for a lock/persistence failure, distinct from the malformed
        // case: the history path sits under a regular file, so the lock's directory cannot be created.
        string blocker = Path.Combine(Path.GetTempPath(), $"octoshift-waitblock-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        string path = Path.Combine(blocker, "panes.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync("owner/name", [], all: false, json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL", stdout, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, stderr.Trim());
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task RunAsync_JsonHistoryFailureStaysOneErrorDocumentWithoutAHumanToken()
    {
        // Under --json the failure is one error document written to the raw stdout stream, and the human
        // PARTIAL token is not emitted through Console.Out.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-waittokenjson-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync("owner/name", [], all: false, json: true, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.DoesNotContain("PARTIAL", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_LeadsWithEmptyTokenForAnExplicitlyEmptyFleet()
    {
        // Round 11: a fleet established and then emptied by retirement is its own disposition — not a quiet
        // sweep and not a failure. A bare run must not re-bootstrap the local machine (that would undo the
        // retirement); it leads with a distinct EMPTY token and succeeds, and reaches no ssh or GitHub
        // because no target is even attempted.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-emptyfleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync("owner/name", [], all: false, json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("EMPTY", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_LeadsWithAPartialTokenWhenNoHostCouldBeCollected()
    {
        // #169: a total-collection failure — every target unreachable, nothing swept — leaves fleet
        // ownership unknown just like a history failure, so the human path must lead its first stdout line
        // with the stable PARTIAL token (matching the unavailable exit) rather than returning unavailable
        // with a silent stdout. The per-target diagnostics still go to stderr. A fresh history means the
        // fleet defaults to the local machine, whose injected scan fails, so the one target is unreachable.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-totalfail-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: false, token, historyPath: path,
                    scanAsync: (_, _) => throw new TmuxUnavailableException("local: tmux is not running")),
                ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL", stdout, StringComparison.Ordinal);
            Assert.Contains("no host could be collected", stdout, StringComparison.Ordinal);
            Assert.Contains("tmux is not running", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_JsonTotalFailureStaysOneErrorDocumentWithoutAHumanToken()
    {
        // Under --json the same total-collection failure is one truthful error document written to the raw
        // stdout stream — no PARTIAL token prepended to it — and the exit stays unavailable.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-totalfailjson-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithCapturedConsoleAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: true, token, historyPath: path,
                    scanAsync: (_, _) => throw new TmuxUnavailableException("local: tmux is not running")),
                ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.DoesNotContain("PARTIAL", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_CancellationDuringCollectionIsNotLaunderedIntoAFailureToken()
    {
        // A genuine caller cancellation during collection is not a total-collection failure: it must
        // propagate as an OperationCanceledException carrying the caller's token, never be reported as a
        // PARTIAL failure with a success-shaped exit. The injected scan cancels the caller's token and then
        // observes it, so the cancellation is raised inside collection rather than at lock acquisition.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-cancel-{Guid.NewGuid():N}.json");
        using var cts = new CancellationTokenSource();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: false, cts.Token, historyPath: path,
                    scanAsync: (_, token) =>
                    {
                        cts.Cancel();
                        token.ThrowIfCancellationRequested();
                        return Task.FromResult<IReadOnlyList<TmuxPane>>([]);
                    }));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task RunAsync_ALoneSurrogateHostIsAUsageErrorAndNeverConstructsTheScanner()
    {
        // #173, the surrogate gap: a lone-surrogate --host has no UTF-8 encoding and would otherwise mint
        // the same target key as U+FFFD, so it must fail before collection. RunAsync validates every host
        // up front, so the injected scanner is never reached, no key is minted, and the exit is Usage —
        // there is no path on which the unrepresentable alias reaches ssh or a colliding identity.
        CancellationToken ct = TestContext.Current.CancellationToken;
        (int exit, _, string stderr) = await RunWithCapturedConsoleAsync(
            token => WaitingCommand.RunAsync(
                "owner/name", ["\ud800"], all: false, json: false, token,
                scanAsync: (_, _) => throw new Xunit.Sdk.XunitException("the scanner must never be constructed for an unrepresentable host")),
            ct);

        Assert.Equal(ExitCode.Usage, exit);
        Assert.Contains("unpaired UTF-16 surrogate", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyFleetJson_IsOneSuccessDocumentMarkingTheFleetEmpty()
    {
        // The command writes JSON to the raw stdout stream Console redirection does not capture, so the
        // shape is verified at the writer: one document marking the fleet empty, with no rows — a success a
        // consumer can branch on.
        using var stream = new MemoryStream();
        WaitingCommand.WriteEmptyFleetJson(stream);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));

        Assert.Equal("empty", doc.RootElement.GetProperty("fleet").GetString());
        Assert.Empty(doc.RootElement.GetProperty("rows").EnumerateArray());
    }
}
