namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift;
using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// What <c>octoshift pr</c> reports about one PR across the fleet. The load-bearing part is that it
/// decides ownership exactly as <c>waiting</c> does — the same contest, the same epoch and view-safety —
/// and that a partly invisible fleet fails rather than answering with confidence it does not have.
/// </summary>
public class PrCommandTests
{
    private static readonly PrFacts Ready = new()
    {
        Number = 4448,
        HeadSha = "abc1234ff",
        State = "open",
        MergeableState = "clean",
        Checks = [new CheckRunFact("ci", "completed", "success")],
    };

    private static TmuxPane Pane(
        string? host,
        string paneId,
        string target,
        string? agentState = null,
        string windowName = "w",
        PaneActivity activity = PaneActivity.Idle,
        string capture = "",
        string epoch = "")
        => new()
        {
            PaneId = paneId,
            Target = target,
            Host = host,
            WindowName = windowName,
            SessionAttached = true,
            AgentStateOption = agentState,
            Activity = activity,
            Capture = capture,
            Epoch = epoch,
        };

    private static WaitingCommand.Collection Collection(
        IReadOnlyList<TmuxPane> panes,
        IReadOnlyList<string?> collectedHosts,
        params string[] unreachable)
        => new(panes, unreachable, collectedHosts.Count + unreachable.Length, collectedHosts);

    private static PaneHistory FreshHistory()
        => new(Path.Combine(Path.GetTempPath(), $"octoshift-prtest-{Guid.NewGuid():N}.json"));

    private static Task<PrLocationResult> LocateAsync(int pr, WaitingCommand.Collection collected, PrFacts? facts = null)
        => LocateInnerAsync(pr, collected, facts);

    private static async Task<PrLocationResult> LocateInnerAsync(int pr, WaitingCommand.Collection collected, PrFacts? facts)
    {
        PrCommand.PrLocation located = await PrCommand.LocateAsync(
            pr,
            collected,
            FreshHistory(),
            (_, _) => Task.FromResult(facts),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            TestContext.Current.CancellationToken);
        return new PrLocationResult(located);
    }

    private readonly record struct PrLocationResult(PrCommand.PrLocation Located)
    {
        public string Json()
        {
            using var stream = new MemoryStream();
            PrCommand.WriteJson(stream, Located, DateTimeOffset.UtcNow);
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    [Fact]
    public async Task Locate_RanksAContestWithClaimRegisterNotAnAdHocFirstRow()
    {
        // Two windows claim PR 4448 on one host. First sweep, so which registered first is inferred, not
        // observed — so pr must name an owner but never present it as a confident one, exactly as the full
        // sweep does. A local sort would have labelled whichever row came first the owner.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [
                Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448"),
                Pane("fernie", "%2", "cp:2", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448b"),
            ],
            ["fernie"]), Ready);

        Assert.Equal(2, result.Located.Claims.Count);
        Assert.Equal(ClaimRank.Owner, result.Located.Claims[0].Claim.Rank);
        Assert.Equal(ClaimRank.Follower, result.Located.Claims[1].Claim.Rank);
        Assert.Equal(ClaimBasis.Inferred, result.Located.Claims[0].Claim.Basis);
        Assert.All(result.Located.Claims, c => Assert.False(c.Claim.OwnsClaim));
    }

