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
/// <remarks>
/// Joins the non-parallel <c>ConsoleCapture</c> collection: several tests here reconcile through
/// <see cref="WaitingCommand.BuildRowsAsync"/>, which mutates the shared static
/// <see cref="WaitingCommand.Omitted"/>/<see cref="WaitingCommand.Departed"/> reporting fields and reads
/// them back. Running in the serialized collection keeps a parallel reconcile in another class from
/// clobbering those fields between a write and the assertion that reads it.
/// </remarks>
[Collection("ConsoleCapture")]
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

        // The closing epoch bracket, equal to the opening one — a collection that did not restart.
        sb.Append(Nonce).Append(":epoch 4242:1755900000\n");
        return sb.ToString();
    }

    /// <summary>Encodes one field the way the script's <c>printf | od | tr</c> pipeline does.</summary>
    private static string Hex(string text) => Convert.ToHexStringLower(System.Text.Encoding.UTF8.GetBytes(text));

    /// <summary>One manifest row, from the readable pipe form to the six encoded fields on the wire.</summary>
    private static string Row(string fields, string nonce = Nonce)
    {
        string[] parts = fields.Split('|', 6);
        return $"{nonce}:w|" + string.Join('|', parts.Select(Hex));
    }

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
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", hostile, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n{Nonce}:epoch 4242:1755900000\n",
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
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, hostile)}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n{Nonce}:epoch 4242:1755900000\n",
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
    [InlineData("deadbeefcafe0123:w|2531|6e|31|31|31|31|6e69676874")]     // last field is not a window id
    public void ParseCollection_RejectsAMalformedManifestRow(string row)
    {
        // Dropping a row loses a window, and a lost window is indistinguishable from a window that is not
        // there — which is the whole failure. A manifest that does not decode is the host being
        // unreadable, and it is reported as that.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{row}\n{Nonce}:end\n", host: null, Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsAManifestWithPanesButNoEpoch()
    {
        // The epoch binds each pane id to the server that minted it. Without it a pane could carry an
        // empty epoch straight past AdoptEpoch's continuity and restart checks, so a manifest that lists
        // any window and names no server generation is not this collection's and is unreadable.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Theory]
    [InlineData("notanepoch")]     // no colon
    [InlineData("4242:")]          // no start time
    [InlineData(":1755900000")]    // no pid
    [InlineData("4242:12:34")]     // an extra colon
    [InlineData("42x2:1755900000")] // a non-decimal pid
    public void ParseCollection_RejectsAMalformedEpoch(string epoch)
    {
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch {epoch}\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsADuplicateEpochEvenWhenIdentical()
    {
        // Two epoch lines are two accounts of the server generation; the output is then not the one this
        // script writes, and trusting either copy is a guess — so a repeat is invalid even if it matches.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n"
                + $"{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_AllowsAnEmptySuccessfulManifestWithNoEpoch()
    {
        // A host with no active tmux server answers with a complete but empty manifest and no epoch — it
        // is observed to hold no windows, which is a success, not a malformed collection.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            $"{Nonce}:manifest\n{Nonce}:end\n", host: null, Nonce);

        Assert.Empty(panes);
    }

    [Theory]
    [InlineData("notanepoch")]
    [InlineData("")]
    [InlineData("4242:")]
    public void ParseCollection_RejectsAMalformedEpochEvenOnAnEmptyManifest(string epoch)
    {
        // Missing is allowed for an empty manifest, but a present epoch must still be canonical: an empty
        // or malformed one is not something this script writes, so it is not accepted just because the
        // manifest carried no windows.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch {epoch}\n{Nonce}:manifest\n{Nonce}:end\n", host: null, Nonce));
    }

    [Fact]
    public void ParseCollection_AllowsAnEmptyManifestThatCarriesAValidEpoch()
    {
        // A running server with no windows is also an empty success, and it reports its epoch — bracketed
        // by an equal opening and closing line, since no restart happened during the collection.
        IReadOnlyList<TmuxPane> panes = TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{Nonce}:end\n{Nonce}:epoch 4242:1755900000\n", host: null, Nonce);

        Assert.Empty(panes);
    }

    [Fact]
    public void ParseCollection_RejectsACollectionWhoseServerRestartedMidway()
    {
        // Blocker 1: the opening and closing epochs differ, so the server restarted somewhere during the
        // collection — after the opening bracket but before the fields, or during the captures. Its rows
        // may then mix two generations and preserve a witnessed history that no longer exists, so the
        // whole account is rejected rather than parsed.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n{Nonce}:epoch 9999:1755900001\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsPanesWithNoClosingEpoch()
    {
        // The closing bracket is what proves the server did not restart during the collection, so a
        // manifest with windows and only an opening epoch is not this collection's complete account.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsContentAfterTheClosingEpoch()
    {
        // The closing epoch is the last thing the collection writes; anything after it is not the shape
        // this script produces.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{Nonce}:end\n{Nonce}:epoch 4242:1755900000\n{Nonce}:epoch 4242:1755900000\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_RejectsTwoOpeningEpochs()
    {
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{Nonce}:end\n{Nonce}:epoch 4242:1755900000\n",
            host: null,
            Nonce));
    }

    [Theory]
    [InlineData("%01")]     // leading-zero pane id
    [InlineData("%00")]     // zero with a leading zero
    public void ParseCollection_RejectsANonCanonicalId(string paneId)
    {
        // tmux mints an id as a canonical non-negative decimal — a lone 0, otherwise no leading zero — so
        // `%01`, `%00` are ids it never prints and are rejected.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow(paneId, "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane {paneId}\n{Hex("> ")}\n{Nonce}:read {paneId}\n{Nonce}:epoch 4242:1755900000\n",
            host: null,
            Nonce));
    }

    [Fact]
    public void ParseCollection_AcceptsZeroAsALoneIdForPane()
    {
        // A lone 0 IS canonical — `%0` is the first pane id tmux mints — so it is kept verbatim.
        IReadOnlyList<TmuxPane> windows = TmuxScanner.ParseCollection(
            $"{Nonce}:epoch 4242:1755900000\n{Nonce}:manifest\n{EncodedRow("%0", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %0\n{Hex("> ")}\n{Nonce}:read %0\n{Nonce}:epoch 4242:1755900000\n",
            host: null,
            Nonce);

        TmuxPane w = Assert.Single(windows);
        Assert.Equal("%0", w.PaneId);
    }

    [Theory]
    [InlineData("0:0")]    // zero components
    [InlineData("4242:0")]
    [InlineData("01:02")]  // leading zeros
    [InlineData("042:1")]
    public void ParseCollection_RejectsANonCanonicalEpoch(string epoch)
    {
        // Blocker 4: an epoch's pid and start_time are canonical positive decimals — never zero, never a
        // leading zero — so an impossible pair like `0:0` or `01:02` is rejected even when it brackets the
        // collection on both sides.
        Assert.Throws<TmuxUnavailableException>(() => TmuxScanner.ParseCollection(
            $"{Nonce}:epoch {epoch}\n{Nonce}:manifest\n{EncodedRow("%1", "night:1", "1", "1755900000", string.Empty, "pr4595")}\n{Nonce}:end\n"
                + $"{Nonce}:pane %1\n{Hex("> ")}\n{Nonce}:read %1\n{Nonce}:epoch {epoch}\n",
            host: null,
            Nonce));
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
    public async Task FetchDetailed_Remaining0OnA200StillClassifiesFound()
    {
        // The request that spends the last unit of the budget can still return a real answer. A 200 with
        // `X-RateLimit-Remaining: 0` is a valid Found — the exhaustion refuses the NEXT read, it does not
        // rewrite THIS one into an outage.
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(200, """
                {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
                """, "x-ratelimit-remaining: 0"),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(200, """{"check_runs":[]}"""),
        };

        var source = new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync);
        PrFetch fetch = await source.FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Found, fetch.Status);
        Assert.NotNull(fetch.Facts);
        Assert.Equal(Head, fetch.Facts.HeadSha);
        Assert.True(source.RateLimited);
        Assert.Equal(0, source.RateLimitRemaining);
    }

    [Fact]
    public async Task FetchDetailed_Remaining0OnA304StillServesTheCachedBody()
    {
        // A conditional read answered 304 as the budget hits zero is still the cached body, current until
        // the branch moves — the exhaustion is remembered, the answer is not thrown away.
        var cache = new FakeCache();
        cache.Put("repos/o/r/pulls/4595", "\"etag-pull\"", """
            {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
            """);
        cache.Put($"repos/o/r/commits/{Head}/check-runs?per_page=100", "\"etag-checks\"", """{"check_runs":[]}""");

        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(304, string.Empty, "x-ratelimit-remaining: 0"),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(304, string.Empty, "x-ratelimit-remaining: 0"),
        };

        var source = new GhPrFactsSource("o/r", cache, gh.RunAsync);
        PrFetch fetch = await source.FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Found, fetch.Status);
        Assert.NotNull(fetch.Facts);
        Assert.Equal(Head, fetch.Facts.HeadSha);
        Assert.True(source.RateLimited);
    }

    [Fact]
    public async Task FetchDetailed_Remaining0OnA404IsStillAnAffirmativeNotFound()
    {
        // An affirmative 404 is the one negative answer to trust, and the budget hitting zero on the same
        // response does not make GitHub's "no such PR" any less true.
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(404, """{"message":"Not Found"}""", "x-ratelimit-remaining: 0"),
        };

        var source = new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync);
        PrFetch fetch = await source.FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.NotFound, fetch.Status);
        Assert.Null(fetch.Facts);
        Assert.True(source.RateLimited);
    }

    [Fact]
    public async Task FetchDetailed_Remaining0RefusesTheNextNetworkRead()
    {
        // Classifying the current response truthfully must not spend the budget again: once a response has
        // reported it exhausted, the very next call is refused before it reaches gh.
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(404, """{"message":"Not Found"}""", "x-ratelimit-remaining: 0"),
        };

        var source = new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync);

        Assert.Equal(PrFetchStatus.NotFound, (await source.FetchDetailedAsync(4595, TestContext.Current.CancellationToken)).Status);

        int spent = gh.Requests.Count;
        Assert.Equal(PrFetchStatus.Unavailable, (await source.FetchDetailedAsync(4596, TestContext.Current.CancellationToken)).Status);

        // The second lookup added no gh request: the exhausted budget refuses it before it is spent.
        Assert.Equal(spent, gh.Requests.Count);
    }

    [Fact]
    public async Task FetchDetailed_FoundReturnsTheFacts()
    {
        // The happy path through the three-outcome seam: a 200 with a head yields Found and the facts.
        var gh = new FakeGh
        {
            ["repos/o/r/pulls/4595"] = Response(200, """
                {"number":4595,"state":"open","mergeable_state":"clean","head":{"sha":"722512e25f0c1d4a9b8e7360a1c2d3e4f5061728"}}
                """),
            [$"repos/o/r/commits/{Head}/check-runs?per_page=100"] = Response(200, """{"check_runs":[]}"""),
        };

        PrFetch fetch = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Found, fetch.Status);
        Assert.NotNull(fetch.Facts);
        Assert.Equal(Head, fetch.Facts.HeadSha);
    }

    [Fact]
    public async Task FetchDetailed_AnAffirmative404IsNotFound()
    {
        // The one negative answer to trust: GitHub looked and there is no such PR. This — and only this —
        // is NotFound, which is what lets `pr` reserve NOTFOUND for a real 404.
        var gh = new FakeGh { ["repos/o/r/pulls/4595"] = Response(404, """{"message":"Not Found"}""") };

        PrFetch fetch = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.NotFound, fetch.Status);
        Assert.Null(fetch.Facts);
    }

    [Theory]
    [InlineData(401, "", "")]                                    // authentication failure
    [InlineData(403, "", "x-ratelimit-remaining: 0")]            // rate limit / forbidden
    [InlineData(429, "", "")]                                    // too many requests
    [InlineData(500, "", "")]                                    // server error
    [InlineData(502, "", "")]                                    // bad gateway
    [InlineData(200, "{ not json", "")]                          // a 200 body that cannot be parsed
    [InlineData(200, """{"state":"open"}""", "")]                // a 200 with no head sha
    public async Task FetchDetailed_UnreadableOutcomesAreUnavailableNotNotFound(int status, string body, string header)
    {
        // Every read that is not an affirmative 404 and not a usable body is Unavailable — auth, rate
        // limit, a 5xx, or a body that cannot be trusted. None of these may collapse to NotFound, or an
        // outage reads as "no such PR".
        string[] extra = header.Length == 0 ? [] : [header];
        var gh = new FakeGh { ["repos/o/r/pulls/4595"] = Response(status, body, extra) };

        PrFetch fetch = await new GhPrFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Null(fetch.Facts);
    }

    [Fact]
    public async Task FetchDetailed_ANonzeroExitWithNoStatusIsUnavailable()
    {
        // A transport failure: gh exits nonzero and there is no HTTP status line to read. The existence of
        // the PR is unknown, so this is Unavailable — never a not-found inferred from silence.
        Task<GhResult> Gh(IReadOnlyList<string> args, CancellationToken ct)
            => Task.FromResult(new GhResult(1, string.Empty, "dial tcp: connection refused"));

        PrFetch fetch = await new GhPrFactsSource("o/r", new FakeCache(), Gh)
            .FetchDetailedAsync(4595, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Null(fetch.Facts);
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
        sb.Append(nonce).Append(":epoch 4242:1755900000\n");
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

        sb.Append(nonce).Append(":epoch 4242:1755900000\n");
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
    public void History_LocalAndAnAliasNamedLocalDoNotShareMemory()
    {
        // Blocker 3: the real local machine and an ssh alias literally named `local` are distinct targets,
        // so the same pane id on each keeps its own registration and both are known separately.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-local-{Guid.NewGuid():N}.json");
        try
        {
            TmuxPane localPane = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = null, PaneId = "%3" };
            TmuxPane aliasPane = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4600") with { Host = "local", PaneId = "%3" };
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var history = new PaneHistory(path);
            history.AdoptEpoch(null, "1:1", t);
            history.AdoptEpoch("local", "2:2", t);
            history.Observe(localPane, t, claimedPr: 4448);
            history.Observe(aliasPane, t.AddHours(1), claimedPr: 4600);

            Assert.Equal(t, history.ClaimedAt(localPane));
            Assert.Equal(t.AddHours(1), history.ClaimedAt(aliasPane));

            Assert.Contains(TargetId.Local.Key, history.KnownHosts);
            Assert.Contains(TargetId.ForHost("local").Key, history.KnownHosts);
            Assert.Equal(2, history.KnownHosts.Count);
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

            // A later run reads the same history: fernie is remembered even though it had no windows. Its
            // key is the structured target id, not the raw alias.
            var second = new PaneHistory(path);
            string fernieKey = TargetId.ForHost("fernie").Key;
            Assert.Contains(fernieKey, second.KnownHosts);

            // The omitted set both commands compute -- KnownHosts not collected this run -- flags it.
            var collectedThisRun = new HashSet<string>([TargetId.ForHost("banff").Key], StringComparer.Ordinal);
            Assert.Contains(fernieKey, second.KnownHosts.Where(k => !collectedThisRun.Contains(k)));
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
    public async Task BuildRows_AGapBreaksAWitnessedOrderSoAnUnseenReclaimIsNotTheObservedOwner()
    {
        // Blocker 2: A and B both claim 4448, witnessed, A first — so A is the observed owner. A's host is
        // then omitted for a sweep (a gap), during which A could have released and reclaimed unseen. On the
        // next full sweep at the same epoch A's remembered order and witness are invalidated, so it is
        // registered fresh and cannot stay the actionable observed owner. No departure is reported for A
        // while it is merely unseen.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-gap-{Guid.NewGuid():N}.json");
        try
        {
            var history = new PaneHistory(path);
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane aQuiet = Pane("ha:1", "", PaneActivity.Idle, agentState: null, windowName: "a") with { Host = "ha", Epoch = "1:1" };
            TmuxPane aClaims = Pane("ha:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "a") with { Host = "ha", Epoch = "1:1" };
            TmuxPane bQuiet = Pane("hb:1", "", PaneActivity.Idle, agentState: null, windowName: "b") with { Host = "hb", Epoch = "2:1" };
            TmuxPane bClaims = Pane("hb:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "b") with { Host = "hb", Epoch = "2:1" };
            static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);
            CancellationToken ct = TestContext.Current.CancellationToken;

            // Sweep 1: full fleet, no claims — establishes continuous observation of both hosts.
            await WaitingCommand.BuildRowsAsync([aQuiet, bQuiet], None, None, t, all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            // Sweep 2: A claims, witnessed (ha was observed before, view complete).
            await WaitingCommand.BuildRowsAsync([aClaims, bQuiet], None, None, t.AddMinutes(10), all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            // Sweep 3: B claims, witnessed; A continues. A registered first, so A is the owner.
            IReadOnlyList<WaitingRow> beforeGap = await WaitingCommand.BuildRowsAsync(
                [aClaims, bClaims], None, None, t.AddMinutes(20), all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            Assert.Equal(ClaimRank.Owner, beforeGap.Single(r => r.Pane.PaneId == aClaims.PaneId).Claim.Rank);

            // Sweep 4: omit ha — A is unseen, not departed.
            await WaitingCommand.BuildRowsAsync([bClaims], None, None, t.AddMinutes(30), all: true, ct, collectedHosts: ["hb"], history: history);
            Assert.DoesNotContain(WaitingCommand.Departed, d => d.Contains("#4448", StringComparison.Ordinal) && d.Contains(TargetId.ForHost("ha").Display, StringComparison.Ordinal));

            // Sweep 5: full fleet again, same epoch. A reappears; the gap invalidated its order and witness.
            IReadOnlyList<WaitingRow> afterGap = await WaitingCommand.BuildRowsAsync(
                [aClaims, bClaims], None, None, t.AddMinutes(40), all: true, ct, collectedHosts: ["ha", "hb"], history: history);

            WaitingRow aRow = afterGap.Single(r => r.Pane.PaneId == aClaims.PaneId);
            WaitingRow bRow = afterGap.Single(r => r.Pane.PaneId == bClaims.PaneId);
            Assert.Equal(ClaimRank.Follower, aRow.Claim.Rank);
            Assert.Equal(ClaimRank.Owner, bRow.Claim.Rank);
            Assert.False(aRow.MayAct);
            Assert.False(bRow.MayAct);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_AnOlderHistoryFileFailsClosedRatherThanTrustingItsRegistrations()
    {
        // Blocker 2/3, backward compatibility: an older, differently-keyed history file — a pane keyed by
        // the raw `fernie|%3`, a host keyed by the raw alias — is not this scheme's, so its entries are
        // dropped on load. Nothing it recorded can become a witnessed order.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-oldfile-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "panes": { "fernie|%3": { "digest": "d", "since": "2026-08-25T12:00:00+00:00", "pr": 4448, "claimedAt": "2026-08-25T12:00:00+00:00", "witnessed": true } },
                  "hosts": { "fernie": { "epoch": "1:1", "sweptAt": "2026-08-25T12:00:00+00:00" } }
                }
                """);

            var history = new PaneHistory(path);
            TmuxPane onFernie = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie", PaneId = "%3" };

            Assert.Empty(history.KnownHosts);
            Assert.Null(history.ClaimedAt(onFernie));
            Assert.False(history.IsWitnessed(onFernie));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_ACorruptedHistoryFileFailsClosedAndCannotCrashLoadingOrReporting()
    {
        // Follow-up 1: a history file whose keys are this scheme's tag but corrupted payloads — a host key
        // `RA` (impossible base64), a pane key `R_w|%3` (byte 0xFF, not valid UTF-8) carrying a witnessed
        // claim. None is a canonical target key, so all are dropped on load: KnownHosts is empty, nothing
        // is witnessed, and neither loading nor a subsequent Save/report throws trying to display them.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-corrupt-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "panes": { "R_w|%3": { "digest": "d", "since": "2026-08-25T12:00:00+00:00", "pr": 4448, "claimedAt": "2026-08-25T12:00:00+00:00", "witnessed": true } },
                  "hosts": {
                    "RA": { "epoch": "1:1", "sweptAt": "2026-08-25T12:00:00+00:00", "continuous": true },
                    "RQR": { "epoch": "2:1", "sweptAt": "2026-08-25T12:00:00+00:00", "continuous": true }
                  }
                }
                """);

            var history = new PaneHistory(path);
            Assert.Empty(history.KnownHosts);

            // Reporting over a fresh live pane must not touch the dropped corrupt entries, and Save must
            // not throw trying to render a departure or an omission from a key it cannot decode.
            TmuxPane live = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4600") with { Host = "banff", PaneId = "%9" };
            history.AdoptEpoch("banff", "9:1", new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero));
            history.Observe(live, new DateTimeOffset(2026, 8, 26, 12, 0, 0, TimeSpan.Zero), claimedPr: 4600);
            IReadOnlyList<string> departed = history.Save([live], ["banff"]);

            Assert.False(history.IsWitnessed(live) && history.ClaimedAt(live) is null);
            Assert.DoesNotContain(departed, d => d.Contains("4448", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_DropsAnEntryWhosePaneIdIsNotCanonical()
    {
        // Blocker 4: the composite pane key's suffix is validated with the exact IsPaneId, not merely a
        // non-empty string, so an entry keyed by `%01` — an id tmux never mints — is dropped on load
        // rather than kept as a witnessed order.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-canon-{Guid.NewGuid():N}.json");
        try
        {
            string good = TargetId.ForHost("fernie").ComposeWith("%3");
            string bad = TargetId.ForHost("fernie").ComposeWith("%01");
            File.WriteAllText(path, $$"""
                {
                  "panes": {
                    "{{good}}": { "digest": "d", "since": "2026-08-25T12:00:00+00:00", "pr": 4448, "claimedAt": "2026-08-25T12:00:00+00:00", "witnessed": true },
                    "{{bad}}": { "digest": "d", "since": "2026-08-25T12:00:00+00:00", "pr": 4600, "claimedAt": "2026-08-25T12:00:00+00:00", "witnessed": true }
                  },
                  "hosts": {}
                }
                """);

            var history = new PaneHistory(path);
            TmuxPane goodPane = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie", PaneId = "%3" };
            TmuxPane badPane = Pane("cp:2", "", PaneActivity.Idle, windowName: "pr4600") with { Host = "fernie", PaneId = "%01" };

            Assert.True(history.IsWitnessed(goodPane));
            Assert.False(history.IsWitnessed(badPane));
            Assert.Null(history.ClaimedAt(badPane));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void History_NullValuesAndImpossibleRecordsAreDroppedRatherThanTrusted()
    {
        // Blocker 2: the deserializer can hand back null values and records this implementation never
        // wrote — a witnessed claim with no time, a host claiming continuity it never swept. None may
        // crash Save/report or confer ownership, so nulls are dropped and impossible combinations fail
        // closed: the claim and its witness go, the continuity goes.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-sanitize-{Guid.NewGuid():N}.json");
        try
        {
            string paneKey = TargetId.ForHost("fernie").ComposeWith("%3");
            string nullPaneKey = TargetId.ForHost("fernie").ComposeWith("%4");
            string hostKey = TargetId.ForHost("fernie").Key;
            string nullHostKey = TargetId.ForHost("banff").Key;
            string uncontinuousKey = TargetId.ForHost("merritt").Key;

            File.WriteAllText(path, $$"""
                {
                  "panes": {
                    "{{paneKey}}": { "digest": "d", "since": "2026-08-25T12:00:00+00:00", "pr": 4448, "claimedAt": null, "witnessed": true },
                    "{{nullPaneKey}}": null
                  },
                  "hosts": {
                    "{{hostKey}}": { "epoch": "1:1", "sweptAt": "2026-08-25T12:00:00+00:00", "continuous": true },
                    "{{nullHostKey}}": null,
                    "{{uncontinuousKey}}": { "epoch": "3:1", "sweptAt": null, "continuous": true }
                  }
                }
                """);

            var history = new PaneHistory(path);
            TmuxPane pane = Pane("cp:1", "", PaneActivity.Idle, windowName: "pr4448") with { Host = "fernie", PaneId = "%3" };
            DateTimeOffset t = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

            // A claim with a PR but no time is not well-formed: registration and witness are cleared.
            Assert.Null(history.ClaimedAt(pane));
            Assert.False(history.IsWitnessed(pane));

            // A host claiming continuity with no sweep time cannot have been observed continuously, so its
            // continuity is dropped and AdoptEpoch reports it as not continuous.
            Assert.False(history.AdoptEpoch("merritt", "3:1", t));

            // Null records are gone; the well-formed host survives; nothing crashed on load or on Save.
            Assert.Contains(hostKey, history.KnownHosts);
            Assert.DoesNotContain(nullHostKey, history.KnownHosts);
            history.Save([pane], ["fernie", "merritt"]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task BuildRows_ATotalFailureBreaksContinuitySoTheNextFullSweepIsNotOwned()
    {
        // Blocker 1: a witnessed contested order (A owner, B follower), then a sweep that collected
        // nothing — a total failure, which persists discontinuity for every known host exactly as
        // WaitingCommand does — then a full sweep at the same epoch. The gap invalidates both
        // registrations, so the order is inferred and nobody is the actionable observed owner.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-totalfail-{Guid.NewGuid():N}.json");
        try
        {
            var history = new PaneHistory(path);
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane aQuiet = Pane("ha:1", "", PaneActivity.Idle, agentState: null, windowName: "a") with { Host = "ha", Epoch = "1:1" };
            TmuxPane aClaims = Pane("ha:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "a") with { Host = "ha", Epoch = "1:1" };
            TmuxPane bQuiet = Pane("hb:1", "", PaneActivity.Idle, agentState: null, windowName: "b") with { Host = "hb", Epoch = "2:1" };
            TmuxPane bClaims = Pane("hb:1", "", PaneActivity.Idle, agentState: "pr=4448 head=abc1234", windowName: "b") with { Host = "hb", Epoch = "2:1" };
            static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);
            CancellationToken ct = TestContext.Current.CancellationToken;

            await WaitingCommand.BuildRowsAsync([aQuiet, bQuiet], None, None, t, all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            await WaitingCommand.BuildRowsAsync([aClaims, bQuiet], None, None, t.AddMinutes(10), all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            IReadOnlyList<WaitingRow> beforeFailure = await WaitingCommand.BuildRowsAsync(
                [aClaims, bClaims], None, None, t.AddMinutes(20), all: true, ct, collectedHosts: ["ha", "hb"], history: history);
            Assert.Equal(ClaimBasis.Observed, beforeFailure.Single(r => r.Pane.PaneId == aClaims.PaneId).Claim.Basis);

            // A total collection failure: nothing collected, so every known host's continuity is broken on
            // disk — the persistence WaitingCommand.RunAsync performs before reporting the failure.
            history.Save([], []);

            IReadOnlyList<WaitingRow> afterFailure = await WaitingCommand.BuildRowsAsync(
                [aClaims, bClaims], None, None, t.AddMinutes(30), all: true, ct, collectedHosts: ["ha", "hb"], history: history);

            WaitingRow aRow = afterFailure.Single(r => r.Pane.PaneId == aClaims.PaneId);
            WaitingRow bRow = afterFailure.Single(r => r.Pane.PaneId == bClaims.PaneId);
            Assert.NotEqual(ClaimBasis.Observed, aRow.Claim.Basis);
            Assert.False(aRow.MayAct);
            Assert.False(bRow.MayAct);
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
    public async Task BuildRows_ClaimsResolveToTheirOwnRepoAcrossRepos()
    {
        // #178: two idle windows, each claiming a PR that lives in a different repo. Each row resolves to
        // its own repo — the point of searching the fleet's repos rather than the one inferred scope.
        TmuxPane onFirst = Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=100 head=aaaa1111 reviews=2/2 rec=merge", windowName: "pr100");
        TmuxPane onSecond = Pane("cp:2", "", PaneActivity.Idle, agentState: "pr=200 head=bbbb1111 reviews=2/2 rec=merge", windowName: "pr200");

        PrFacts factsFirst = new() { Number = 100, Repo = "owner/first", HeadSha = "aaaa1111", State = "open", MergeableState = "clean" };
        PrFacts factsSecond = new() { Number = 200, Repo = "owner/second", HeadSha = "bbbb1111", State = "open", MergeableState = "clean" };

        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [onFirst, onSecond],
            (pr, _) => Task.FromResult(pr == 100
                ? PrFetch.Found(factsFirst).WithRepos(["owner/first", "owner/second"], ["owner/first"])
                : PrFetch.Found(factsSecond).WithRepos(["owner/first", "owner/second"], ["owner/second"])),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow, all: true, TestContext.Current.CancellationToken);

        Assert.Equal("owner/first", Assert.Single(rows, r => r.Record?.PrNumber == 100).Repo);
        Assert.Equal("owner/second", Assert.Single(rows, r => r.Record?.PrNumber == 200).Repo);
    }

    [Fact]
    public async Task BuildRows_ANotFoundClaimSaysNotInTheSearchedReposNotCouldNotBeRead()
    {
        // #178 reporting fix: a claimed PR that affirmatively 404s in every searched repo reads as "no such
        // PR in <repos>", never as "could not be read" — different fact, different remedy (widen the scope).
        TmuxPane pane = Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4623 head=abcd1234", windowName: "pr4623");

        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [pane],
            (_, _) => Task.FromResult(PrFetch.NotFound.WithRepos(["owner/first", "owner/second"], [])),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow, all: true, TestContext.Current.CancellationToken);

        WaitingRow row = Assert.Single(rows);
        Assert.Contains("no such PR #4623 in owner/first, owner/second", row.Verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("could not be read", row.Verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildRows_AnUnavailableClaimAcrossReposStillSaysCouldNotBeRead()
    {
        // The complement: an outage in a searched repo keeps the honest "could not read from GitHub"
        // reading, since existence is genuinely unknown.
        TmuxPane pane = Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=4623 head=abcd1234", windowName: "pr4623");

        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [pane],
            (_, _) => Task.FromResult(PrFetch.Unavailable.WithRepos(["owner/first", "owner/second"], [])),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow, all: true, TestContext.Current.CancellationToken);

        Assert.Contains("could not read PR #4623 from GitHub", Assert.Single(rows).Verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BuildRows_JsonNamesTheSearchedReposAndPerRowResolvedRepo()
    {
        // #178: the searched-repo scope and the per-row resolved repo are both in the JSON, so a consumer
        // reads the sweep's scope and where each claim landed rather than inferring a single repo.
        TmuxPane pane = Pane("cp:1", "", PaneActivity.Idle, agentState: "pr=100 head=aaaa1111 reviews=2/2 rec=merge", windowName: "pr100");
        PrFacts facts = new() { Number = 100, Repo = "owner/first", HeadSha = "aaaa1111", State = "open", MergeableState = "clean" };

        IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
            [pane],
            (_, _) => Task.FromResult(PrFetch.Found(facts).WithRepos(["owner/first", "owner/second"], ["owner/first"])),
            (_, _) => Task.FromResult<PrFacts?>(null),
            DateTimeOffset.UtcNow, all: true, TestContext.Current.CancellationToken);

        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, rows, default, [], null, null, ["owner/first", "owner/second"]);
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(System.Text.Encoding.UTF8.GetString(stream.ToArray()));

        Assert.Equal(
            ["owner/first", "owner/second"],
            doc.RootElement.GetProperty("repos").EnumerateArray().Select(e => e.GetString()!).ToArray());
        System.Text.Json.JsonElement row = Assert.Single(doc.RootElement.GetProperty("rows").EnumerateArray().ToArray());
        Assert.Equal("owner/first", row.GetProperty("repo").GetString());
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

    [Fact]
    public void Retirement_ANonIdlePaneIsNeverRetiredFromAStaleDone()
    {
        // Blocker 3, retirement half. `rec=done` is trusted as the agent's final word only when the pane
        // is idle and has actually handed over. A pane blocked on a prompt, stalled, unreadable, or
        // mid-turn is not finished whatever it last published, so a stale `rec=done` under it must not
        // clear the context out from under live work — the same activity gate the verdict passes through.
        AgentState state = AgentState.Parse("pr=1 head=abc1234 reviews=2/2 rec=done", "pr1")!;
        WaitingVerdict verdict = new(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High);

        foreach (PaneActivity activity in new[] { PaneActivity.Blocked, PaneActivity.Stalled, PaneActivity.Unreadable, PaneActivity.Working })
        {
            Assert.False(Retirement.For(verdict, state, activity).IsRetirable);
        }

        // The identical record is retirable once the pane is idle, so the gate — not the record — is what
        // suppressed it above.
        Assert.True(Retirement.For(verdict, state, PaneActivity.Idle).IsRetirable);
    }

    [Fact]
    public void IsReleasing_ANonIdleOwnerWithStaleStopDoesNotTriggerPromotion()
    {
        // Blocker 3, promotion half. A stale `rec=stop` under a non-idle owner is not a release: the owner
        // is mid-turn, blocked, stalled, or unreadable, so it has not handed the claim over and a follower
        // must not be promoted on the strength of a record the pane contradicts. The Merged/Closed half is
        // gated separately by the activity-gated verdict.
        AgentState state = AgentState.Parse("pr=1 head=abc1234 rec=stop", "pr1")!;
        WaitingVerdict verdict = new(WaitingState.Unknown, RowOwner.Agent, "mid-turn", Assurance.Low("x"));

        foreach (PaneActivity activity in new[] { PaneActivity.Blocked, PaneActivity.Stalled, PaneActivity.Unreadable, PaneActivity.Working })
        {
            Assert.False(Claim.IsReleasing(state, verdict, activity));
        }

        // Idle, the same `rec=stop` is a genuine release the tool may surface.
        Assert.True(Claim.IsReleasing(state, verdict, PaneActivity.Idle));
    }

    [Fact]
    public void IsReleasing_AMergedOrClosedVerdictReleasesRegardlessOfTheRecommendation()
    {
        // The other half of releasing: a window whose PR merged or closed is done even without a `rec=stop`.
        // That verdict only reaches Merged/Closed from an idle pane (ForActivity gates it), so it needs no
        // extra activity gate here.
        AgentState state = AgentState.Parse("pr=1 head=abc1234 reviews=2/2 rec=merge", "pr1")!;
        WaitingVerdict merged = new(WaitingState.Merged, RowOwner.Operator, "merged", Assurance.High);

        Assert.True(Claim.IsReleasing(state, merged, PaneActivity.Idle));
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
            PaneId = "%" + ((uint)target.GetHashCode()).ToString(System.Globalization.CultureInfo.InvariantCulture),
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
