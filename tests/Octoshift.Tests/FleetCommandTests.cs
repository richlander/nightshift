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
    public async Task Add_AHostAndLocalDeclareThemAndLeadWithAdded()
    {
        // A fleet first declared with only a remote has no way to gain local except an explicit add. Adding
        // local and another host declares both, leads with the ADDED token, and makes them members.
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            // Start from a fleet that has local retired, so the add genuinely re-declares it.
            await CapturedAsync(t => FleetCommand.RunRetireAsync([], local: true, json: false, t, historyPath: path), ct);
            Assert.DoesNotContain(TargetId.Local.Key, new PaneHistory(path).KnownHosts);

            (int exit, string stdout, _) = await CapturedAsync(
                t => FleetCommand.RunAddAsync(["merritt"], local: true, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("ADDED", stdout, StringComparison.Ordinal);

            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.Local.Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("merritt").Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Add_LocalReturnsAfterRetirementAndABareSweepReachesItAgain()
    {
        // The explicit-local-re-add path: retire the sole local member, confirm a bare sweep would then
        // reach nothing (the empty fleet stays empty), then add --local and confirm the bootstrap is back.
        string path = SeedFleet();
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            await CapturedAsync(t => FleetCommand.RunRetireAsync([], local: true, json: false, t, historyPath: path), ct);
            Assert.Empty(new PaneHistory(path).FleetTargets([]));

            (int exit, _, _) = await CapturedAsync(
                t => FleetCommand.RunAddAsync([], local: true, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.Local.Key, reopened.KnownHosts);

            // A bare sweep now reaches the local machine again.
            Assert.Equal<string?[]>([null], [.. reopened.FleetTargets([])]);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Add_RejectsAnOptionShapedAliasAndWritesNothing()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            string before = File.ReadAllText(path);
            (int exit, _, string stderr) = await CapturedAsync(
                t => FleetCommand.RunAddAsync(["-V"], local: false, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Usage, exit);
            Assert.NotEqual(string.Empty, stderr.Trim());
            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Add_WithNoTargetIsAUsageError()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, string stderr) = await CapturedAsync(
                t => FleetCommand.RunAddAsync([], local: false, json: false, t, historyPath: path), ct);

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
    public async Task Add_HumanOutputPreservesTargetKind()
    {
        string path = SeedFleet("fernie");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(
                t => FleetCommand.RunAddAsync(["merritt"], local: true, json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("ADDED", stdout, StringComparison.Ordinal);
            // The real local machine labels as `local`; a remote as `host <alias>`, so a consumer can tell
            // which flag named it.
            Assert.Contains("local", stdout, StringComparison.Ordinal);
            Assert.Contains("host merritt", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task List_ExplicitlyEmptyFleetReportsEmptiedNotDefaultLocal()
    {
        // After retiring the sole member the fleet is empty ON PURPOSE. List must say so — distinct from a
        // never-established fleet, which defaults to scanning local — so an operator is not told the local
        // machine will be swept when it will not.
        string path = SeedFleet();
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            await CapturedAsync(t => FleetCommand.RunRetireAsync([], local: true, json: false, t, historyPath: path), ct);

            (int exit, string stdout, _) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);

            Assert.Equal(ExitCode.Ok, exit);
            Assert.StartsWith("FLEET empty", stdout, StringComparison.Ordinal);
            Assert.Contains("emptied", stdout, StringComparison.Ordinal);
            Assert.DoesNotContain("scanned by default", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task List_UninitializedFleetReportsDefaultLocal()
    {
        // A genuinely fresh history (absent file) is uninitialized: list says the local machine is scanned
        // by default — the distinction a consumer needs to tell it from an emptied fleet. (The JSON
        // initialized flag is covered directly in MembersJson_*, since the command writes JSON to the raw
        // stdout stream Console redirection does not capture.)
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-fleetfresh-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);
            Assert.Equal(ExitCode.Ok, exit);
            Assert.Contains("scanned by default", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task List_HumanOutputPreservesTheKindOfAnAliasNamedLocal()
    {
        // The collision this whole scheme exists to prevent, at the fleet surface: an ssh alias literally
        // named `local` must render as `host local`, distinct from the real local machine's `local`, so a
        // consumer can tell whether to pass --local or --host local.
        string path = SeedFleet("local");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await CapturedAsync(t => FleetCommand.RunListAsync(json: false, t, historyPath: path), ct);
            Assert.Equal(ExitCode.Ok, exit);
            Assert.Contains("host local", stdout, StringComparison.Ordinal);
            // And the real local machine is present as a bare `local` line, not `host local`.
            Assert.Contains(stdout.Split('\n'), l => l.Trim() == "local");
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void FleetTargets_BootstrapsLocalOnlyWhileUninitialized()
    {
        // The mechanism behind the empty-fleet contract, at the history level: an absent (fresh) history
        // bootstraps local; once established and emptied, it returns nothing rather than re-adding local.
        string fresh = Path.Combine(Path.GetTempPath(), $"octoshift-ft-{Guid.NewGuid():N}.json");
        try
        {
            var uninitialized = new PaneHistory(fresh);
            Assert.False(uninitialized.IsInitialized);
            Assert.Equal<string?[]>([null], [.. uninitialized.FleetTargets([])]);

            // Establish (attempt local), then retire it: the fleet is now empty on purpose.
            uninitialized.Save([], hosts: [null], attempted: [null]);
            var established = new PaneHistory(fresh);
            Assert.True(established.IsInitialized);
            established.Retire(null);
            established.Persist();

            var emptied = new PaneHistory(fresh);
            Assert.True(emptied.IsInitialized);
            Assert.Empty(emptied.FleetTargets([]));

            // A --host request on an empty fleet still declares that host; local stays out.
            Assert.Equal<string?[]>(["fernie"], [.. emptied.FleetTargets(["fernie"])]);
        }
        finally
        {
            File.Delete(fresh);
            File.Delete(fresh + ".lock");
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
    public void MembersJson_IsOneDocumentListingEveryMemberWithItsKind()
    {
        using var stream = new MemoryStream();
        FleetCommand.WriteMembersJson(stream, [TargetId.Local, TargetId.ForHost("fernie"), TargetId.ForHost("local")], initialized: true);
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));

        Assert.True(doc.RootElement.GetProperty("initialized").GetBoolean());
        JsonElement[] members = [.. doc.RootElement.GetProperty("members").EnumerateArray()];

        // The real local machine: kind local, no host.
        Assert.Equal("local", members[0].GetProperty("kind").GetString());
        Assert.False(members[0].TryGetProperty("host", out _));

        // A remote: kind host, with its alias.
        Assert.Equal("host", members[1].GetProperty("kind").GetString());
        Assert.Equal("fernie", members[1].GetProperty("host").GetString());

        // An ssh alias literally named `local` is NOT the local machine — a consumer must be able to tell.
        Assert.Equal("host", members[2].GetProperty("kind").GetString());
        Assert.Equal("local", members[2].GetProperty("host").GetString());
    }

    [Fact]
    public void MembersJson_CarriesTheInitializedFlagSoAnEmptiedFleetIsDistinguishable()
    {
        using var stream = new MemoryStream();
        FleetCommand.WriteMembersJson(stream, [], initialized: false);
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));

        Assert.False(doc.RootElement.GetProperty("initialized").GetBoolean());
        Assert.Empty(doc.RootElement.GetProperty("members").EnumerateArray());
    }

    [Fact]
    public void RetiredJson_IsOneDocumentListingWhatWasRetiredWithKind()
    {
        using var stream = new MemoryStream();
        FleetCommand.WriteTargetsJson(stream, "retired", [TargetId.Local, TargetId.ForHost("merritt")]);
        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));

        JsonElement[] retired = [.. doc.RootElement.GetProperty("retired").EnumerateArray()];
        Assert.Equal("local", retired[0].GetProperty("kind").GetString());
        Assert.False(retired[0].TryGetProperty("host", out _));
        Assert.Equal("host", retired[1].GetProperty("kind").GetString());
        Assert.Equal("merritt", retired[1].GetProperty("host").GetString());
    }
}
