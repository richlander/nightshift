namespace Octoshift.Tests;

using Octoshift;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The command-level auth contract with GitHub App credentials configured but unusable (the repro
/// <c>OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH=/missing octoshift waiting|pr …</c>). Each test drives a
/// <em>successful</em> collection so the GitHub read actually reaches the disabled runner, proving the
/// broken credentials become a GitHub-unavailable fact rather than a crash — <c>waiting</c> degrades to a
/// truthful exit-0 report with a per-row caveat over a complete fleet, and <c>pr</c> stays unavailable when
/// it cannot resolve the PR. Joining the ConsoleCapture collection serializes the console redirect and the
/// process-wide environment mutation with the other command tests.
/// </summary>
[Collection("ConsoleCapture")]
public class AuthContractCommandTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";

    /// <summary>An idle window that names a PR and asks to stop — a row that needs a person and a GitHub read.</summary>
    private static TmuxPane IdlePrPane(int pr) => new()
    {
        PaneId = "%1",
        Target = "night:1",
        WindowName = $"pr{pr}",
        SessionAttached = false,
        Activity = PaneActivity.Idle,
        LastActivity = DateTimeOffset.UtcNow.AddHours(-2),
        AgentStateOption = $"pr={pr} head=1234567890abcdef1234567890abcdef12345678 rec=stop reviews=1/2",
        Capture = "$ ",
    };

    /// <summary>
    /// Runs <paramref name="run"/> with a configured-but-missing App credentials path — the case that made
    /// the runner configured-but-broken — capturing the console and restoring the environment afterwards.
    /// </summary>
    private static async Task<(int Exit, string Out, string Err)> RunWithBrokenAppCredentialsAsync(
        Func<CancellationToken, Task<int>> run, CancellationToken ct)
    {
        string missingCredentialsPath = Path.Combine(Path.GetTempPath(), $"octoshift-missing-creds-{Guid.NewGuid():N}.json");
        string? savedEnv = Environment.GetEnvironmentVariable(CredentialsPathVariable);
        TextWriter savedOut = Console.Out;
        TextWriter savedErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(CredentialsPathVariable, missingCredentialsPath);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            int exit = await run(ct);
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Environment.SetEnvironmentVariable(CredentialsPathVariable, savedEnv);
        }
    }

    [Fact]
    public async Task Waiting_BrokenAppCredentials_DegradesToExitZeroReportWithGitHubCaveat()
    {
        // A successful scan of one idle PR pane completes the fleet, so the read reaches the disabled runner;
        // GitHub comes back unreadable and the row carries that caveat, but the collection is complete, so
        // the report is a truthful degraded view and the exit stays 0.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authwait-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithBrokenAppCredentialsAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: false, token, historyPath: path,
                    scanAsync: (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([IdlePrPane(4595)])),
                ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.Contains("could not be read", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Waiting_BrokenAppCredentials_JsonDegradesToExitZeroReport()
    {
        // The JSON report is one document over the complete fleet with the same disposition; the exit stays 0.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authwaitjson-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithBrokenAppCredentialsAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: true, token, historyPath: path,
                    scanAsync: (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([IdlePrPane(4595)])),
                ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.DoesNotContain("PARTIAL", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Pr_BrokenAppCredentials_StaysUnavailableWhenGitHubCannotResolveThePr()
    {
        // An initialized, empty fleet is a complete view with no claimant, so LocateAsync's unconditional
        // GitHub read reaches the disabled runner. GitHub is unreadable, so the PR's existence is unknown and
        // the human path leads with the PARTIAL token at ExitCode.Unavailable.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authpr-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithBrokenAppCredentialsAsync(
                token => PrCommand.RunAsync(4595, "owner/name", [], json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL PR #4595", stdout, StringComparison.Ordinal);
            Assert.Contains("could not be read", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Pr_BrokenAppCredentials_JsonStaysUnavailable()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authprjson-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, _) = await RunWithBrokenAppCredentialsAsync(
                token => PrCommand.RunAsync(4595, "owner/name", [], json: true, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }
}
