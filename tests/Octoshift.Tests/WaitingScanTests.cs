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

    [Fact]
    public void ParseWindows_ReadsTargetAttachmentAndActivity()
    {
        IReadOnlyList<TmuxPane> windows = TmuxScanner.ParseWindows(
            "%1|night:3|1|1755900000|pr=4595 head=abc1234 reviews=2/2 rec=merge|pr4595\n%2|night:4|0|1755800000||i158\n");

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
    public void ParseWindows_KeepsAPipeInTheWindowName()
    {
        // Window name is formatted last precisely so a separator inside it cannot shift earlier fields.
        IReadOnlyList<TmuxPane> windows = TmuxScanner.ParseWindows("%7|night:3|1|1755900000||pr4595|round2");

        TmuxPane window = Assert.Single(windows);
        Assert.Equal("night:3", window.Target);
        Assert.Equal("pr4595|round2", window.WindowName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage\nmalformed row")]
    [InlineData("night:3|1|1755900000||name")]
    [InlineData("%1|night:3|1|1755900000")]
    public void ParseWindows_DropsMalformedRows(string stdout)
        => Assert.Empty(TmuxScanner.ParseWindows(stdout));

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
        var scanner = new TmuxScanner((_, _) => Task.FromResult(new CommandResult(1, string.Empty, "no server running")));

        TmuxUnavailableException ex = await Assert.ThrowsAsync<TmuxUnavailableException>(
            () => scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Contains("no server running", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_MarksAPaneUnreadableWhenTheCaptureFails()
    {
        var scanner = new TmuxScanner((args, _) => Task.FromResult(
            args[0] == "list-windows"
                ? new CommandResult(0, "%1|night:1|1|1755900000||pr4595\n", string.Empty)
                : new CommandResult(1, string.Empty, "pane not found")));

        TmuxPane pane = Assert.Single(await scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Equal(PaneActivity.Unreadable, pane.Activity);
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

    private static TmuxPane Pane(string target, string capture, PaneActivity activity, DateTimeOffset? lastActivity = null, string? agentState = null, string windowName = "w")
        => new()
        {
            PaneId = "%" + target.GetHashCode().ToString("x", System.Globalization.CultureInfo.InvariantCulture),
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
