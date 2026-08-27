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
[Collection("ConsoleCapture")]
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
        => new(panes, unreachable, collectedHosts.Count + unreachable.Length, collectedHosts, collectedHosts);

    private static PaneHistory FreshHistory()
        => new(Path.Combine(Path.GetTempPath(), $"octoshift-prtest-{Guid.NewGuid():N}.json"));

    private static Task<PrLocationResult> LocateAsync(int pr, WaitingCommand.Collection collected, PrFacts? facts = null)
        => LocateInnerAsync(pr, collected, facts is null ? PrFetch.Unavailable : PrFetch.Found(facts));

    private static Task<PrLocationResult> LocateAsync(int pr, WaitingCommand.Collection collected, PrFetch fetch)
        => LocateInnerAsync(pr, collected, fetch);

    private static async Task<PrLocationResult> LocateInnerAsync(int pr, WaitingCommand.Collection collected, PrFetch fetch)
    {
        PrCommand.PrLocation located = await PrCommand.LocateAsync(
            pr,
            collected,
            FreshHistory(),
            (_, _) => Task.FromResult(fetch),
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

        public string Report()
        {
            var writer = new StringWriter();
            PrCommand.WriteReport(writer, Located, DateTimeOffset.UtcNow);
            return writer.ToString();
        }

        public string FirstLine() => Report().Split('\n')[0];
    }

    [Fact]
    public async Task Locate_AFoundPrLeadsWithPrAndSucceeds()
    {
        // The one success: a complete view with a claim leads with the PR token, unchanged.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448")],
            ["fernie"]), Ready);

        Assert.Equal(PrCommand.PrDisposition.Found, result.Located.Disposition);
        Assert.Equal(ExitCode.Ok, result.Located.ExitCode);
        Assert.StartsWith("PR #4448", result.FirstLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_AnUnreachableHostLeadsWithPartial()
    {
        // A host that did not answer: the PR may be claimed where the sweep could not see, so the first
        // line leads with PARTIAL and the exit fails, aligned.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane(null, "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448")],
            [null],
            "fernie: no server running"), Ready);

        Assert.Equal(PrCommand.PrDisposition.Partial, result.Located.Disposition);
        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        Assert.StartsWith("PARTIAL PR #4448", result.FirstLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_AnEmptiedFleetLocatesOnGithubWithNoLocalWindowsAndNoBootstrap()
    {
        // Round 11 / #2: `pr` over a fleet emptied by retirement does NOT re-bootstrap local and does NOT
        // emit an EMPTY token (that is `waiting`'s contract) — it locates the PR on GitHub with no window
        // rows, since a lookup does not depend on any window existing. The injected scan is never called:
        // an emptied, initialized fleet attempts no target. A complete view (nothing unreachable, nothing
        // known omitted) plus a found PR is the ordinary success.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-premptyfleet-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}");
        try
        {
            PrCommand.PrLocation located = await PrCommand.CollectAndLocateAsync(
                4448,
                [],
                (_, _) => throw new Xunit.Sdk.XunitException("an emptied fleet must attempt no target"),
                (_, _) => Task.FromResult(PrFetch.Found(Ready)),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken,
                historyPath: path);

            Assert.Empty(located.Claims);
            Assert.True(located.ViewComplete);
            Assert.Equal(PrCommand.PrDisposition.Found, located.Disposition);
            Assert.Equal(ExitCode.Ok, located.ExitCode);
            Assert.DoesNotContain("EMPTY", new PrLocationResult(located).Report(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Locate_AWindowsHistoryNarrowerThanBeforeLeadsWithNarrowed()
    {
        // A host was collected before but not in this run: no host was unreachable, yet the view is
        // narrower than it has been, so the first line leads with NARROWED — never PARTIAL, which is
        // reserved for a host that actually failed.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prnarrow-{Guid.NewGuid():N}.json");
        try
        {
            // Seed a history that knows about a second host, so this fernie-only run reads as narrowed.
            var seed = new PaneHistory(path);
            seed.AdoptEpoch("merritt", "1:1", DateTimeOffset.UtcNow);
            seed.Save([], ["merritt"]);

            PrCommand.PrLocation located = await PrCommand.LocateAsync(
                4448,
                Collection([Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448", epoch: "2:1")], ["fernie"]),
                new PaneHistory(path),
                (_, _) => Task.FromResult(PrFetch.Found(Ready)),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            var result = new PrLocationResult(located);

            Assert.Equal(PrCommand.PrDisposition.Narrowed, result.Located.Disposition);
            Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
            Assert.StartsWith("NARROWED PR #4448", result.FirstLine(), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Locate_AnAmbiguousNumberLeadsWithAmbiguousAndNamesBothRepos()
    {
        // #178: the same number resolves in two searched repos. The tool refuses to pick one — it leads
        // with AMBIGUOUS, fails the exit, names both repos, and points at the --repo remedy.
        PrLocationResult result = await LocateAsync(4623, Collection(
            [],
            [null]),
            new PrFetch(PrFetchStatus.Ambiguous, null).WithRepos(["owner/first", "owner/second"], ["owner/first", "owner/second"]));

        Assert.Equal(PrCommand.PrDisposition.Ambiguous, result.Located.Disposition);
        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        Assert.StartsWith("AMBIGUOUS PR #4623", result.FirstLine(), StringComparison.Ordinal);
        Assert.Contains("owner/first, owner/second", result.Report(), StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Equal("ambiguous", doc.RootElement.GetProperty("github").GetString());
        Assert.Equal(
            ["owner/first", "owner/second"],
            doc.RootElement.GetProperty("foundIn").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Locate_ANotFoundNamesTheSearchedReposRatherThanASingleInferredScope()
    {
        // A not-found now says which repos were searched, so a wrong-scope miss is diagnosed as "widen the
        // scope" instead of a bare "no such PR".
        PrLocationResult result = await LocateAsync(4623, Collection(
            [],
            [null]),
            PrFetch.NotFound.WithRepos(["owner/first", "owner/second"], []));

        Assert.Equal(PrCommand.PrDisposition.NotFound, result.Located.Disposition);
        Assert.Contains("no such PR in owner/first, owner/second", result.Report(), StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Equal(
            ["owner/first", "owner/second"],
            doc.RootElement.GetProperty("searched").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    [Fact]
    public async Task Locate_ALocalClaimVerdictNamesTheNotFoundScopeNotAGithubOutage()
    {
        // #178 round 1 / item 3: an idle local window claims the PR, but every searched repo 404s. The
        // claim's verdict must join against the same not-found the header names — "no such PR in <repos>",
        // not the "could not read from GitHub" an outage earns, which would contradict the top line.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448")],
            ["fernie"]),
            PrFetch.NotFound.WithRepos(["owner/first", "owner/second"], []));

        string report = result.Report();
        Assert.Contains("no such PR #4448 in owner/first, owner/second", report, StringComparison.Ordinal);
        Assert.DoesNotContain("could not read PR #4448 from GitHub", report, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        JsonElement claim = doc.RootElement.GetProperty("claims").EnumerateArray().First();
        Assert.Contains(
            "no such PR #4448 in owner/first, owner/second",
            claim.GetProperty("verdict").GetProperty("reason").GetString()!,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_ALocalClaimVerdictSaysAmbiguousWhenTheNumberCollides()
    {
        // #178 round 1 / item 3: with a claim and an ambiguous collision, the claim's verdict says
        // ambiguous — matching the AMBIGUOUS header — rather than a bare "could not read from GitHub".
        PrLocationResult result = await LocateAsync(4623, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4623 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4623")],
            ["fernie"]),
            new PrFetch(PrFetchStatus.Ambiguous, null).WithRepos(["owner/first", "owner/second"], ["owner/first", "owner/second"]));

        Assert.Equal(PrCommand.PrDisposition.Ambiguous, result.Located.Disposition);
        string report = result.Report();
        Assert.Contains("ambiguous", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("could not read PR #4623 from GitHub", report, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        JsonElement claim = doc.RootElement.GetProperty("claims").EnumerateArray().First();
        Assert.Contains(
            "ambiguous",
            claim.GetProperty("verdict").GetProperty("reason").GetString()!,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Locate_AcompleteViewWithNeitherClaimNorPrLeadsWithNotFound()
    {
        // A complete view that turned up no claiming window, and GitHub affirmatively 404s: NOTFOUND, exit
        // unavailable. The 404 is what earns NOTFOUND — an unreadable GitHub would be PARTIAL instead.
        PrLocationResult result = await LocateAsync(4999, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448")],
            ["fernie"]), PrFetch.NotFound);

        Assert.Equal(PrCommand.PrDisposition.NotFound, result.Located.Disposition);
        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        Assert.StartsWith("NOTFOUND PR #4999", result.FirstLine(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_AcompleteViewWithNoClaimAndUnreadableGithubLeadsWithPartialNotNotFound()
    {
        // The round-8 blocker: a complete, fully-reached view with no claiming window, and GitHub could
        // not be read (auth, rate limit, transport, 5xx, malformed all reduce to Unavailable at the source).
        // Existence is unknown, so this must lead PARTIAL and fail — never NOTFOUND, which would assert
        // GitHub has no such PR off an outage. The JSON names the outcome truthfully.
        PrLocationResult result = await LocateAsync(4999, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448")],
            ["fernie"]), PrFetch.Unavailable);

        Assert.Equal(PrCommand.PrDisposition.Unavailable, result.Located.Disposition);
        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        Assert.StartsWith("PARTIAL PR #4999", result.FirstLine(), StringComparison.Ordinal);
        Assert.DoesNotContain("NOTFOUND", result.Report(), StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Report(), StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Equal("unavailable", doc.RootElement.GetProperty("github").GetString());
    }

    [Fact]
    public async Task Locate_AClaimWithUnreadableGithubUnderACompleteViewStillLeadsWithPr()
    {
        // A window claims the PR but GitHub could not be read. The view is complete and a claim exists, so
        // this is a find, not a not-found: it keeps the PR lead and succeeds, and the body still says
        // GitHub could not be read.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448")],
            ["fernie"]), facts: null);

        Assert.Equal(PrCommand.PrDisposition.Found, result.Located.Disposition);
        Assert.Equal(ExitCode.Ok, result.Located.ExitCode);
        Assert.StartsWith("PR #4448", result.FirstLine(), StringComparison.Ordinal);
        Assert.Contains("could not be read", result.Report(), StringComparison.Ordinal);
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
    public async Task Locate_ContestedIdleOwnerReleasingWithWorkingFollowerSuggestsPromotion()
    {
        // Blocker 3, promotion half at the surface. The owner is idle and asking to stop — a genuine
        // release — while a follower is still working, so ownership is with the window doing the least. The
        // report suggests promoting the follower.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [
                Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 rec=stop", windowName: "pr4448", activity: PaneActivity.Idle),
                Pane("fernie", "%2", "cp:2", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448b", activity: PaneActivity.Working),
            ],
            ["fernie"]), Ready);

        Assert.Equal(ClaimRank.Owner, result.Located.Claims[0].Claim.Rank);
        Assert.Contains("consider promoting it", result.Report(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Locate_ContestedNonIdleOwnerWithStaleStopDoesNotSuggestPromotion()
    {
        // Blocker 3: the same shape, but the owner is mid-turn. A stale rec=stop under a working owner is
        // not a release — the pane is not idle and has handed nothing over — so no follower promotion is
        // suggested off a record the pane contradicts.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [
                Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 rec=stop", windowName: "pr4448", activity: PaneActivity.Working, capture: "still working\n> "),
                Pane("fernie", "%2", "cp:2", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448b", activity: PaneActivity.Working),
            ],
            ["fernie"]), Ready);

        Assert.Equal(ClaimRank.Owner, result.Located.Claims[0].Claim.Rank);
        Assert.DoesNotContain("consider promoting it", result.Report(), StringComparison.Ordinal);
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
    public Task Locate_AWorkingPaneNeverPrintsAResolvedReadyVerdict()
        => AssertNonIdleVerdictIsGated(PaneActivity.Working, "UNKNOWN", "unknown");

    [Fact]
    public Task Locate_ABlockedPaneNeverPrintsAResolvedReadyVerdict()
        => AssertNonIdleVerdictIsGated(PaneActivity.Blocked, "NEEDSOPERATOR", "needsoperator");

    [Fact]
    public Task Locate_AStalledPaneNeverPrintsAResolvedReadyVerdict()
        => AssertNonIdleVerdictIsGated(PaneActivity.Stalled, "NEEDSOPERATOR", "needsoperator");

    [Fact]
    public Task Locate_AnUnreadablePaneNeverPrintsAResolvedReadyVerdict()
        => AssertNonIdleVerdictIsGated(PaneActivity.Unreadable, "UNKNOWN", "unknown");

    private static async Task AssertNonIdleVerdictIsGated(PaneActivity activity, string humanState, string jsonState)
    {
        // Round 9, blocker 3: `octoshift pr` gates the published verdict on what the pane is doing now,
        // exactly as `octoshift waiting` does. A window mid-turn, one holding a prompt open, one stalled, and
        // one that could not be captured all carry a stale reviews=2/2 rec=merge, but none has handed
        // anything over — so none may resolve to an actionable READY off that stale state. The human report
        // and the JSON must agree on the gated verdict.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234ff reviews=2/2 rec=merge",
                windowName: "pr4448", activity: activity, capture: "agent output")],
            ["fernie"]), Ready);

        string report = result.Report();
        Assert.DoesNotContain("READY", report, StringComparison.Ordinal);
        Assert.Contains($"verdict   {humanState}", report, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        JsonElement verdict = doc.RootElement.GetProperty("claims")[0].GetProperty("verdict");
        Assert.Equal(jsonState, verdict.GetProperty("state").GetString());
    }

    [Fact]
    public async Task Locate_AnIdlePaneStillResolvesThePublishedVerdictToReady()
    {
        // The other half of the gate: an idle pane HAS handed over, so its published reviews=2/2 rec=merge
        // on a clean, green PR still resolves to READY. The gate suppresses the verdict for non-idle panes
        // only — it does not blunt the ordinary idle handover the tool exists to surface.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234ff reviews=2/2 rec=merge",
                windowName: "pr4448", activity: PaneActivity.Idle)],
            ["fernie"]), Ready);

        Assert.Contains("verdict   READY", result.Report(), StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Equal("ready", doc.RootElement.GetProperty("claims")[0].GetProperty("verdict").GetProperty("state").GetString());
    }

    [Fact]
    public Task Locate_WithoutGithubFacts_AWorkingClaimStillSurfacesItsVerdict()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234 reviews=2/2 rec=merge", PaneActivity.Working, "UNKNOWN", "unknown", "mid-turn");

    [Fact]
    public Task Locate_WithoutGithubFacts_ABlockedClaimSurfacesToOperator()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234", PaneActivity.Blocked, "NEEDSOPERATOR", "needsoperator", "prompt open");

    [Fact]
    public Task Locate_WithoutGithubFacts_AStalledClaimSurfacesToOperator()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234", PaneActivity.Stalled, "NEEDSOPERATOR", "needsoperator", "stalled");

    [Fact]
    public Task Locate_WithoutGithubFacts_AnUnreadablePaneSurfaces()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234", PaneActivity.Unreadable, "UNKNOWN", "unknown", "could not be captured");

    [Fact]
    public Task Locate_WithoutGithubFacts_AnIdleStopEscalationSurfaces()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234 rec=stop", PaneActivity.Idle, "NEEDSOPERATOR", "needsoperator", "asking to stop");

    [Fact]
    public Task Locate_WithoutGithubFacts_AnIdleApproveEscalationSurfaces()
        => AssertVerdictSurfacesWithoutFacts(
            "pr=4448 head=abc1234 rec=approve", PaneActivity.Idle, "NEEDSOPERATOR", "needsoperator", "authorise more rounds");

    private static async Task AssertVerdictSurfacesWithoutFacts(
        string agentState, PaneActivity activity, string humanState, string jsonState, string reasonFragment)
    {
        // Blocker 2: `octoshift pr` must apply the activity/escalation gate to every claim even when GitHub
        // is unavailable. Working/blocked/stalled/unreadable activity is derived from the pane, not GitHub,
        // and an idle explicit rec=stop/rec=approve escalation is the agent asking a person to decide — none
        // of these need the PR to be legible, so suppressing the verdict when Facts is null (the old
        // `if (facts is null) break;`) erased exactly the signals an operator most needs during an outage.
        // Both the human report and the JSON must carry the verdict, with GitHub read as unavailable.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: agentState, windowName: "pr4448",
                activity: activity, capture: "Execution failed: boom\n> ")],
            ["fernie"]));   // no facts: GitHub unavailable

        string report = result.Report();
        Assert.Contains($"verdict   {humanState}", report, StringComparison.Ordinal);
        Assert.Contains(reasonFragment, report, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        JsonElement claim = doc.RootElement.GetProperty("claims")[0];
        Assert.Equal(jsonState, claim.GetProperty("verdict").GetProperty("state").GetString());
        Assert.Equal("unavailable", doc.RootElement.GetProperty("github").GetString());
    }

    [Fact]
    public async Task Locate_WithoutGithubFacts_AGenuinelyGithubDependentIdleClaimStaysLowConfidenceUnknown()
    {
        // The complement of the escalation cases: an idle window whose readiness genuinely depends on
        // GitHub (reviews=2/2 rec=merge) must NOT be invented into a READY when GitHub cannot be read. It
        // surfaces as a low-confidence UNKNOWN — the verdict is present, but it does not claim a readiness
        // the tool cannot verify.
        PrLocationResult result = await LocateAsync(4448, Collection(
            [Pane("fernie", "%1", "cp:1", agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge",
                windowName: "pr4448", activity: PaneActivity.Idle)],
            ["fernie"]));   // no facts

        string report = result.Report();
        Assert.Contains("verdict   UNKNOWN", report, StringComparison.Ordinal);
        Assert.DoesNotContain("READY", report, StringComparison.Ordinal);

        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Equal("unknown", doc.RootElement.GetProperty("claims")[0].GetProperty("verdict").GetProperty("state").GetString());
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
        // the structured target id and the pane id, each has its own silence measurement; a host-local key
        // would let one overwrite the other and lose a window's output history. The two keys are distinct
        // and neither is a raw `host|pane` an alias could forge.
        TmuxPane onFernie = Pane("fernie", "%3", "cp:1", agentState: "pr=4448 head=abc1234", windowName: "pr4448");
        TmuxPane onBanff = Pane("banff", "%3", "cp:1", agentState: "pr=4600 head=abc1234", windowName: "pr4600");
        PrLocationResult result = await LocateAsync(4448, Collection([onFernie, onBanff], ["fernie", "banff"]), Ready);

        Assert.Contains(Claim.Key(onFernie), result.Located.Silence.Keys);
        Assert.Contains(Claim.Key(onBanff), result.Located.Silence.Keys);
        Assert.NotEqual(Claim.Key(onFernie), Claim.Key(onBanff));
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
                (_, _) => Task.FromResult(PrFetch.Found(Ready)),
                (_, _) => Task.FromResult<PrFacts?>(null),
                DateTimeOffset.UtcNow,
                TestContext.Current.CancellationToken);
            Assert.True(first.ViewComplete);

            PrCommand.PrLocation second = await PrCommand.LocateAsync(
                4448,
                Collection([onBanff], ["banff"]),
                new PaneHistory(path),
                (_, _) => Task.FromResult(PrFetch.Found(Ready)),
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
            PrFetchStatus.Found,
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
                (_, _) => Task.FromResult(PrFetch.Found(Ready)),
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
        // Nothing claims 4448 and GitHub affirmatively 404s: the not-found failure still produces a single
        // valid JSON document rather than a bare error line, and it names the outcome truthfully.
        PrLocationResult result = await LocateAsync(4448, Collection([], [null]), PrFetch.NotFound);

        Assert.Equal(ExitCode.Unavailable, result.Located.ExitCode);
        using JsonDocument doc = JsonDocument.Parse(result.Json());
        Assert.Empty(doc.RootElement.GetProperty("claims").EnumerateArray());
        Assert.Equal("notfound", doc.RootElement.GetProperty("github").GetString());
    }

    // Serializes the few tests that must capture the process-wide Console streams, and restores them.
    private static readonly SemaphoreSlim ConsoleGate = new(1, 1);

    private static async Task<(int Exit, string Out, string Err)> RunWithCapturedConsoleAsync(Func<CancellationToken, Task<int>> run, CancellationToken ct)
    {
        await ConsoleGate.WaitAsync(ct);
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
            ConsoleGate.Release();
        }
    }

    [Fact]
    public async Task RunAsync_LeadsWithAPartialTokenWhenTheHistoryIsMalformed()
    {
        // Blocker 4: a history failure leaves fleet ownership unknown, so the human output leads its first
        // stdout line with an aligned failure token — not only a stderr line — matching the unavailable
        // exit. The detail goes to stderr. The malformed history fails the load before any collection, so
        // this needs no ssh or GitHub.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prtoken-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithCapturedConsoleAsync(
                token => PrCommand.RunAsync(4448, ["owner/name"], [], json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL PR #4448", stdout, StringComparison.Ordinal);
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
        string blocker = Path.Combine(Path.GetTempPath(), $"octoshift-prblock-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        string path = Path.Combine(blocker, "panes.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithCapturedConsoleAsync(
                token => PrCommand.RunAsync(4448, ["owner/name"], [], json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL PR #4448", stdout, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, stderr.Trim());
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task RunAsync_JsonHistoryFailureReturnsUnavailableWithoutAHumanToken()
    {
        // Under --json the failure is one error document (validated by WriteJsonError's own test) written
        // to the raw stdout stream, and the human PARTIAL token is not emitted through Console.Out.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prtokenjson-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithCapturedConsoleAsync(
                token => PrCommand.RunAsync(4448, ["owner/name"], [], json: true, token, historyPath: path), ct);

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
    public async Task RunAsync_GenuineCancellationPropagatesWithoutAFailureToken()
    {
        // A caller cancellation is not a history failure: it must escape as an OperationCanceledException,
        // never be dressed up as a PARTIAL token. The lock is held so RunAsync blocks acquiring the
        // transaction, then the caller cancels.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prcancel-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            using PaneHistory holder = await PaneHistory.OpenAsync(path, ct);
            using var cts = new CancellationTokenSource();

            await ConsoleGate.WaitAsync(ct);
            TextWriter savedOut = Console.Out;
            TextWriter savedErr = Console.Error;
            var outWriter = new StringWriter();
            try
            {
                Console.SetOut(outWriter);
                Console.SetError(new StringWriter());
                Task<int> run = PrCommand.RunAsync(4448, ["owner/name"], [], json: false, cts.Token, historyPath: path);
                await Task.Delay(150, ct);
                await cts.CancelAsync();

                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
                Assert.DoesNotContain("PARTIAL", outWriter.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                Console.SetOut(savedOut);
                Console.SetError(savedErr);
                ConsoleGate.Release();
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }
}
