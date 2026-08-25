namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The plumbing around the pure core: reading tmux, reading GitHub conditionally, and deciding which
/// panes are worth a call at all. Every seam is faked, so nothing here starts a process or a request.
/// </summary>
public class WaitingScanTests
{
    private const string Head = "722512e25f0c1d4a9b8e7360a1c2d3e4f5061728";

    private const string Nonce = "deadbeefcafe0123";

    /// <summary>Builds a collection stream the way the script emits one: manifest, then captures.</summary>
    private static string Stream(IEnumerable<string> manifest, params (string PaneId, string Text)[] captures)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(Nonce).Append(":epoch 4242:1755900000\n");
        sb.Append(Nonce).Append(":manifest\n");
        foreach (string row in manifest)
        {
            sb.Append(row).Append('\n');
        }

        sb.Append(Nonce).Append(":end\n");
        foreach ((string paneId, string text) in captures)
        {
            sb.Append(Nonce).Append(":pane ").Append(paneId).Append('\n').Append(text).Append('\n');
        }

        return sb.ToString();
    }

    [Fact]
    public void ParseCollection_ReadsTargetAttachmentAndActivity()
    {
        IReadOnlyList<TmuxPane> windows = TmuxScanner.ParseCollection(
            Stream([
                "%1|night:3|1|1755900000|pr=4595 head=abc1234 reviews=2/2 rec=merge|pr4595",
                "%2|night:4|0|1755800000||i158"]),
            host: null,
            Nonce);

        Assert.Equal(2, windows.Count);
        Assert.Equal("%1", windows[0].PaneId);
        Assert.Equal("night:3", windows[0].Target);
        Assert.True(windows[0].SessionAttached);
        Assert.Equal("pr4595", windows[0].WindowName);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1755900000), windows[0].LastActivity);
        Assert.Equal("pr=4595 head=abc1234 reviews=2/2 rec=merge", windows[0].AgentStateOption);
        Assert.Null(windows[1].AgentStateOption);
        Assert.False(windows[1].SessionAttached);
    }

    [Fact]
    public void ParseCollection_KeepsAPipeInTheWindowName()
    {
        // Window name is formatted last precisely so a separator inside it cannot shift earlier fields.
        IReadOnlyList<TmuxPane> windows = TmuxScanner.ParseCollection(Stream(["%7|night:3|1|1755900000||pr4595|round2"]), host: null, Nonce);

        TmuxPane window = Assert.Single(windows);
        Assert.Equal("night:3", window.Target);
        Assert.Equal("pr4595|round2", window.WindowName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\nmalformed row")]
    [InlineData("night:3|1|1755900000||name")]
    [InlineData("%1|night:3|1|1755900000")]
    [InlineData("night:3|1|1755900000||80|name")]
    public void ParseCollection_DropsMalformedRows(string stdout)
        => Assert.Empty(TmuxScanner.ParseCollection(Stream([stdout]), host: null, Nonce));

    [Fact]
    public void ClassifyActivity_ReadsTheFooter()
    {
        Assert.Equal(PaneActivity.Working, TmuxScanner.ClassifyActivity("Thinking…\n(esc to interrupt)"));
        Assert.Equal(PaneActivity.Blocked, TmuxScanner.ClassifyActivity("Do you want to proceed?\n(enter to confirm)"));
        Assert.Equal(PaneActivity.Idle, TmuxScanner.ClassifyActivity("Round 2 is complete for PR 4563.\n\n> "));
    }

    [Fact]
    public void ClassifyActivity_OnlyReadsTheFooterNotTheWholeScreen()
    {
        // An interrupt hint from an earlier turn is scrolled history, not the current state.
        string capture = string.Join('\n', ["(esc to interrupt)", .. Enumerable.Repeat("output line", 12), "> "]);

        Assert.Equal(PaneActivity.Idle, TmuxScanner.ClassifyActivity(capture));
    }

    [Fact]
    public async Task FetchAsync_JoinsPullDetailWithChecksOnTheHead()
    {
        var gh = new FakeGh
        {
            [$"repos/o/r/pulls/4595"] = Response(200, """
                {"number":4595,"state":"open","merged":false,"mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
                """),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(200, """
                {"check_runs":[{"name":"ci-required","status":"completed","conclusion":"success"}]}
                """),
        };

        PrFacts? facts = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync).FetchAsync(4595, TestContext.Current.CancellationToken);

        Assert.NotNull(facts);
        Assert.Equal(Head, facts.HeadSha);
        Assert.Equal("clean", facts.MergeableState);
        Assert.False(facts.IsConflicting);
        CheckRunFact check = Assert.Single(facts.Checks);
        Assert.Equal("ci-required", check.Name);
        Assert.False(check.IsFailure);
    }

    [Fact]
    public async Task FetchAsync_ServesA304FromCacheWithoutSpendingBudget()
    {
        var cache = new FakeCache();
        cache.Put($"repos/o/r/pulls/4595", "\"etag-pull\"", """
            {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
            """);
        cache.Put($"repos/o/r/commits/{Head}/check-runs?per_page=100", "\"etag-checks\"", """{"check_runs":[]}""");

        var gh = new FakeGh
        {
            [$"repos/o/r/pulls/4595"] = Response(304, string.Empty),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(304, string.Empty),
        };

        var source = new GhPrFactsSource("o/r", cache, gh.RunAsync);
        PrFacts? facts = await source.FetchAsync(4595, TestContext.Current.CancellationToken);

        Assert.NotNull(facts);
        Assert.Equal(Head, facts.HeadSha);
        Assert.Equal(2, source.NotModified);
        Assert.All(gh.Requests, args => Assert.Contains(args, a => a.StartsWith("If-None-Match:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchAsync_ReportsRateLimitingRatherThanInventingFacts()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(403, string.Empty, "x-ratelimit-remaining: 0"),
        };

        var source = new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync);

        Assert.Null(await source.FetchAsync(4595, TestContext.Current.CancellationToken));
        Assert.True(source.RateLimited);
        Assert.Equal(0, source.RateLimitRemaining);
    }

    [Fact]
    public async Task BuildRows_SkipsWorkingPanesAndFetchesEachPrOnce()
    {
        // Two windows claiming one PR is a measured condition (#159), and the second window's question
        // has the same answer as the first's.
        var fetches = new List<int>();
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("night:1", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=none next=round-3"),
                Pane("night:2", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=none next=round-3"),
                Pane("night:3", "Working on PR 4600\n(esc to interrupt)", PaneActivity.Working),
            ],
            (pr, _) => { fetches.Add(pr); return Task.FromResult<PrFacts?>(null); },
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: false,
            TestContext.Current.CancellationToken);

        Assert.Equal([4595], fetches);
        Assert.Equal(2, rows.Count);
        Assert.DoesNotContain(rows, r => r.Pane.Target == "night:3");
    }

    [Fact]
    public async Task BuildRows_BlockedPaneNeedsAKeystrokeNotALookup()
    {
        var fetches = new List<int>();
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [Pane("night:1", "Do you want to proceed?\n(enter to confirm)", PaneActivity.Blocked)],
            (pr, _) => { fetches.Add(pr); return Task.FromResult<PrFacts?>(null); },
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: false,
            TestContext.Current.CancellationToken);

        Assert.Empty(fetches);
        Assert.Equal(WaitingState.NeedsOperator, Assert.Single(rows).Verdict.State);
    }

    [Fact]
    public async Task BuildRows_HidesLegitimateHoldsUntilAllIsAsked()
    {
        PrFacts holding = new()
        {
            Number = 4595,
            HeadSha = Head,
            State = "open",
            MergeableState = "clean",
            Checks = [new CheckRunFact("ci-required", "in_progress", null)],
        };

        TmuxPane[] panes = [Pane("night:1", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=check:ci-required next=round-3")];

        Assert.Empty(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(holding),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: false, ct: TestContext.Current.CancellationToken));
        Assert.Single(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(holding),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: true, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BuildRows_AWindowThatIdentifiesNothingSurfacesOnlyUnderAll()
    {
        TmuxPane[] panes = [Pane("night:1", "$ ", PaneActivity.Idle)];

        Assert.Empty(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: false, ct: TestContext.Current.CancellationToken));

        WaitingRow row = Assert.Single(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: true, ct: TestContext.Current.CancellationToken));
        Assert.Null(row.Record);
        Assert.Contains("no published state", row.Verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildRows_OrdersTheLongestWaitFirst()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("night:1", "", PaneActivity.Idle, now.AddMinutes(-20), agentState: "pr=1 waiting=none next=x"),
                Pane("night:2", "", PaneActivity.Idle, now.AddHours(-6), agentState: "pr=2 waiting=none next=x"),
                Pane("night:3", "", PaneActivity.Idle, now.AddMinutes(-90), agentState: "pr=3 waiting=none next=x"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null),
            now,
            all: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(["night:2", "night:3", "night:1"], rows.Select(r => r.Pane.Target));
        Assert.Equal(TimeSpan.FromHours(6), rows[0].StoppedFor);
    }

    [Fact]
    public async Task ScanAsync_TreatsAnUnreachableTmuxAsAFailureNotAnEmptyFleet()
    {
        // Reporting QUIET for both is how a silent tool gets mistaken for a quiet one.
        var scanner = new TmuxScanner(host: null, (_, _) => Task.FromResult(new CommandResult(1, string.Empty, "no server running")));

        TmuxUnavailableException ex = await Assert.ThrowsAsync<TmuxUnavailableException>(
            () => scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no server running", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_MarksAPaneUnreadableWhenTheCaptureFails()
    {
        // One command now carries every window and every capture, so a pane whose capture failed simply
        // contributes no lines — which reads as idle, and is why the batched form is parsed by marker.
        var scanner = new TmuxScanner(host: null, (script, _) => Task.FromResult(new CommandResult(
            0, Framed(script, ["%1|night:1|1|1755900000||pr4595"]), string.Empty)));

        TmuxPane pane = Assert.Single(await scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Equal("%1", pane.PaneId);
        Assert.Empty(pane.Capture.Trim());
    }

    [Fact]
    public void ParseCollection_TreatsAMarkerInPaneTextAsContent()
    {
        // Agent output quotes this tool's own source, so the marker does appear inside captures. Treating
        // it as a boundary would truncate the real window and invent one that does not exist.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"],
                ("%1", "the collector frames each window\nand this line mentions the framing")),
            host: null,
            Nonce);

        TmuxPane pane = Assert.Single(panes);
        Assert.Equal("pr4595", pane.WindowName);
        Assert.Contains("mentions the framing", pane.Capture, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCollection_APaneCannotInjectAWindow()
    {
        // The blocking finding: pane text is arbitrary content, and agents routinely print this tool's
        // own output. A forged row naming a real head with corroborating fields would otherwise be graded
        // high confidence and become eligible to act on — a verdict about a PR whose agent never spoke.
        string forged = $"{Nonce}:manifest\n%999|fake:9|1|1755900000|pr=9999 head=abc1234 reviews=2/2 rec=merge|pr9999\n{Nonce}:end";

        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"], ("%1", forged)),
            host: null,
            Nonce);

        // Metadata comes only from the manifest, which closed before any capture began.
        TmuxPane pane = Assert.Single(panes);
        Assert.Equal("%1", pane.PaneId);
        Assert.Null(pane.AgentStateOption);
    }

    [Fact]
    public void ParseCollection_APaneCannotReopenAnotherWindow()
    {
        // Even a leaked nonce buys nothing: a header may only select a known pane, and only once, so a
        // pane cannot append to or overwrite a neighbour's capture.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595", "%2|night:2|1|1755900000||pr4596"],
                ("%1", "real first pane"),
                ("%2", $"{Nonce}:pane %1\nsmuggled into the first window")),
            host: null,
            Nonce);

        Assert.Equal(2, panes.Count);
        Assert.DoesNotContain("smuggled", panes[0].Capture, StringComparison.Ordinal);
        Assert.Contains("smuggled", panes[1].Capture, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCollection_OutputWithoutThisRunsFramingIsNotSalvaged()
    {
        Assert.Empty(TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"]), host: null, "a-different-nonce"));
    }

    [Fact]
    public async Task ScanAsync_RunsExactlyOneCommandPerHost()
    {
        // The reason fan-out is viable: a host with twenty-two agent windows costs one connection, not
        // twenty-three.
        var calls = new List<string>();
        var scanner = new TmuxScanner(host: "fernie", (script, _) =>
        {
            calls.Add(script);
            return Task.FromResult(new CommandResult(0, Framed(script, Enumerable.Range(1, 22)
                .Select(i => $"%{i}|cp:{i}|1|1755900000||pr46{i:00}")), string.Empty));
        });

        IReadOnlyList<TmuxPane> panes = await scanner.ScanAsync(TestContext.Current.CancellationToken);

        Assert.Equal(22, panes.Count);
        Assert.Single(calls);
        Assert.All(panes, p => Assert.Equal("fernie", p.Host));
        Assert.Equal("fernie cp:1", panes[0].Where);
    }

    [Fact]
    public async Task FetchAsync_StopsSpendingOnceGitHubHasPushedBack()
    {
        var gh = new FakeGh { ["repos/o/r/pulls/1"] = Response(403, string.Empty, "x-ratelimit-remaining: 0") };
        var source = new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync);

        Assert.Null(await source.FetchAsync(1, TestContext.Current.CancellationToken));
        Assert.Null(await source.FetchAsync(2, TestContext.Current.CancellationToken));

        // Only the first call reaches gh; further requests cannot succeed and deepen the hole for every
        // other agent on the same budget.
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task FetchAsync_TruncatedChecksAreNotAnEmptyCheckSet()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(200, """
                {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
                """),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(200, """
                {"total_count":140,"check_runs":[{"name":"build","status":"completed","conclusion":"success"}]}
                """),
        };

        PrFacts? facts = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(4595, TestContext.Current.CancellationToken);

        Assert.NotNull(facts);
        Assert.False(facts.ChecksKnown);
    }

    [Fact]
    public async Task FetchAsync_KeepsOnlyTheNewestAttemptPerCheckName()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(200, """
                {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
                """),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(200, """
                {"total_count":2,"check_runs":[
                  {"name":"ci-required","status":"completed","conclusion":"failure","started_at":"2026-08-24T01:00:00Z"},
                  {"name":"ci-required","status":"completed","conclusion":"success","started_at":"2026-08-24T03:00:00Z"}]}
                """),
        };

        PrFacts? facts = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(4595, TestContext.Current.CancellationToken);

        // A rerun leaves the failed attempt in the response; reporting it is how an agent sits waiting on
        // a check that has already gone green.
        CheckRunFact check = Assert.Single(facts!.Checks);
        Assert.False(check.IsFailure);
    }

    /// <summary>Replays the nonce out of the script the scanner generated, so fakes frame correctly.</summary>
    private static string Framed(string script, IEnumerable<string> manifest)
    {
        string nonce = System.Text.RegularExpressions.Regex.Match(script, @"printf '([0-9a-f]{32}):manifest").Groups[1].Value;
        Assert.NotEmpty(nonce);
        return $"{nonce}:manifest\n" + string.Join('\n', manifest) + $"\n{nonce}:end\n";
    }

    [Fact]
    public async Task BuildRows_RanksTwoClaimsOnOnePrRatherThanRejectingEither()
    {
        // Observed live: PR 4448 claimed by a working window on one host and a blocked one on another.
        // Rejecting the second loses work that is really happening; treating them as equals gives two
        // owners and a fight. First registration owns it, the rest are followed.
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("cp:9", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=0/2", windowName: "pr4448"),
                Pane("cp:17", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 round=15 reviews=0/2", windowName: "pr4448"),
                Pane("cp:3", "", PaneActivity.Idle, agentState: "pr=4600 head=abc1234 reviews=0/2", windowName: "pr4600"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: true,
            ct: TestContext.Current.CancellationToken);

        WaitingRow[] contested = [.. rows.Where(r => r.Record?.PrNumber == 4448)];
        Assert.Equal(2, contested.Length);
        Assert.Single(contested, r => r.Claim.Rank == ClaimRank.Owner);
        Assert.Single(contested, r => r.Claim.Rank == ClaimRank.Follower);
        Assert.All(contested, r => Assert.Single(r.Claim.Others));

        // The uncontested one is unaffected.
        Assert.Equal(ClaimRank.Sole, rows.Single(r => r.Record?.PrNumber == 4600).Claim.Rank);
    }

    [Fact]
    public void Claim_OrdersByRegistrationNotByCollectionOrder()
    {
        // An owner that changes identity between sweeps is worse than no owner, so ranking is by when
        // each window first claimed the PR — remembered, not derived from this sweep's ordering.
        TmuxPane late = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane early = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(late, 4448, null), (early, 4448, null)],
            p => p.PaneId == early.PaneId ? t : t.AddHours(1));

        Assert.Equal(ClaimRank.Owner, ranked[Claim.Key(early)].Rank);
        Assert.Equal(ClaimBasis.Observed, ranked[Claim.Key(early)].Basis);
        Assert.True(ranked[Claim.Key(early)].OwnsClaim);
        Assert.Equal(ClaimRank.Follower, ranked[Claim.Key(late)].Rank);
    }

    [Fact]
    public void Claim_AWindowNeverSeenRegisteringSortsLast()
    {
        TmuxPane known = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane unknown = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(unknown, 4448, null), (known, 4448, null)],
            p => p.PaneId == known.PaneId ? DateTimeOffset.UnixEpoch : null);

        Assert.Equal(ClaimRank.Owner, ranked[Claim.Key(known)].Rank);
        Assert.Equal(ClaimRank.Follower, ranked[Claim.Key(unknown)].Rank);
    }

    [Fact]
    public void Claim_OwnershipNobodyWatchedIsNotOwnershipDecided()
    {
        // Rivals rarely appear in the same moment, so registration order is real — and unavailable to a
        // run that started after both. Guessing which agent began first and then driving it is a coin
        // toss whose losing side drives the agent that is not doing the work.
        TmuxPane senior = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane junior = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(junior, 4448, 1), (senior, 4448, 15)],
            _ => null);

        // Seniority still orders them, so the report names a likely owner...
        Assert.Equal(ClaimRank.Owner, ranked[Claim.Key(senior)].Rank);
        Assert.Equal(ClaimBasis.Inferred, ranked[Claim.Key(senior)].Basis);

        // ...but neither is entitled to be driven.
        Assert.False(ranked[Claim.Key(senior)].OwnsClaim);
        Assert.False(ranked[Claim.Key(junior)].OwnsClaim);
    }

    [Fact]
    public void ParseCollection_CarriesTheServerEpoch()
    {
        TmuxPane pane = Assert.Single(TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"]), host: null, Nonce));

        Assert.Equal("4242:1755900000", pane.Epoch);
    }

    [Fact]
    public void History_ForgetsAHostWhoseTmuxServerRestarted()
    {
        // Pane ids restart at %0 with the server, so keeping the old ones would attribute a departed
        // window's registration to whatever now holds its id — and present that as observed fact.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-epoch-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane before = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie", Epoch = "100:1" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            Assert.False(first.AdoptEpoch("fernie", "100:1", t));
            first.Observe(before, t, claimedPr: 4448);
            first.Save([before]);

            // Same pane id, new server: the registration must not survive.
            var second = new PaneHistory(path);
            Assert.False(second.AdoptEpoch("fernie", "200:2", t.AddHours(1)));
            Assert.Null(second.ClaimedAt(before));

            // An unchanged server keeps it, and reports the host as continuously swept.
            var third = new PaneHistory(path);
            second.Observe(before, t.AddHours(1), claimedPr: 4448);
            second.Save([before]);
            Assert.True(new PaneHistory(path).AdoptEpoch("fernie", "200:2", t.AddHours(2)));
            Assert.NotNull(new PaneHistory(path).ClaimedAt(before));
            _ = third;
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Claim_AWindowThatAppearedSinceTheLastSweepIsKnownToBeNewer()
    {
        // The common shape once the tool has been running: one claim was watched registering, the other
        // was not there at the last full sweep, so it can only have arrived afterwards.
        TmuxPane seen = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane fresh = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(fresh, 4448, null), (seen, 4448, null)],
            p => p.PaneId == seen.PaneId ? t : null,
            _ => t.AddMinutes(30));

        Assert.Equal(ClaimRank.Owner, ranked[Claim.Key(seen)].Rank);
        Assert.Equal(ClaimBasis.Observed, ranked[Claim.Key(seen)].Basis);
        Assert.True(ranked[Claim.Key(seen)].OwnsClaim);
    }

    [Fact]
    public void Claim_TwoWindowsBothUnseenCannotBeOrdered()
    {
        // Neither was watched registering, so nothing distinguishes them but a guess.
        TmuxPane a = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane b = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(a, 4448, 3), (b, 4448, 9)], _ => null, _ => DateTimeOffset.UnixEpoch);

        Assert.All(ranked.Values, c => Assert.Equal(ClaimBasis.Inferred, c.Basis));
        Assert.All(ranked.Values, c => Assert.False(c.OwnsClaim));
    }

    [Fact]
    public void Claim_ASoleClaimIsAlwaysItsOwnOwner()
    {
        TmuxPane only = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");

        Assert.True(Claim.Register([(only, 4448, null)], _ => null)[Claim.Key(only)].OwnsClaim);
    }

    [Fact]
    public async Task BuildRows_AContestedPrSurfacesEvenWhenNothingElseIsWrong()
    {
        // Both windows are legitimately in progress, so neither is an attention row on its own. The
        // contest is the finding, and it must not need --all to be seen.
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=0/2", windowName: "pr4448"),
                Pane("cp:2", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=0/2", windowName: "pr4448"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: false,
            ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public async Task BuildRows_AContestedPrIsNeverActedOnHoweverGoodItsEvidence()
    {
        // The fix for two agents on one PR is not to drive both of them carefully.
        PrFacts ready = new()
        {
            Number = 4448,
            HeadSha = "abc1234ff",
            State = "open",
            MergeableState = "clean",
            Checks = [new CheckRunFact("ci", "completed", "success")],
        };

        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448"),
                Pane("cp:2", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(ready),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: true,
            ct: TestContext.Current.CancellationToken);

        // Both rows are READY on identical evidence, and neither may be acted on: the follower because
        // it is a follower, the owner because this sweep is the first to see either of them, so which
        // registered first is inferred rather than known.
        Assert.All(rows, r => Assert.Equal(WaitingState.Ready, r.Verdict.State));
        Assert.All(rows, r => Assert.False(r.MayAct));
        Assert.All(rows, r => Assert.Equal(ClaimBasis.Inferred, r.Claim.Basis));
    }

    [Fact]
    public void WindowNaming_MarksAFollowerSoItIsVisibleInTheStatusBar()
    {
        // Forgetting the second window is as bad as driving it. The suffix keeps it present.
        var follower = new Claim(ClaimRank.Follower, [], null);

        Assert.Equal("follows", WindowNaming.SuffixFor(
            new(WaitingState.Ready, RowOwner.Operator, "reviews 2/2", Assurance.High), follower));
    }

    [Fact]
    public async Task CollectAsync_OneBadHostDoesNotStopTheOthers()
    {
        FleetScan scan = await FleetScan.CollectAsync(["nosuchbox"], TestContext.Current.CancellationToken);

        Assert.True(scan.TotalFailure);
        Assert.Single(scan.Unreachable);
    }

    [Fact]
    public void DigestBody_IgnoresTheFooterSoASpinnerIsNotProgress()
    {
        // Measured: a window advanced window_activity and changed on screen while its body was
        // byte-identical. Only the body distinguishes producing output from animating.
        const string Body = "● Round 3 is complete for PR 4616.\n  Fix description: authenticated every hop.";

        string first = TmuxScanner.DigestBody($"{Body}\n~/git/dotnet-inspect\n────────\n· Working (esc to interrupt) ⠋");
        string second = TmuxScanner.DigestBody($"{Body}\n~/git/dotnet-inspect\n────────\n· Working (esc to interrupt) ⠙");

        Assert.Equal(first, second);
    }

    [Fact]
    public void DigestBody_ChangesWhenTheAgentActuallyEmits()
    {
        string before = TmuxScanner.DigestBody("● Round 3 complete.\nfooter\nfooter\nfooter");
        string after = TmuxScanner.DigestBody("● Round 3 complete.\n● Round 4 starting.\nfooter\nfooter\nfooter");

        Assert.NotEqual(before, after);
    }

    [Theory]
    [InlineData("pr4448-blocked", "pr4448")]
    [InlineData("pr4448-merged", "pr4448")]
    [InlineData("pr4448", "pr4448")]
    [InlineData("tune-performance-triage", "tune-performance-triage")]
    public void WindowNaming_StripsOnlyItsOwnSuffixes(string name, string expected)
        => Assert.Equal(expected, WindowNaming.Strip(name));

    [Fact]
    public void WindowNaming_ReplacesAStaleSuffixRatherThanAppending()
    {
        // Three of six `-blocked` windows on one fleet had no prompt open. The suffix was believed and
        // wrong, which is worse than absent.
        Assert.Equal("pr4448-merged", WindowNaming.Apply("pr4448-blocked", "merged"));
        Assert.Equal("pr4448", WindowNaming.Apply("pr4448-blocked", null));
    }

    [Fact]
    public void WindowNaming_NeverPublishesALowConfidenceVerdictAsAName()
    {
        // A row can say "probably"; a name is read at a glance and believed.
        WaitingVerdict unsure = new(WaitingState.Ready, RowOwner.Operator, "reviews 2/2", Assurance.Low("contradicts itself"));

        Assert.Null(WindowNaming.SuffixFor(unsure));
        Assert.Equal("ready", WindowNaming.SuffixFor(new(WaitingState.Ready, RowOwner.Operator, "reviews 2/2", Assurance.High)));
    }

    [Fact]
    public void History_ReportsAWindowThatDepartedRatherThanPruningItSilently()
    {
        // A window vanishing is an event: an agent finished and reclaimed, one that crashed, or a
        // session killed by hand. Pruning it quietly makes all three the same nothing.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-depart-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane a = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
            TmuxPane b = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4600");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.Observe(a, t, claimedPr: 4448);
            first.Observe(b, t, claimedPr: 4600);
            Assert.Empty(first.Save([a, b], [null]));

            var second = new PaneHistory(path);
            second.Observe(a, t.AddMinutes(10), claimedPr: 4448);
            string gone = Assert.Single(second.Save([a], [null]));

            Assert.Contains("#4600", gone, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AWindowOnAnUnreachableHostHasNotDeparted()
    {
        // It is unseen, not gone. Forgetting it would manufacture a departure on every failed sweep.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-unseen-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane onFernie = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.Observe(onFernie, t, claimedPr: 4448);
            first.Save([onFernie], ["fernie"]);

            // A sweep that only reached merritt must not conclude the fernie window is gone.
            var second = new PaneHistory(path);
            Assert.Empty(second.Save([], ["merritt"]));
            Assert.NotNull(second.ClaimedAt(onFernie));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("pr=1 head=abc1234 reviews=2/2 rec=done", true)]
    [InlineData("pr=1 head=abc1234 reviews=2/2 rec=merge", false)]
    public void Retirement_ReadsTheAgentsOwnReportOfBeingFinished(string record, bool retirable)
    {
        // `done` is a report, not a request: the work is finished, so what it asks for is not a decision
        // but a reclamation. `merge` is still asking for something.
        AgentState state = AgentState.Parse(record, "pr1")!;
        WaitingVerdict verdict = new(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High);

        Assert.Equal(retirable, Retirement.For(verdict, state, PaneActivity.Idle).IsRetirable);
    }

    [Fact]
    public void Retirement_AWorkingWindowIsNeverRetirableWhateverItLastPublished()
    {
        AgentState state = AgentState.Parse("pr=1 head=abc1234 reviews=2/2 rec=done", "pr1")!;
        WaitingVerdict merged = new(WaitingState.Merged, RowOwner.Operator, "merged", Assurance.High);

        Assert.False(Retirement.For(merged, state, PaneActivity.Working).IsRetirable);
    }

    [Fact]
    public void Retirement_AdvisesClearingTheContextNotKillingTheWindow()
    {
        // The window and its session are worth keeping; a transcript of work that already merged is not.
        WaitingVerdict merged = new(WaitingState.Merged, RowOwner.Operator, "merged", Assurance.High);
        Retirement retirement = Retirement.For(merged, null, PaneActivity.Idle);

        Assert.True(retirement.IsRetirable);
        Assert.Contains("clear the context", retirement.Advice, StringComparison.Ordinal);
        Assert.Contains("reuse the window", retirement.Advice, StringComparison.Ordinal);
    }

    private static TmuxPane Pane(string target, string capture, PaneActivity activity, DateTimeOffset? lastActivity = null, string? agentState = null, string windowName = "w")
        => new()
        {
            PaneId = "%" + target.GetHashCode().ToString("x", System.Globalization.CultureInfo.InvariantCulture),
            Host = null,
            Target = target,
            AgentStateOption = agentState,
            WindowName = windowName,
            SessionAttached = true,
            LastActivity = lastActivity,
            Activity = activity,
            Capture = capture,
        };

    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    /// <summary>A gh stand-in that answers by API path and records what it was asked.</summary>
    private sealed class FakeGh : Dictionary<string, string>
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<GhResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Requests.Add(args);
            string path = args.Count > 1 ? args[1] : string.Empty;
            return Task.FromResult(TryGetValue(path, out string? response)
                ? new GhResult(0, response, string.Empty)
                : new GhResult(1, string.Empty, "not found (HTTP 404)"));
        }
    }

    private sealed class FakeCache : IConditionalCache
    {
        private readonly Dictionary<string, (string? ETag, string Body)> _entries = [];

        public (string? ETag, string? Body) Get(string path)
            => _entries.TryGetValue(path, out (string? ETag, string Body) entry) ? (entry.ETag, entry.Body) : (null, null);

        public void Put(string path, string? etag, string body) => _entries[path] = (etag, body);
    }
}
