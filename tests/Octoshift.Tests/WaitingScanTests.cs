namespace Octoshift.Tests;

using System.Globalization;
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

    /// <summary>
    /// Builds a collection stream the way the script emits one: manifest, then framed captures. A null
    /// capture text is a pane whose <c>capture-pane</c> failed — headed, then closed as lost.
    /// </summary>
    /// <remarks>
    /// Manifest rows are written here in the readable pipe form and encoded field by field, exactly as
    /// the script's <c>od</c> pipeline does, so no fixture can accidentally exercise a shape the collector
    /// never produces. A fixture that says nothing about captures still gets one complete, empty frame per
    /// row: every listed pane must be spoken for, and a stream that skips one is a failure now.
    /// </remarks>
    private static string Stream(IEnumerable<string> manifest, params (string PaneId, string? Text)[] captures)
    {
        string[] rows = [.. manifest];
        var sb = new System.Text.StringBuilder();
        sb.Append(Nonce).Append(":epoch 4242:1755900000\n");
        sb.Append(Nonce).Append(":manifest\n");
        foreach (string row in rows)
        {
            sb.Append(Row(row)).Append('\n');
        }

        sb.Append(Nonce).Append(":end\n");

        (string PaneId, string? Text)[] frames = captures.Length > 0
            ? captures
            : [.. rows.Select(row => (row.Split('|')[0], (string?)string.Empty))];

        foreach ((string paneId, string? text) in frames)
        {
            sb.Append(Nonce).Append(":pane ").Append(paneId).Append('\n');
            if (text is null)
            {
                sb.Append(Nonce).Append(":lost ").Append(paneId).Append('\n');
                continue;
            }

            sb.Append(Hex(text)).Append('\n').Append(Nonce).Append(":read ").Append(paneId).Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Encodes one field the way the script's <c>printf | od | tr</c> pipeline does.</summary>
    private static string Hex(string text) => Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>One manifest row, from the readable pipe form to the six encoded fields on the wire.</summary>
    private static string Row(string fields, string nonce = Nonce)
        => $"{nonce}:w|" + string.Join('|', fields.Split('|', 6).Select(Hex));

    /// <summary>A row built field by field, for fixtures whose values contain the separator itself.</summary>
    private static string EncodedRow(params string[] fields)
        => $"{Nonce}:w|" + string.Join('|', fields.Select(Hex));

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

    [Fact]
    public void ParseCollection_ANewlineInTheStateCannotSplitARow()
    {
        // The blocking finding, verbatim: an agent published an `@agent_state` containing a newline, the
        // row tore in two, both halves failed to parse, both were dropped — and a host with a live window
        // reported QUIET and exited 0. Encoding is what makes this impossible: a value cannot reach the
        // framing, so there is no row to split and nothing to drop. The value here carries the separator
        // and the manifest marker too, because a value that can hold a newline can hold those as well.
        const string hostile = "pr=4595 head=abc1234\ndeadbeefcafe0123:manifest\n%9|fake:9|1|1|pr=9999|pr9999";

        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", hostile, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce);

        TmuxPane pane = Assert.Single(panes);
        Assert.Equal("%1", pane.PaneId);
        Assert.Equal(hostile, pane.AgentStateOption);
        Assert.Equal("pr4595", pane.WindowName);
    }

    [Fact]
    public void ParseCollection_ControlCharactersInAWindowNameCannotSplitARow()
    {
        // A window name is arbitrary text too, and it was the last field precisely because it used to be
        // the only one a separator could not shift. Encoded, none of them can.
        const string hostile = "pr4595\r\n%9|fake:9|1|1||forged\u0007and still the name";

        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, hostile)}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce);

        TmuxPane pane = Assert.Single(panes);
        Assert.Equal(hostile, pane.WindowName);
        Assert.Null(pane.AgentStateOption);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("%1|night:3|1|1755900000||name")]                         // the old unencoded row shape
    [InlineData("deadbeefcafe0123:w|2531|6e69676874")]                    // too few fields
    [InlineData("deadbeefcafe0123:w|2531|6e|31|31|31|31|31")]             // too many
    [InlineData("deadbeefcafe0123:w|2531|nothex|31|31|31|31")]            // not encoded
    [InlineData("deadbeefcafe0123:w|2531|616|31|31|31|31")]               // truncated mid-byte
    [InlineData("deadbeefcafe0123:w|6e69676874|6e|31|31|31|31")]          // first field is not a pane id
    public void ParseCollection_RejectsAMalformedManifestRow(string row)
    {
        // Dropping a row loses a window, and a lost window is indistinguishable from a window that is not
        // there — which is the whole failure. A manifest that does not decode is the host being
        // unreadable, and it is reported as that.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{row}\n{Nonce}:end\n", host: null, Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsARepeatedManifestRow()
    {
        // Two rows for one pane are two accounts of one window; taking either is a guess about which the
        // host meant, and the second would silently overwrite the first.
        TmuxUnavailableException ex = Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Row("%1|night:2|1|1755900000||pr9999")}\n{Nonce}:end\n",
            host: null,
            Nonce));

        Assert.Contains("%1", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCollection_APaneWithNoCaptureFrameIsAFailure()
    {
        // A manifest row with no frame after it is a collection that stopped early. Left non-fatal, the
        // window is reported on evidence that never arrived — and an empty capture reads as idle, which
        // is the one state a published record is acted on in.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n", host: null, Nonce));
    }

    [Fact]
    public void ParseCollection_AnUnclosedCaptureFrameIsAFailure()
    {
        // The connection dropped mid-capture. What arrived is a partial screen, and classifying activity
        // from a partial screen is reading a footer that is not the footer.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("half a screen")}\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_ACaptureFrameThatNeverOpenedIsAFailure()
    {
        // A close with no header is the shape a pane would forge to hand itself back as read. It is not a
        // shape the script writes, so the collection is not the host's.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_ARepeatedCaptureFrameIsAFailure()
    {
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n"
                + $"{Nonce}:pane %1\n{Hex("(esc to interrupt)")}\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_ACaptureOfAPaneTheManifestNeverListedIsAFailure()
    {
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n"
                + $"{Nonce}:pane %9\n{Hex("> ")}\n{Nonce}:read %9\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_ContentBetweenFramesIsAFailure()
    {
        // Every capture is encoded, so there is no legitimate free text anywhere past the manifest.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\nConnection to fernie closed.\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_AnExplicitLostFrameIsTheOnlyForgivableMissingCapture()
    {
        // The distinction the whole frame exists to draw: a pane the host said it could not read is a row
        // that cannot be graded, while a pane the host said nothing about is a collection that failed.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"], ("%1", null)), host: null, Nonce);

        Assert.Equal(PaneActivity.Unreadable, Assert.Single(panes).Activity);
    }

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
                Pane("night:1", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=review"),
                Pane("night:2", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=review"),
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
    public async Task BuildRows_AnUnreadablePaneIsReportedButNeverActionable()
    {
        // The record here is the strongest one the contract allows — head, a clean 2/2, rec=merge — and
        // on an idle pane it resolves Ready and high. Unread, it must not: the capture is the only
        // evidence the agent actually stopped, and nobody has it.
        var fetches = new List<int>();
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [Pane("night:1", string.Empty, PaneActivity.Unreadable, agentState: $"pr=4595 head={Head} reviews=2/2 rec=merge")],
            (pr, _) => { fetches.Add(pr); return Task.FromResult<PrFacts?>(null); },
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: false,
            TestContext.Current.CancellationToken);

        WaitingRow row = Assert.Single(rows);
        Assert.Equal(WaitingState.Unknown, row.Verdict.State);
        Assert.False(row.Verdict.MayAct);

        // Still surfaced, and still identified: the window options came from the manifest, which is sound.
        Assert.True(row.Verdict.NeedsAttention);
        Assert.Equal(4595, row.Record?.PrNumber);

        // And no budget spent asking GitHub about a pane whose own state could not be read.
        Assert.Empty(fetches);
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

        TmuxPane[] panes = [Pane("night:1", "", PaneActivity.Idle, agentState: "pr=4595 head=722512e25 waiting=check:ci-required")];

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
        Assert.Null(row.Unidentified);
        Assert.Contains("no published state", row.Verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BuildRows_AnEmptyOptionIsStillJustAnEmptyShell(string option)
    {
        // Publishing an empty string is not publishing a record, so this stays where an idle shell has
        // always been: out of the default view, available under --all.
        TmuxPane[] panes = [Pane("night:1", "$ ", PaneActivity.Idle, agentState: option)];

        Assert.Empty(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: false, ct: TestContext.Current.CancellationToken));

        WaitingRow row = Assert.Single(await WaitingCommand.BuildRowsAsync(panes, (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null), DateTimeOffset.UtcNow, all: true, ct: TestContext.Current.CancellationToken));
        Assert.Null(row.Unidentified);
        Assert.Equal(WaitingState.Unknown, row.Verdict.State);
    }

    [Fact]
    public async Task BuildRows_APublishedStateThatNamesNothingIsSeenWithoutAll()
    {
        // The blocking finding, end to end: an idle window named `worker` publishing `pr=none head=pending
        // rec=stop`. Nothing identifies it, so it used to be filtered to --all along with the empty shells
        // — an agent asking to be released, reported as a quiet fleet.
        var fetches = new List<int>();
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [Pane("night:1", "$ ", PaneActivity.Idle, agentState: "pr=none head=pending rec=stop", windowName: "worker")],
            (pr, _) => { fetches.Add(pr); return Task.FromResult<PrFacts?>(null); },
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: false,
            TestContext.Current.CancellationToken);

        WaitingRow row = Assert.Single(rows);
        Assert.True(row.Verdict.NeedsAttention);
        Assert.Equal(WaitingState.NeedsOperator, row.Verdict.State);
        Assert.Contains("stop", row.Verdict.Reason, StringComparison.Ordinal);

        // No identity, so no number was invented and nothing was asked of GitHub about one.
        Assert.Null(row.Record);
        Assert.Equal(Recommendation.Stop, row.Unidentified?.Recommendation);
        Assert.Empty(fetches);

        // And the grammar defects travel with it rather than being repaired away.
        Assert.Contains(row.Defects, d => d.Contains("pr=none", StringComparison.Ordinal));
        Assert.Contains(row.Defects, d => d.Contains("head=pending", StringComparison.Ordinal));
        Assert.False(row.Verdict.MayAct);
    }

    [Fact]
    public async Task BuildRows_OrdersTheLongestWaitFirst()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("night:1", "", PaneActivity.Idle, now.AddMinutes(-20), agentState: "pr=1 waiting=review"),
                Pane("night:2", "", PaneActivity.Idle, now.AddHours(-6), agentState: "pr=2 waiting=review"),
                Pane("night:3", "", PaneActivity.Idle, now.AddMinutes(-90), agentState: "pr=3 waiting=review"),
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
        // One command carries every window and every capture, so a pane whose capture failed contributes
        // no lines — which would read as idle, the one state a published record is acted on in. The
        // script closes each capture with its own marker so "nothing was captured" is said, not inferred.
        var scanner = new TmuxScanner(host: null, (script, _) => Task.FromResult(new CommandResult(
            0, Framed(script, ["%1|night:1|1|1755900000||pr4595"], ("%1", null)), string.Empty)));

        TmuxPane pane = Assert.Single(await scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Equal("%1", pane.PaneId);
        Assert.Equal(PaneActivity.Unreadable, pane.Activity);
        Assert.Empty(pane.Capture);
    }

    [Fact]
    public void ParseCollection_AFailedCaptureIsUnreadableRatherThanIdle()
    {
        // A pane that closed between enumeration and capture is still a row — it just cannot be graded.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595", "%2|night:2|1|1755900000||pr4596"],
                ("%1", null),
                ("%2", "Round 2 is complete.\n\n> ")),
            host: null,
            Nonce);

        Assert.Equal(2, panes.Count);
        Assert.Equal(PaneActivity.Unreadable, panes[0].Activity);
        Assert.Equal(PaneActivity.Idle, panes[1].Activity);
    }

    [Fact]
    public void ParseCollection_RejectsAnOutOfRangeActivityTimestamp()
    {
        string row = $"%1|night:1|1|{long.MaxValue.ToString(CultureInfo.InvariantCulture)}||pr4595";
        TmuxUnavailableException error = Assert.Throws<TmuxUnavailableException>(
            () => TmuxScanner.ParseCollection(Stream([row], ("%1", "> ")), host: "fernie", Nonce));

        Assert.Contains("out-of-range window activity", error.Message, StringComparison.Ordinal);
        Assert.Contains("fernie", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("9223372036854775808")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("not-a-timestamp")]
    public void ParseCollection_RejectsAnUnparseableActivityTimestamp(string activity)
    {
        string row = $"%1|night:1|1|{activity}||pr4595";
        TmuxUnavailableException error = Assert.Throws<TmuxUnavailableException>(
            () => TmuxScanner.ParseCollection(Stream([row], ("%1", "> ")), host: "fernie", Nonce));

        Assert.Contains("out-of-range window activity", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseCollection_APaneCannotDeclareANeighbourReadable()
    {
        // Unreadable is a protective classification, so nothing in a capture may lift it from another
        // pane. Encoding settles it outright — a marker in pane text is bytes inside one field — and the
        // marker that does close a frame may only name the pane it closes.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595", "%2|night:2|1|1755900000||pr4596"],
                ("%1", null),
                ("%2", $"{Nonce}:read %1\n> ")),
            host: null,
            Nonce);

        Assert.Equal(PaneActivity.Unreadable, panes[0].Activity);
        Assert.Empty(panes[0].Capture);
        Assert.Contains(":read %1", panes[1].Capture, StringComparison.Ordinal);
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
    public void ParseCollection_APaneCannotOpenTheNextWindowsSection()
    {
        // The script closes every capture before heading the next one, so a header arriving mid-capture
        // is content by definition — and treating it otherwise would let one pane write into another's.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595", "%2|night:2|1|1755900000||pr4596"],
                ("%1", $"real first pane\n{Nonce}:pane %2\nsmuggled into the second window"),
                ("%2", "> ")),
            host: null,
            Nonce);

        Assert.Equal(2, panes.Count);
        Assert.Contains("smuggled", panes[0].Capture, StringComparison.Ordinal);
        Assert.DoesNotContain("smuggled", panes[1].Capture, StringComparison.Ordinal);
        Assert.Equal(PaneActivity.Idle, panes[1].Activity);
    }

    [Fact]
    public void ParseCollection_OutputWithoutThisRunsFramingIsAFailureNotAQuietHost()
    {
        // Wrong nonce means the bytes are not this collection's, whatever they parse as.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            Stream(["%1|night:1|1|1755900000||pr4595"]), host: null, "a-different-nonce"));
    }

    [Fact]
    public void ParseCollection_AnEmptyManifestIsAQuietHostNotAFailure()
    {
        // The one exit-0-with-no-rows case that is real: a tmux server with no windows. It has to stay
        // distinguishable from the framing failures, or the distinction buys nothing.
        Assert.Empty(TmuxScanner.ParseCollection($"{Nonce}:manifest\n\n{Nonce}:end\n", host: null, Nonce));
    }

    [Fact]
    public void ParseCollection_ATruncatedManifestIsAFailure()
    {
        // A connection dropped mid-manifest yields rows that are real but incomplete. Reporting the ones
        // that arrived would silently shrink the fleet.
        TmuxUnavailableException ex = Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Row("%1|night:1|1|1755900000||pr4595")}\n", host: "fernie", Nonce));

        Assert.StartsWith("fernie:", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_ExitZeroWithoutTheCollectionIsUnavailableNotAQuietFleet()
    {
        // What `--host=-V` produced: ssh answered, exited 0, and never ran the script. Reported as a
        // quiet fleet, that is a sweep that saw nothing claiming there was nothing to see.
        var scanner = new TmuxScanner(host: "fernie", (_, _) => Task.FromResult(
            new CommandResult(0, "OpenSSH_9.9p1, LibreSSL 3.3.6\n", string.Empty)));

        TmuxUnavailableException ex = await Assert.ThrowsAsync<TmuxUnavailableException>(
            () => scanner.ScanAsync(TestContext.Current.CancellationToken));

        Assert.Contains("fernie", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanAsync_AnEmptyManifestReportsNoWindowsWithoutFailing()
    {
        var scanner = new TmuxScanner(host: null, (script, _) => Task.FromResult(
            new CommandResult(0, Framed(script, []), string.Empty)));

        Assert.Empty(await scanner.ScanAsync(TestContext.Current.CancellationToken));
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
    public async Task Collect_RepeatedHostsCostOneConnectionAndOneSetOfRows()
    {
        // Naming an alias twice is a typo. Honouring it would buy a second ssh connection and a duplicate
        // of every row and count that host contributes.
        var scanned = new List<string?>();

        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff", "fernie"],
            (host, _) =>
            {
                scanned.Add(host);
                return Task.FromResult<IReadOnlyList<TmuxPane>>([Pane($"{host}:1", string.Empty, PaneActivity.Idle)]);
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(["fernie", "banff"], scanned);
        Assert.Equal(["fernie:1", "banff:1"], collected.Panes.Select(p => p.Target));
        Assert.Equal(2, collected.Targets);
        Assert.False(collected.AnyFailure);
    }

    [Fact]
    public async Task Collect_OneUnreachableHostKeepsEveryOtherHostsRows()
    {
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            (host, _) => host == "fernie"
                ? throw new TmuxUnavailableException("fernie: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([Pane("banff:1", string.Empty, PaneActivity.Idle)]),
            TestContext.Current.CancellationToken);

        Assert.Equal("banff:1", Assert.Single(collected.Panes).Target);
        Assert.Equal("fernie: no server running", Assert.Single(collected.Unreachable));

        // Partial, so the rows still print — but the sweep was not clean, and the exit code says so.
        Assert.False(collected.TotalFailure);
        Assert.True(collected.AnyFailure);
    }

    [Fact]
    public async Task Collect_EveryHostFailingIsATotalFailureNotAQuietFleet()
    {
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            (host, _) => throw new TmuxUnavailableException($"{host}: no server running"),
            TestContext.Current.CancellationToken);

        Assert.True(collected.TotalFailure);
        Assert.Equal(2, collected.Unreachable.Count);
    }

    [Fact]
    public async Task Collect_AHostWithNoWindowsIsQuietRatherThanUnreachable()
    {
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie"],
            (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([]),
            TestContext.Current.CancellationToken);

        Assert.False(collected.TotalFailure);
        Assert.False(collected.AnyFailure);
    }

    [Fact]
    public async Task Collect_NoHostsMeansThisMachine()
    {
        var scanned = new List<string?>();

        await WaitingCommand.CollectAsync([], (host, _) =>
        {
            scanned.Add(host);
            return Task.FromResult<IReadOnlyList<TmuxPane>>([]);
        }, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(scanned));
    }

    [Fact]
    public async Task Collect_ATargetThatHangsPastItsDeadlineIsUnreachableAndLaterTargetsStillRun()
    {
        // The first target connects and then never answers — the failure ssh's ConnectTimeout cannot see.
        // Its own deadline, not the whole sweep dying, is what ends it, and the second target is still read.
        var scanned = new List<string?>();

        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            async (host, token) =>
            {
                scanned.Add(host);
                if (host == "fernie")
                {
                    // Completes only when the token fires, so `banff` is reached exactly when the
                    // per-target deadline trips — no wall-clock race decides the outcome.
                    await Task.Delay(Timeout.Infinite, token);
                }

                return [Pane("banff:1", string.Empty, PaneActivity.Idle)];
            },
            TestContext.Current.CancellationToken,
            perTargetTimeout: TimeSpan.FromMilliseconds(20));

        Assert.Equal(["fernie", "banff"], scanned);
        Assert.Equal("banff:1", Assert.Single(collected.Panes).Target);
        Assert.Contains("fernie", Assert.Single(collected.Unreachable));

        // A timeout on one host and a good read on another is partial, never total — the rows still print.
        Assert.False(collected.TotalFailure);
        Assert.True(collected.AnyFailure);
    }

    [Fact]
    public async Task Collect_CallerCancellationPropagatesRatherThanBecomingAnUnreachableHost()
    {
        // A generous per-target deadline, so the only thing that ends the scan is the caller's own token.
        // That is a real cancellation and must surface, not be laundered into an unreachable host.
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            async (host, token) =>
            {
                cts.Cancel();
                await Task.Delay(Timeout.Infinite, token);
                return [Pane("fernie:1", string.Empty, PaneActivity.Idle)];
            },
            cts.Token,
            perTargetTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Collect_CallerCancellationDominatesAConcurrentTmuxFailure()
    {
        // The caller cancels and the very same scan loses tmux — a real race at shutdown. Cancellation
        // dominates: the sweep must escape as an OperationCanceledException, never fold the loss into a
        // quietly completed collection where an unreachable host stands in for a cancelled run.
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            (host, token) =>
            {
                cts.Cancel();
                throw new TmuxUnavailableException($"{host}: no server running");
            },
            cts.Token,
            perTargetTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Collect_CallerCancellationEscapesCarryingTheCallersOwnToken()
    {
        // Each target runs under a linked token, so an OperationCanceledException raised inside the scan
        // carries that linked token. The caller is owed exactly the token it passed in — the escaping
        // exception must carry ct, not the internal linked token, or a `when (e.CancellationToken == ct)`
        // handler upstream would fail to recognise its own cancellation.
        using var cts = new CancellationTokenSource();

        OperationCanceledException oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WaitingCommand.CollectAsync(
                ["fernie", "banff"],
                async (host, token) =>
                {
                    cts.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                    return [Pane("fernie:1", string.Empty, PaneActivity.Idle)];
                },
                cts.Token,
                perTargetTimeout: TimeSpan.FromSeconds(30)));

        Assert.Equal(cts.Token, oce.CancellationToken);
    }

    [Fact]
    public async Task Collect_CancellationAfterTheFinalCallbackButBeforeReturnStillPropagates()
    {
        // The last scan completes cleanly, then the caller cancels before CollectAsync returns. A report
        // that finished a hair before the token fired is still a cancelled run — surface it rather than
        // hand back a completed collection assembled under a token that is now cancelled.
        using var cts = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WaitingCommand.CollectAsync(
            ["banff"],
            (host, token) =>
            {
                cts.Cancel();
                return Task.FromResult<IReadOnlyList<TmuxPane>>(
                    [Pane("banff:1", string.Empty, PaneActivity.Idle)]);
            },
            cts.Token,
            perTargetTimeout: TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task Collect_ATargetTimeoutStaysUnreachableWhileTheCallerTokenIsUntouched()
    {
        // The per-target deadline — never the caller — ends the first target. With ct never cancelled,
        // that OperationCanceledException must stay laundered into an unreachable host, and the later
        // target must still run: caller-cancellation handling must not swallow a plain timeout.
        var scanned = new List<string?>();

        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            async (host, token) =>
            {
                scanned.Add(host);
                if (host == "fernie")
                {
                    await Task.Delay(Timeout.Infinite, token);
                }

                return [Pane("banff:1", string.Empty, PaneActivity.Idle)];
            },
            TestContext.Current.CancellationToken,
            perTargetTimeout: TimeSpan.FromMilliseconds(20));

        Assert.Equal(["fernie", "banff"], scanned);
        Assert.Equal("banff:1", Assert.Single(collected.Panes).Target);
        Assert.Contains("fernie", Assert.Single(collected.Unreachable));
        Assert.Contains("timed out", Assert.Single(collected.Unreachable));
    }

    [Fact]
    public async Task Collect_EveryTargetTimingOutIsATotalFailureNamingEachTarget()
    {
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return (IReadOnlyList<TmuxPane>)[];
            },
            TestContext.Current.CancellationToken,
            perTargetTimeout: TimeSpan.FromMilliseconds(20));

        Assert.True(collected.TotalFailure);
        Assert.Equal(2, collected.Unreachable.Count);
        Assert.Contains("fernie", collected.Unreachable[0]);
        Assert.Contains("banff", collected.Unreachable[1]);
        Assert.All(collected.Unreachable, m => Assert.Contains("timed out", m));
    }

    [Fact]
    public async Task Collect_ALocalScanThatTimesOutNamesTheLocalMachine()
    {
        // No hosts means this machine, and its timeout message has no alias to carry, so it must say so
        // itself rather than print a bare, sourceless "timed out".
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            [],
            async (_, token) =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return (IReadOnlyList<TmuxPane>)[];
            },
            TestContext.Current.CancellationToken,
            perTargetTimeout: TimeSpan.FromMilliseconds(20));

        Assert.True(collected.TotalFailure);
        Assert.Contains("local", Assert.Single(collected.Unreachable));
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
    private static string NonceOf(string script)
    {
        string nonce = System.Text.RegularExpressions.Regex.Match(script, @"printf '([0-9a-f]{32}):manifest").Groups[1].Value;
        Assert.NotEmpty(nonce);
        return nonce;
    }

    /// <summary>A whole collection, framed with the nonce the scanner actually generated.</summary>
    private static string Framed(string script, IEnumerable<string> manifest, params (string PaneId, string? Text)[] captures)
    {
        string nonce = NonceOf(script);
        string[] rows = [.. manifest];
        var sb = new System.Text.StringBuilder();
        sb.Append(nonce).Append(":manifest\n");
        foreach (string row in rows)
        {
            sb.Append(Row(row, nonce)).Append('\n');
        }

        sb.Append(nonce).Append(":end\n");

        (string PaneId, string? Text)[] frames = captures.Length > 0
            ? captures
            : [.. rows.Select(row => (row.Split('|')[0], (string?)string.Empty))];

        foreach ((string paneId, string? text) in frames)
        {
            sb.Append(nonce).Append(":pane ").Append(paneId).Append('\n');
            sb.Append(text is null ? string.Empty : Hex(text) + "\n")
              .Append(nonce).Append(text is null ? ":lost " : ":read ").Append(paneId).Append('\n');
        }

        return sb.ToString();
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
    public async Task BuildRows_EveryActivityClaimantContestsNotOnlyIdleOnes()
    {
        // The blocking finding: a working window and a blocked window each claim PR 4448 alongside an idle
        // one. All three hold the same claim; leaving the two busy ones out of the contest would hand the
        // idle rival sole, actionable ownership of a PR three agents are on. Distinct names, so identity
        // comes from the published state rather than the window name.
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
                Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "a"),
                Pane("cp:2", "mid turn", PaneActivity.Working, agentState: "pr=4448 head=abc1234", windowName: "b"),
                Pane("cp:3", "answer? (esc to cancel)", PaneActivity.Blocked, agentState: "pr=4448 head=abc1234", windowName: "c"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(ready),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: true,
            ct: TestContext.Current.CancellationToken);

        WaitingRow idle = rows.Single(r => r.Pane.Target == "cp:1");
        Assert.True(idle.Claim.IsContested);

        // Both the working and the blocked window contest it, so its rivals number two, not zero.
        Assert.Equal(2, idle.Claim.Others.Count);

        // And it is never acted on, however good its evidence — the fix for three agents on one PR is not
        // to drive one of them carefully.
        Assert.False(idle.MayAct);
    }

    [Fact]
    public async Task CollectAsync_AHostThatAnswersEmptyIsCollectedNotOmitted()
    {
        // A host that answered with no windows is evidence it was observed, not a host that was skipped.
        // It must appear in CollectedHosts so a quiet host still counts toward a complete view.
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            (host, _) => host == "banff"
                ? Task.FromResult<IReadOnlyList<TmuxPane>>([Pane("banff:1", "", PaneActivity.Idle)])
                : Task.FromResult<IReadOnlyList<TmuxPane>>([]),
            TestContext.Current.CancellationToken);

        Assert.False(collected.AnyFailure);
        Assert.Contains("fernie", collected.CollectedHosts);
        Assert.Contains("banff", collected.CollectedHosts);
    }

    [Fact]
    public void History_PartialSweepPrunesOnlyCollectedHostsAndRetainsTheRest()
    {
        // On a partial collection only the successfully collected hosts' partitions are updated. A window
        // that vanished from a collected host has departed; a window on a host not swept this run is merely
        // unseen, and its registration must survive rather than being deleted.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-partial-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane onFernie = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie" };
            TmuxPane onBanff = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4600") with { Host = "banff" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.Observe(onFernie, t, claimedPr: 4448);
            first.Observe(onBanff, t, claimedPr: 4600);
            first.Save([onFernie, onBanff], ["fernie", "banff"]);

            // A later sweep collects only fernie, where the window is now gone. banff was not swept at all.
            var second = new PaneHistory(path);
            string gone = Assert.Single(second.Save([], ["fernie"]));

            Assert.Contains("#4448", gone, StringComparison.Ordinal);
            Assert.NotNull(new PaneHistory(path).ClaimedAt(onBanff));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_SamePaneIdOnTwoHostsDoesNotCollide()
    {
        // A pane id is unique only within one tmux server, so `%3` on two hosts is two windows. Keyed by
        // host and pane id together, each keeps its own registration; a host-local key would let one
        // overwrite the other.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-collide-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane onFernie = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie", PaneId = "%3" };
            TmuxPane onBanff = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4600") with { Host = "banff", PaneId = "%3" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var history = new PaneHistory(path);
            history.Observe(onFernie, t, claimedPr: 4448);
            history.Observe(onBanff, t.AddHours(1), claimedPr: 4600);

            Assert.Equal(t, history.ClaimedAt(onFernie));
            Assert.Equal(t.AddHours(1), history.ClaimedAt(onBanff));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AnEmptySuccessfulHostIsRememberedSoALaterOmissionNarrows()
    {
        // Finding 3, across runs: a host that answered with no windows must still enter KnownHosts, or a
        // later run that omits it cannot tell the fleet narrowed and reads its view as complete.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-empty-{Guid.NewGuid():N}.json");
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane onBanff = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "banff" };

            var first = new PaneHistory(path);
            first.AdoptEpoch("banff", "1:1", t);
            first.Observe(onBanff, t, claimedPr: 4448);
            first.RecordSweptEmpty("fernie", t);
            first.Save([onBanff], ["fernie", "banff"]);

            // A later run reads the same history: fernie is remembered even though it had no windows.
            var second = new PaneHistory(path);
            Assert.Contains("fernie", second.KnownHosts);

            // The omitted set both commands compute -- KnownHosts not collected this run -- flags it.
            var collectedThisRun = new HashSet<string>(["banff"], StringComparer.Ordinal);
            Assert.Contains("fernie", second.KnownHosts.Where(h => !collectedThisRun.Contains(h)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AHostSweptEmptyDoesNotLendEpochContinuityToALaterSweep()
    {
        // The empty sweep records no epoch, so a window reappearing on the host next run is registered
        // fresh rather than treated as continuous across a gap the tool never watched.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-emptyepoch-{Guid.NewGuid():N}.json");
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.RecordSweptEmpty("fernie", t);
            first.Save([], ["fernie"]);

            var second = new PaneHistory(path);
            Assert.False(second.AdoptEpoch("fernie", "1:1", t.AddHours(1)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AWindowThatStopsClaimingClearsItsRegistrationAndCannotInheritOwnership()
    {
        // Blocker 3: A owned PR 4448, then published no usable identity while B claimed it, then reclaimed
        // it. Observing A with a null claim while it was quiet cleared its registration, so the reclaim is
        // a fresh, later registration — A cannot jump the queue ahead of B, which claimed it in the
        // meantime. Without the clear, A's stale time would keep it the owner.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-clear-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane a = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "h" };
            TmuxPane b = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "h" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            history.Observe(a, t, claimedPr: 4448, registrationWitnessed: true);
            history.Observe(a, t.AddMinutes(10), claimedPr: null);
            Assert.Null(history.ClaimedAt(a));
            Assert.False(history.IsWitnessed(a));

            history.Observe(b, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: true);
            history.Observe(a, t.AddMinutes(20), claimedPr: 4448, registrationWitnessed: true);
            Assert.Equal(t.AddMinutes(20), history.ClaimedAt(a));

            IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
                [(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.IsWitnessed);

            Assert.Equal(ClaimRank.Owner, ranked[Claim.Key(b)].Rank);
            Assert.Equal(ClaimRank.Follower, ranked[Claim.Key(a)].Rank);
            Assert.True(ranked[Claim.Key(b)].OwnsClaim);
            Assert.False(ranked[Claim.Key(a)].OwnsClaim);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AnIssueOrMalformedStateClearsAPriorPrRegistration()
    {
        // The other shapes blocker 3 covers: a window that had claimed a PR now tracks an issue, or
        // publishes a record that names nothing. Both are observed with a null claim, so the stale PR
        // registration and its provenance are cleared while the digest and silence survive.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-clear2-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane w = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "h" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            history.Observe(w, t, claimedPr: 4448, registrationWitnessed: true);
            Assert.NotNull(history.ClaimedAt(w));

            // Now the window tracks an issue (an issue-state resolves to a null claim) or a malformed
            // record — the command passes claimedPr null in both cases.
            TimeSpan? silence = history.Observe(w, t.AddMinutes(30), claimedPr: null);

            Assert.Null(history.ClaimedAt(w));
            Assert.False(history.IsWitnessed(w));

            // The silence measurement survives the claim being cleared: the digest is unchanged.
            Assert.Equal(TimeSpan.FromMinutes(30), silence);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildRows_AReclaimAfterGoingQuietDoesNotInheritOldOwnership()
    {
        // Blocker 3, end to end through waiting: across three sweeps sharing one history, A claims 4448,
        // goes quiet (no identity) while B claims it, then reclaims. B claimed it first of the two live
        // registrations, so B owns and A follows — A does not inherit its original ownership.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-reclaim-{Guid.NewGuid():N}.json");
        try
        {
            var history = new PaneHistory(path);
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane aClaims = Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "a") with { Host = "h", Epoch = "1:1" };
            TmuxPane aQuiet = Pane("cp:1", "", PaneActivity.Idle, agentState: null, windowName: "worker") with { Host = "h", Epoch = "1:1" };
            TmuxPane bClaims = Pane("cp:2", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "b") with { Host = "h", Epoch = "1:1" };

            static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);

            await WaitingCommand.BuildRowsAsync([aClaims], None, None, t, all: true, TestContext.Current.CancellationToken, collectedHosts: ["h"], history: history);
            await WaitingCommand.BuildRowsAsync([aQuiet, bClaims], None, None, t.AddMinutes(10), all: true, TestContext.Current.CancellationToken, collectedHosts: ["h"], history: history);
            IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
                [aClaims, bClaims], None, None, t.AddMinutes(20), all: true, TestContext.Current.CancellationToken, collectedHosts: ["h"], history: history);

            WaitingRow aRow = rows.Single(r => r.Pane.PaneId == aClaims.PaneId);
            WaitingRow bRow = rows.Single(r => r.Pane.PaneId == bClaims.PaneId);

            Assert.Equal(ClaimRank.Owner, bRow.Claim.Rank);
            Assert.Equal(ClaimRank.Follower, aRow.Claim.Rank);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildRows_AnOmittedKnownHostNarrowsTheViewAndIsReported()
    {
        // Blocker 5, waiting: a run that omits a previously-collected host is narrower than the fleet has
        // been, so Omitted names it and the first line leads with NARROWED rather than QUIET.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-narrow-{Guid.NewGuid():N}.json");
        try
        {
            var seed = new PaneHistory(path);
            seed.AdoptEpoch("fernie", "1:1", DateTimeOffset.UtcNow);
            seed.AdoptEpoch("banff", "2:1", DateTimeOffset.UtcNow);
            seed.Save([], ["fernie", "banff"]);

            var history = new PaneHistory(path);
            static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);
            TmuxPane onBanff = Pane("cp:1", "$ ", PaneActivity.Idle, windowName: "w") with { Host = "banff", Epoch = "2:1" };

            IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
                [onBanff], None, None, DateTimeOffset.UtcNow, all: true, TestContext.Current.CancellationToken,
                collectedHosts: ["banff"], history: history);

            Assert.Contains("fernie", WaitingCommand.Omitted);
            Assert.StartsWith("NARROWED", WaitingCommand.Summary(rows, [], WaitingCommand.Omitted), StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Claim_OrdersByRegistrationNotByCollectionOrder()
    {
        // An owner that changes identity between sweeps is worse than no owner, so ranking is by when
        // each window first claimed the PR — remembered, not derived from this sweep's ordering. The order
        // is observed only because both windows' host was swept in full before this run, so the recorded
        // times are witnessed appearances rather than first looks.
        TmuxPane late = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane early = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(late, 4448, null), (early, 4448, null)],
            p => p.PaneId == early.PaneId ? t : t.AddHours(1),
            _ => true);

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
    public void Claim_AnUnwitnessedRivalCannotBeOrderedSoTheContestStaysInferred()
    {
        // The stricter rule: an ownership order is a fact only when BOTH claims were witnessed
        // registering. A window watched registering cannot be ranked against one that was not — the
        // unwatched one may be older, not newer — so the contest stays inferred until both are witnessed.
        TmuxPane seen = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane fresh = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(fresh, 4448, null), (seen, 4448, null)],
            p => p.PaneId == seen.PaneId ? t : null,
            p => p.PaneId == seen.PaneId);

        Assert.All(ranked.Values, c => Assert.Equal(ClaimBasis.Inferred, c.Basis));
        Assert.All(ranked.Values, c => Assert.False(c.OwnsClaim));
    }

    [Fact]
    public void Claim_TwoWindowsBothUnseenCannotBeOrdered()
    {
        // Neither was watched registering, so nothing distinguishes them but a guess.
        TmuxPane a = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448");
        TmuxPane b = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4448");

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(a, 4448, 3), (b, 4448, 9)], _ => null, _ => false);

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
                // Distinct window names: the contest is established by the published state, and two
                // windows sharing a name is a different defect with its own test.
                Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448"),
                Pane("cp:2", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234 reviews=2/2 rec=merge", windowName: "pr4448-b"),
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
        // One host unreachable, another quiet: the sweep still returns, reporting the failure rather than
        // being condemned by it. Injected so it costs no ssh and no tmux server.
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["nosuchbox", "banff"],
            (host, _) => host == "nosuchbox"
                ? throw new TmuxUnavailableException("nosuchbox: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([Pane("banff:1", "", PaneActivity.Idle)]),
            TestContext.Current.CancellationToken);

        Assert.Single(collected.Panes);
        Assert.Single(collected.Unreachable);
        Assert.False(collected.TotalFailure);
        Assert.True(collected.AnyFailure);
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

    [Theory]
    [InlineData("✗ Execution failed: 422 This content was flagged for possible cybersecurity risk.")]
    [InlineData("Execution failed: Failed to get response from the AI model; retried 5 times")]
    [InlineData("  Rate limit reached for this model, try again later")]
    public void ClassifyActivity_NoticesTheAgentItselfFailing(string line)
    {
        // A different beast from every other state: the work is fine and the worker is not. Nothing
        // about the PR explains it, and nothing about the PR will clear it.
        Assert.Equal(PaneActivity.Stalled, TmuxScanner.ClassifyActivity($"● Round 3 complete.\n{line}\n> "));
        Assert.Contains("Execution failed", TmuxScanner.StallReason("x\nExecution failed: 422 flagged\n> ")!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClassifyActivity_AStallOutranksAnInterruptHint()
    {
        // A pane that failed mid-turn can still be showing "esc to interrupt", which would otherwise
        // read as an agent hard at work.
        const string Capture = "Execution failed: 422 This content was flagged\n· Working (esc to interrupt)";

        Assert.Equal(PaneActivity.Stalled, TmuxScanner.ClassifyActivity(Capture));
    }

    [Fact]
    public void ClassifyActivity_OrdinaryOutputIsNotAStall()
    {
        Assert.Null(TmuxScanner.StallReason("● Round 3 is complete for PR 4616.\n  Fix description: ...\n> "));
    }

    [Fact]
    public async Task BuildRows_TwoWindowsSharingANameCannotBeIdentifiedByIt()
    {
        // Observed live on fernie: windows 0 and 6 both named pr4551-blocked, with window 0 actually
        // working on 4663. An agent had renamed a neighbour. The one with published state is still
        // identified correctly; the one without is not identified at all, because the only evidence it
        // had was a name that demonstrably belongs to someone else.
        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [
                Pane("cp:0", "", PaneActivity.Idle, agentState: "pr=4663 head=abc1234 reviews=0/2 rec=wait", windowName: "pr4551-blocked"),
                Pane("cp:6", "", PaneActivity.Idle, windowName: "pr4551-blocked"),
            ],
            (_, _) => Task.FromResult<PrFacts?>(null),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow,
            all: true,
            ct: TestContext.Current.CancellationToken);

        WaitingRow stated = rows.Single(r => r.Record?.PrNumber == 4663);
        Assert.Contains(stated.Record!.Defects, d => d.Contains("shares the name", StringComparison.Ordinal));

        WaitingRow nameless = rows.Single(r => r.Record is null);
        Assert.Contains("no published state", nameless.Verdict.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("● Round 2 is complete for PR 4663.", 4448, true)]   // talks about a different PR
    [InlineData("● Round 2 is complete for PR 4448.", 4448, false)]  // talks about this one
    [InlineData("~/git/dotnet-inspect\n❯ ", 4448, false)]            // says nothing about any PR
    public void PaneContradictsPr_TellsDisagreementFromSilence(string capture, int pr, bool contradicts)
        => Assert.Equal(contradicts, TmuxScanner.PaneContradictsPr(capture, pr));

    [Fact]
    public void MentionsPr_IsBoundedByNonDigits()
    {
        // 4663 must not match inside 46631 or a sha-like run of digits.
        Assert.True(TmuxScanner.MentionsPr("work on PR 4663 now", 4663));
        Assert.False(TmuxScanner.MentionsPr("build 46631 failed", 4663));
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