    [Fact]
    public async Task Locate_APartialFleetFailsAndNamesTheUnreachableHostInJson()
    {
        // A partly invisible fleet cannot produce success-shaped output: the PR may be claimed on the host
        // that did not answer. The exit code fails and the JSON names the failure rather than omitting it.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane(null, "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448")],
            [null],
            "fernie: no server running"), Ready);

        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);

        string json = result.Json();
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.False(doc.RootElement.GetProperty("viewComplete").GetBoolean());
        Assert.Contains(
            "fernie: no server running",
            doc.RootElement.GetProperty("unreachable").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task Locate_ADuplicateWindowNameIsNotAConfidentClaim()
    {
        // Two windows on one host share the name pr4448 and publish no state. A duplicated name is a rename
        // that went where it did not belong, so it identifies nothing — pr must reject both, exactly as
        // waiting does, rather than reporting a claim on the strength of a name that names two windows.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [
                Pane("fernie", "%1", "cp:1", agentState: null, windowName: "pr4448"),
                Pane("fernie", "%2", "cp:2", agentState: null, windowName: "pr4448"),
            ],
            ["fernie"]), Ready);

        Assert.Empty(result.Located.Claims);
    }

    [Fact]
    public async Task Locate_APaneThatContradictsItsOwnPrCarriesADefect()
    {
        // The window's own output talks about PR 9999 and never 4448, while its state claims 4448 — a sign
        // the state may have been written by another agent. Pane text is the one channel another agent
        // cannot write, so the disagreement is recorded as a defect rather than yielding confident
        // ownership.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448", capture: "working on PR 9999 now")],
            ["fernie"]), Ready);

        (TmuxPane _, AgentState state, Claim _) = Assert.Single(result.Located.Claims);
        Assert.Contains(state.Defects, d => d.Contains("its state may have been written by another agent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Locate_SilenceIsKeyedByHostAndPaneIdNotPaneIdAlone()
    {
        // A pane id is unique only within one tmux server, so `%3` on two hosts is two windows. Keyed by
        // host and pane id together, each has its own silence measurement; a host-local key would let one
        // overwrite the other and lose a window's output history.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [
                Pane("fernie", "%3", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448"),
                Pane("banff", "%3", "cp:1", agentState: "pr=4600 head=abc1234", windowName: "pr4600"),
            ],
            ["fernie", "banff"]), Ready);

        Assert.Contains("fernie|%3", result.Located.Silence.Keys);
        Assert.Contains("banff|%3", result.Located.Silence.Keys);
        Assert.Equal(2, result.Located.Silence.Count);
    }

    [Fact]
    public async Task Locate_AnEmptySuccessfulHostIsRememberedSoALaterOmissionNarrowsTheView()
    {
        // Finding 3, across runs and through the pr path: run 1 successfully collects an empty fernie
        // beside a busy banff; run 2 omits fernie and must read as a narrowed, incomplete view rather
        // than a complete one.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prtest-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane onBanff = Pane("banff", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448", epoch: "1:1");

            PrCommand.PrLocation first = await PrCommand.LocateAsync(
                4448,
                Collection([onBanff], ["fernie", "banff"]),
                new PaneHistory(path),
                (_, _) => Task.FromResult<PrFacts?>(Ready),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            Assert.True(first.ViewComplete);

            PrCommand.PrLocation second = await PrCommand.LocateAsync(
                4448,
                Collection([onBanff], ["banff"]),
                new PaneHistory(path),
                (_, _) => Task.FromResult<PrFacts?>(Ready),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            Assert.False(second.ViewComplete);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Report_TheAgentLineSaysNoIdentityNotNoStateWhenFieldsWerePublished()
    {
        // #164 round 12: a record can publish rec, reviews and the rest while omitting its pr/issue, so
        // identity falls back to the window name. Calling that "published no state" is false -- the fields
        // were published -- so the line names the one thing certainly missing, the identity.
        TmuxPane pane = Pane("fernie", "%1", "cp:1", agentState: "rec=stop reviews=0/2", windowName: "pr4448");
        AgentState state = AgentState.Parse("rec=stop reviews=0/2", "pr4448")!;
        Assert.Equal(StateSource.WindowName, state.Source);

        var located = new PrCommand.PrLocation(
            4448,
            [(pane, state, Claim.Sole)],
            Ready,
            true,
            Collection([pane], ["fernie"]),
            new Dictionary<string, TimeSpan?>());

        var output = new StringWriter(CultureInfo.InvariantCulture);
        PrCommand.WriteReport(output, located, DateTimeOffset.UtcNow);
        string text = output.ToString();

        Assert.Contains("published no identity; identity read from the window name", text, StringComparison.Ordinal);
        Assert.DoesNotContain("published no state", text, StringComparison.Ordinal);

        // The published fields still follow, so nothing the agent said is dropped.
        Assert.Contains("rec stop", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_ANarrowedViewFailsTheExitCodeEvenWithoutAnUnreachableHost()
    {
        // Blocker 5, pr: a run that omits a previously-collected host is narrower than the fleet has been,
        // so the PR may be claimed on a host it did not sweep. The exit fails and the JSON says so, even
        // though no host actively errored.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prnarrow-{Guid.NewGuid():N}.json");
        try
        {
            var seed = new PaneHistory(path);
            seed.AdoptEpoch("fernie", "1:1", DateTimeOffset.UtcNow);
            seed.AdoptEpoch("banff", "2:1", DateTimeOffset.UtcNow);
            seed.Save([], ["fernie", "banff"]);

            TmuxPane onBanff = Pane("banff", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448", epoch: "2:1");
            PrCommand.PrLocation located = await PrCommand.LocateAsync(
                4448,
                Collection([onBanff], ["banff"]),
                new PaneHistory(path),
                (_, _) => Task.FromResult<PrFacts?>(Ready),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);

            Assert.False(located.ViewComplete);
            Assert.Equal(ExitCode.Unavailable, located.ExitCode);

            using JsonDocument doc = JsonDocument.Parse(new PrLocationResult(located).Json());
            Assert.False(doc.RootElement.GetProperty("viewComplete").GetBoolean());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Locate_JsonIsValidEvenWhenThePrIsNotFound()
    {
        // Nothing claims 4448 and GitHub could not be read: the not-found failure still produces a single
        // valid JSON document rather than a bare error line.
        PrLocationResult result = await LocateAsync(4448, Collection([], [null]), facts: null);

        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Empty(doc.RootElement.GetProperty("claims").EnumerateArray());
    }
}
