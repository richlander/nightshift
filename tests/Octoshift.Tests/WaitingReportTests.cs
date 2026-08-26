namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// What a sweep prints. Two things are load-bearing here and neither is cosmetic: the first line is a
/// claim about coverage that a reader acts on, and every value in the table was chosen by somebody else.
/// </summary>
public class WaitingReportTests
{
    private static readonly WaitingCommand.Budget NoBudget = new(0, 0, 0, null, false);

    private static WaitingRow Row(
        WaitingVerdict verdict,
        string target = "night:1",
        string windowName = "pr4595",
        AgentState? record = null,
        UnidentifiedState? unidentified = null,
        string? host = null)
        => new()
        {
            Pane = new TmuxPane
            {
                PaneId = "%1",
                Target = target,
                Host = host,
                WindowName = windowName,
                SessionAttached = false,
                Activity = PaneActivity.Idle,
            },
            Record = record,
            Unidentified = unidentified,
            Verdict = verdict,
            StoppedFor = TimeSpan.FromMinutes(5),
        };

    private static WaitingVerdict Ready()
        => new(WaitingState.Ready, RowOwner.Operator, "reviews 2/2, mergeable", Assurance.High);

    private static WaitingVerdict Holding()
        => new(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High);

    private static WaitingVerdict Unsure()
        => new(WaitingState.Unknown, RowOwner.Operator, "could not read PR #4595 from GitHub", Assurance.Low("GitHub could not be read"));

