namespace Octoshift.Tests;

using System.Text;
using System.Text.Json;
using Octoshift;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The declared-fleet management surface: <c>octoshift fleet</c> (list) and <c>octoshift fleet retire</c>.
/// Membership grows on its own by attempting a target; retirement is the one deliberate way it shrinks, so
/// it must be unambiguous — a first-line token that matches the exit code, an unknown target reported as a
/// non-success with nothing written, and the host, pane and registration state pruned when a member goes.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class FleetCommandTests
{
    private static async Task<(int Exit, string Out, string Err)> CapturedAsync(Func<CancellationToken, Task<int>> run, CancellationToken ct)
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

    private static TmuxPane Window(string paneId, string? host, string epoch = "100:1")
        => new()
        {
            PaneId = paneId,
            Target = $"cp:{paneId.TrimStart('%')}",
            Host = host,
            WindowName = "w",
            SessionAttached = true,
            Epoch = epoch,
        };

    // Seeds a history that has attempted local plus the given remotes, so the fleet has known members to
    // list and retire.
    private static string SeedFleet(params string[] remotes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-fleet-{Guid.NewGuid():N}.json");
        var history = new PaneHistory(path);
        var panes = new List<TmuxPane>();
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        int i = 1;
        foreach (string remote in remotes)
        {
            history.AdoptEpoch(remote, $"{i}:1", t);
            panes.Add(Window($"%{i}", remote));
            i++;
        }

        // Attempt local plus every remote, so local is a member too.
        history.Save(panes, hosts: [null, .. remotes], attempted: [null, .. remotes]);
        return path;
    }

    [Fact]
    public async Task List_EmptyFleetLeadsWithFleetTokenAndSucceeds()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-fleet-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("FLEET empty", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task List_ShowsLocalAndEveryDeclaredRemote()
    {
        string path = SeedFleet("fernie", "merritt");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("FLEET 3 member(s)", stdout, StringComparison.Ordinal);
            Assert.Contains("local", stdout, StringComparison.Ordinal);
            Assert.Contains("fernie", stdout, StringComparison.Ordinal);
            Assert.Contains("merritt", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_AKnownRemoteLeadsWithRetiredAndRemovesIt()
    {
        string path = SeedFleet("fernie", "merritt");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync(["merritt"], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("RETIRED", stdout, StringComparison.Ordinal);
            Assert.Contains("merritt", stdout, StringComparison.Ordinal);

            var reopened = new PaneHistory(path);
            Assert.DoesNotContain(TargetId.ForHost("merritt").Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
            Assert.Contains(TargetId.Local.Key, reopened.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_LocalIsRetirableToo()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync([], local: true, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.Contains("local", stdout, StringComparison.Ordinal);

            var reopened = new PaneHistory(path);
            Assert.DoesNotContain(TargetId.Local.Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_AnUnknownTargetIsANonSuccessAndWritesNothing()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            string before = File.ReadAllText(path);

            (int exit, string stdout, _) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync(["ghost"], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Usage, exit);
            Assert.StartsWith("UNKNOWN", stdout, StringComparison.Ordinal);

            // Nothing was written: a typo cannot mutate the fleet.
            Assert.Equal(before, File.ReadAllText(path));
            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_AMixOfKnownAndUnknownRetiresNothing()
    {
        string path = SeedFleet("fernie", "merritt");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, _) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync(["fernie", "ghost"], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Usage, exit);

            // All-or-nothing: the known target is not retired when a sibling is unknown.
            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("merritt").Key, reopened.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_WithNoTargetIsAUsageError()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, string stderr) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync([], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Usage, exit);
            Assert.NotEqual(string.Empty, stderr.Trim());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Retire_RejectsAnOptionShapedAlias()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, string stderr) = await CapturedAsync(
                t => FleetCommand.RunRetireAsync(["-V"], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Usage, exit);
            Assert.NotEqual(string.Empty, stderr.Trim());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task List_LeadsWithPartialWhenTheHistoryIsMalformed()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-fleetbad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL", stdout, StringComparison.Ordinal);
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
    public void MembersJson_IsOneDocumentListingEveryMember()
    {
        using var stream = new MemoryStream();
        FleetCommand.WriteMembersJson(stream, [TargetId.Local, TargetId.ForHost("fernie")]);
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));

        string[] members = [.. doc.RootElement.GetProperty("members").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(["local", "fernie"], members);
    }

    [Fact]
    public void RetiredJson_IsOneDocumentListingWhatWasRetired()
    {
        using var stream = new MemoryStream();
        FleetCommand.WriteRetiredJson(stream, ["local", "merritt"]);
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));

        string[] retired = [.. doc.RootElement.GetProperty("retired").EnumerateArray().Select(e => e.GetString()!)];
        Assert.Equal(["local", "merritt"], retired);
    }
}
