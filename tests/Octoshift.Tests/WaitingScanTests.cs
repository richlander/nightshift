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
        Assert.Contains("no published state", row.Verdict.Reason, StringComparison.Ordinal);
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