    private static string Render(IReadOnlyList<WaitingRow> rows, params string[] unreachable)
    {
        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, rows, NoBudget, unreachable);
        return output.ToString();
    }

    private static string RenderJson(IReadOnlyList<WaitingRow> rows, params string[] unreachable)
    {
        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, rows, NoBudget, unreachable);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public void Summary_QuietIsAClaimAboutTheWholeFleet()
        => Assert.StartsWith("QUIET", WaitingCommand.Summary([Row(Holding())], []), StringComparison.Ordinal);

    [Fact]
    public void Summary_AttentionCountsTheRowsThatNeedAPerson()
        => Assert.Equal(
            "ATTENTION 1 of 2 window(s) need you",
            WaitingCommand.Summary([Row(Ready()), Row(Holding(), "night:2")], []));

    [Fact]
    public void Summary_AnUnreachableHostIsNeverQuiet()
    {
        // The shape this existed to prevent: one host succeeds with nothing on it, another has gone dark,
        // and the first line reported a quiet fleet while the reason sat on the last line under the
        // budget. Nothing about the dark host was collected, so nothing about it can be called quiet.
        string summary = WaitingCommand.Summary([], ["fernie: no server running"]);

        Assert.StartsWith("PARTIAL", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("QUIET", summary, StringComparison.Ordinal);
        Assert.Contains("1 host(s) unreachable", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_AnOmittedHostIsNarrowedNeverQuiet()
    {
        // Blocker 5: a run that omits a previously-collected host is looking at less of the fleet than it
        // has seen, so its first line must not say QUIET. It leads with NARROWED — the same token the
        // trailer line and the exit code use — even when nothing it could see needs a person.
        string summary = WaitingCommand.Summary([Row(Holding())], [], ["fernie"]);

        Assert.StartsWith("NARROWED", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("QUIET", summary, StringComparison.Ordinal);
        Assert.Contains("1 host(s) not collected this run", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_RowsBesideAnUnreachableHostStillLeadWithThePartialSweep()
    {
        string summary = WaitingCommand.Summary([Row(Ready()), Row(Holding(), "night:2")], ["fernie: no server running"]);

        Assert.StartsWith("PARTIAL", summary, StringComparison.Ordinal);

        // The rows are still counted honestly — they are just counted as what was visible.
        Assert.Contains("1 of 2 visible window(s) need you", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Collect_AQuietHostBesideAnUnreachableOneIsAPartialSweep()
    {
        // Nothing was collected anywhere, but one host was read and one was not, so this is not the
        // total-failure path — and it is exactly the shape that used to print QUIET on the first line
        // with the reason buried under the budget on the last.
        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            ["fernie", "banff"],
            (host, _) => host == "fernie"
                ? throw new TmuxUnavailableException("fernie: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([]),
            TestContext.Current.CancellationToken);

        Assert.Empty(collected.Panes);
        Assert.False(collected.TotalFailure);
        Assert.True(collected.AnyFailure);
        Assert.StartsWith("PARTIAL", WaitingCommand.Summary([], collected.Unreachable), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_APartialSweepStillPrintsEveryRowAndNamesEveryUnreachableHost()
    {
        string text = Render([Row(Ready())], "fernie: no server running", "banff: connection refused");

        Assert.StartsWith("PARTIAL 2 host(s) unreachable", text, StringComparison.Ordinal);
        Assert.Contains("night:1 pr4595", text, StringComparison.Ordinal);
        Assert.Contains("READY", text, StringComparison.Ordinal);
        Assert.Contains("UNREACHABLE fernie: no server running", text, StringComparison.Ordinal);
        Assert.Contains("UNREACHABLE banff: connection refused", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_EveryReportedRowIsCountedExactlyOnce()
    {
        // The counts partition the table, so they add up to the rows printed under them. Taking the second
        // from assurance instead of from the first left a hole: a high-confidence row that is merely
        // holding may not be acted on and is not unsure, so it was counted in neither.
        string text = Render([Row(Ready()), Row(Holding(), "night:2"), Row(Unsure(), "night:3")]);

        Assert.Contains("1 row(s) met the bar to act, 2 did not", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_AWindowNameCannotForgeARow()
    {
        // A window name is arbitrary text an agent sets, and the report is line-oriented, so a newline in
        // one prints a second line that reads exactly like the tool's own summary.
        string text = Render([Row(Ready(), windowName: "pr4595\nATTENTION 9 of 9 window(s) need you")]);

        // Exactly one ATTENTION line: the real summary, which this row's own verdict produced.
        Assert.Single(text.Split('\n'), l => l.StartsWith("ATTENTION", StringComparison.Ordinal));
        Assert.Contains(@"pr4595\nATTENTION", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_ACarriageReturnCannotOverwriteTheLineAlreadyPrinted()
    {
        string text = Render([Row(Ready(), target: "night:1\rQUIET 0 window(s), none need you")]);

        Assert.DoesNotContain('\r', text);
        Assert.Contains(@"night:1\rQUIET", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_AnEscapeSequenceInADefectCannotDriveTheTerminal()
    {
        // The defect quotes the record back verbatim, which is the point of it — so the record is where
        // an ESC arrives. `ESC[2J` clears the reader's screen; the reader is who this row is for.
        AgentState record = AgentState.Parse("pr=4595 head=722512e25 reviews=2/2 blocked=\u001b[2J rec=merge", "pr4595")!;
        string text = Render([Row(Unsure(), record: record)]);

        Assert.NotEmpty(record.Defects);
        Assert.DoesNotContain('\u001b', text);
        Assert.Contains(@"\e[2J", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_AnUnreachableMessageCarriesARemotesStderrAndIsEscapedToo()
    {
        string text = Render([], "fernie: \u001b[2Jgone\nUNREACHABLE banff: invented");

        Assert.DoesNotContain('\u001b', text);
        Assert.Single(text.Split('\n'), l => l.StartsWith("UNREACHABLE", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteTable_OrdinaryTextIsUntouchedAndTheColumnsStayAligned()
    {
        string text = Render([Row(Ready(), target: "night:1", windowName: "pr4595"), Row(Holding(), "a-much-longer-session:12", "pr4600")]);

        string[] rows = [.. text.Split('\n').Where(l => l.Contains("pr459", StringComparison.Ordinal) || l.Contains("pr4600", StringComparison.Ordinal))];
        Assert.Equal(2, rows.Length);

        // The state column starts at the same offset on both rows: escaping happens before the widths are
        // measured, so the padded width is the width actually printed.
        Assert.Equal(rows[0].IndexOf("#", StringComparison.Ordinal), rows[1].IndexOf("#", StringComparison.Ordinal));
    }

    [Fact]
    public void WriteJson_IsStructurallySafeAndKeepsTheValueTheAgentPublished()
    {
        // The consumer is a program, and Utf8JsonWriter already makes a newline or an ESC unable to end a
        // string. So the value travels intact rather than in this tool's terminal rendering of it.
        const string Hostile = "pr4595\n\u001b[2J\"},{\"window\":\"forged";
        string json = RenderJson([Row(Ready(), windowName: Hostile)]);

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement row = Assert.Single(parsed.RootElement.GetProperty("rows").EnumerateArray().ToArray());
        Assert.Equal(Hostile, row.GetProperty("window").GetString());
    }

    [Fact]
    public void WriteJson_CarriesEveryUnreachableHost()
    {
        string json = RenderJson([], "fernie: no server running", "banff: connection refused");

        using JsonDocument parsed = JsonDocument.Parse(json);
        Assert.Equal(
            ["fernie: no server running", "banff: connection refused"],
            parsed.RootElement.GetProperty("unreachable").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void WriteJsonError_IsStillJson()
    {
        using var stream = new MemoryStream();
        WaitingCommand.WriteJsonError(stream, "fernie: no server running; banff: connection refused");

        using JsonDocument parsed = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        Assert.Contains("fernie", parsed.RootElement.GetProperty("error").GetString()!, StringComparison.Ordinal);
        Assert.Empty(parsed.RootElement.GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("night:1", "night:1")]
    [InlineData("caf\u00e9 \u2014 ok", "caf\u00e9 \u2014 ok")]
    [InlineData("a\nb", @"a\nb")]
    [InlineData("a\rb", @"a\rb")]
    [InlineData("a\tb", @"a\tb")]
    [InlineData("a\u001b[2Jb", @"a\e[2Jb")]
    [InlineData("a\u0007b", @"a\x07b")]
    [InlineData("a\u009bb", @"a\x9bb")]
    [InlineData("a\u2028b", @"a\u2028b")]
    public void Safe_EscapesWhatATerminalActsOnAndNothingElse(string value, string expected)
        => Assert.Equal(expected, DisplayText.Safe(value));

    [Theory]
    [InlineData("pr\u202e4595", @"pr\u202e4595")]                       // RIGHT-TO-LEFT OVERRIDE
    [InlineData("pr\u202d4595", @"pr\u202d4595")]                       // LEFT-TO-RIGHT OVERRIDE
    [InlineData("pr\u202a4595\u202c", @"pr\u202a4595\u202c")]           // embedding and its pop
    [InlineData("pr\u2066ready\u2069", @"pr\u2066ready\u2069")]         // first-strong isolate
    [InlineData("\u2067rtl\u2069", @"\u2067rtl\u2069")]
    [InlineData("\u2068n\u2069", @"\u2068n\u2069")]
    [InlineData("a\u200eb\u200fc", @"a\u200eb\u200fc")]                 // LRM / RLM
    [InlineData("a\u061cb", @"a\u061cb")]                               // ARABIC LETTER MARK
    [InlineData("\ufeffnight:1", @"\ufeffnight:1")]                     // BOM, invisible where it lands
    [InlineData("a\u200bb", @"a\u200bb")]                               // zero width space
    [InlineData("a\u00adb", @"a\xadb")]                                 // soft hyphen, spelled like any other latin-1 code point
    public void Safe_EscapesTheFormatCharactersThatReorderARow(string value, string expected)
        // None of these are control characters, so all of them printed verbatim — and a terminal
        // implements bidi, so a single U+202E prints the rest of the line reversed. A row an operator
        // reads as its own opposite is worse than one they cannot read.
        => Assert.Equal(expected, DisplayText.Safe(value));

    [Theory]
    [InlineData("caf\u00e9")]                                           // ordinary non-ASCII
    [InlineData("\u65e5\u672c\u8a9e")]
    [InlineData("\u2014 \u2192 \u00b1")]                                // punctuation and symbols
    [InlineData("done \ud83c\udf89")]                                   // U+1F389, a supplementary emoji
    [InlineData("\ud83d\udc68\ud83c\udffd")]                            // emoji plus a skin-tone modifier
    public void Safe_LeavesPrintableTextAloneIncludingSupplementaryCharacters(string value)
        // Escaping is for what a terminal executes or lays out. A surrogate pair is one code point, so it
        // is judged whole: judging its halves would spell an emoji out as two lone surrogates.
        => Assert.Equal(value, DisplayText.Safe(value));

    [Theory]
    [InlineData("a\U000e0001b", @"a\U000e0001b")]                       // LANGUAGE TAG
    [InlineData("a\U000e0065b", @"a\U000e0065b")]                       // TAG LATIN SMALL LETTER E
    [InlineData("a\U0001d173b", @"a\U0001d173b")]                       // MUSICAL SYMBOL BEGIN BEAM
    public void Safe_SpellsOutASupplementaryFormatCodePointAsOne(string value, string expected)
        // The other half of walking by code point: these are format characters above the BMP, and each
        // arrives as a surrogate pair whose halves look like ordinary unassigned chars on their own.
        => Assert.Equal(expected, DisplayText.Safe(value));

    [Fact]
    public void Safe_AnUnpairedSurrogateIsNotACharacterAndIsSpelledOut()
    {
        // A lone half of a pair can arrive from a truncated capture. It is not text, and printing it
        // leaves the reader with a replacement box in a column that is supposed to be legible.
        Assert.Equal(@"a\ud83cb", DisplayText.Safe("a\ud83cb"));
        Assert.Equal(@"a\udf89", DisplayText.Safe("a\udf89"));
    }

    [Fact]
    public void WriteTable_ABidiOverrideInAWindowNameCannotReorderTheRow()
    {
        string text = Render([Row(Ready(), windowName: "pr4595\u202eyduaerton")]);

        Assert.DoesNotContain('\u202e', text);
        Assert.Contains(@"pr4595\u202eyduaerton", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Safe_TreatsAMissingValueAsEmpty()
        => Assert.Equal(string.Empty, DisplayText.Safe(null));

    [Fact]
    public void WriteTable_ARecordThatNamesNothingIsPrintedWithoutAPrNumber()
    {
        // The window named `worker` publishing `pr=none head=pending rec=stop`. It is in the table because
        // the request is real; its PR column is the same `-` an unidentified row has always printed,
        // because there is no number and inventing #0 would be a second wrong fact in a row about
        // wrongness.
        UnidentifiedState unusable = AgentState.Read("pr=none head=pending rec=stop", "worker").Unidentified!;
        string text = Render([Row(WaitingVerdict.Unidentified(unusable), windowName: "worker", unidentified: unusable)]);

        string row = Assert.Single(text.Split('\n'), l => l.Contains("worker", StringComparison.Ordinal));
        Assert.DoesNotContain("#0", row, StringComparison.Ordinal);
        Assert.Contains("NEEDSOPERATOR", row, StringComparison.Ordinal);
        Assert.Contains("low", row, StringComparison.Ordinal);

        // The defects are the detail: what it asked for, and every way the record failed to say about what.
        Assert.Contains("pr=none is not a PR number", row, StringComparison.Ordinal);
        Assert.Contains("head=pending is not a sha", row, StringComparison.Ordinal);
        Assert.Contains("ATTENTION 1 of 1", text, StringComparison.Ordinal);
        Assert.Contains("0 row(s) met the bar to act, 1 did not", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteJson_ARecordThatNamesNothingCarriesNoPrField()
    {
        // A consumer reads `pr` as a PR. A `0` or a `-1` there would be one, so the key is absent — and
        // what can still be said without an identity is still said.
        UnidentifiedState unusable = AgentState.Read("pr=none head=pending rec=stop", "worker").Unidentified!;
        string json = RenderJson([Row(WaitingVerdict.Unidentified(unusable), windowName: "worker", unidentified: unusable)]);

        using JsonDocument parsed = JsonDocument.Parse(json);
        JsonElement row = Assert.Single(parsed.RootElement.GetProperty("rows").EnumerateArray().ToArray());

        Assert.False(row.TryGetProperty("pr", out _));
        Assert.False(row.TryGetProperty("source", out _));
        Assert.Equal("stop", row.GetProperty("rec").GetString());
        Assert.Equal("needsoperator", row.GetProperty("state").GetString());
        Assert.False(row.GetProperty("mayAct").GetBoolean());
        Assert.NotEmpty(row.GetProperty("defects").EnumerateArray().ToArray());
    }

    [Fact]
    public void WriteJson_ARenameDoesNotWriteToTheJsonStream()
    {
        // The blocking finding, from the JSON side: `--json --rename` must leave a single valid JSON
        // document on stdout. WriteJson is the only thing that writes stdout, and it emits nothing but the
        // document — the rename diagnostics are written to a separate sink (stderr in production).
        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, [Row(Ready())], NoBudget, []);
        string stdout = Encoding.UTF8.GetString(stream.ToArray());

        Assert.DoesNotContain("RENAMED", stdout, StringComparison.Ordinal);
        using JsonDocument parsed = JsonDocument.Parse(stdout);
        Assert.True(parsed.RootElement.TryGetProperty("rows", out _));
    }
}
