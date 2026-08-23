namespace Octoshift.Tests;

using Octoshift.Waiting;
using Xunit;

/// <summary>
/// Parsing a stopped agent's final output. The two prose fixtures are verbatim pane captures from a live
/// fleet on 2026-08-22 — they are the shapes that exist in the wild today, before any agent has been asked
/// to emit a sentinel, and they are what the inference tier has to survive.
/// </summary>
public class StatusRecordTests
{
    private const string Bar = "┃";

    /// <summary>Wraps lines in the TUI's box frame the way capture-pane returns them.</summary>
    private static string Boxed(params string[] lines)
        => string.Join('\n', lines.Select(l => l.PadRight(120) + Bar));

    [Fact]
    public void Parse_ReadsEveryDeclaredField()
    {
        StatusRecord? record = StatusRecord.Parse(
            "NIGHTSHIFT-STATUS pr=4595 head=722512e25 round=2 verdict=gated waiting=check:ci-required next=round-2-review at=2026-08-23T03:44Z");

        Assert.NotNull(record);
        Assert.Equal(RecordSource.Declared, record.Source);
        Assert.Equal(4595, record.PrNumber);
        Assert.Equal("722512e25", record.Head);
        Assert.Equal(2, record.Round);
        Assert.Equal("gated", record.Verdict);
        Assert.Equal(PredicateKind.Check, record.Waiting.Kind);
        Assert.Equal("ci-required", record.Waiting.CheckName);
        Assert.Equal("round-2-review", record.Next);
        Assert.Equal(new DateTimeOffset(2026, 8, 23, 3, 44, 0, TimeSpan.Zero), record.At);
    }

    [Fact]
    public void Parse_StripsTheBoxFrame()
    {
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "Round 2 is complete for PR 4563.",
            "",
            "NIGHTSHIFT-STATUS pr=4563 head=f5c2b3fac verdict=converging waiting=none next=round-3"));

        Assert.NotNull(record);
        Assert.Equal(RecordSource.Declared, record.Source);
        Assert.Equal(4563, record.PrNumber);
        Assert.Equal("round-3", record.Next);
    }

    [Fact]
    public void Parse_JoinsARecordTheTuiWrapped()
    {
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "NIGHTSHIFT-STATUS pr=4595 head=722512e25 round=2",
            "verdict=gated waiting=check:ci-required next=round-2-review"));

        Assert.NotNull(record);
        Assert.Equal("ci-required", record.Waiting.CheckName);
        Assert.Equal("round-2-review", record.Next);
    }

    [Fact]
    public void Parse_DoesNotSwallowProseFollowingTheRecord()
    {
        // The join must stop at the first row that is not a pure key=value run, or a record trailed by
        // commentary would absorb whatever words happened to contain an '='.
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "NIGHTSHIFT-STATUS pr=4563 head=f5c2b3fac waiting=none next=round-3",
            "Fix description: routed rows to Overview, next=nothing here is a field."));

        Assert.NotNull(record);
        Assert.Equal("round-3", record.Next);
    }

    [Fact]
    public void Parse_LastRecordWins()
    {
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "NIGHTSHIFT-STATUS pr=4563 head=aaaaaaa round=1 waiting=none next=round-2",
            "Round 2 is complete for PR 4563.",
            "NIGHTSHIFT-STATUS pr=4563 head=f5c2b3fac round=2 waiting=none next=round-3"));

        Assert.NotNull(record);
        Assert.Equal(2, record.Round);
        Assert.Equal("f5c2b3fac", record.Head);
    }

    [Fact]
    public void Parse_InfersFromARoundCompleteBlock()
    {
        // Verbatim shape of the completion block agents write today.
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "● Round 2 is complete for PR 4563.",
            "",
            "  • Review models GPT-5.6 Sol and Claude Opus 5 were used for adversarial review.",
            "  • Review feedback is: converging.",
            "  • Round duration: 3:02.",
            "",
            "  Fix description: routed rows to supported Overview, surfaced API truncation, and safely",
            "  skipped implementation-only assemblies. Head  f5c2b3fac  requires round 3."));

        Assert.NotNull(record);
        Assert.Equal(RecordSource.Inferred, record.Source);
        Assert.Equal(4563, record.PrNumber);
        Assert.Equal("f5c2b3fac", record.Head);
        Assert.Equal(PredicateKind.Unknown, record.Waiting.Kind);
        Assert.Null(record.Next);
    }

    [Fact]
    public void Parse_InfersFromAGatedBlock()
    {
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "PR #4595 remains on exact head  722512e25 , conflict-free and mergeable. The Windows rerun",
            "has not produced  ci-required  yet, so Round 2 review remains gated."));

        Assert.NotNull(record);
        Assert.Equal(RecordSource.Inferred, record.Source);
        Assert.Equal(4595, record.PrNumber);
        Assert.Equal("722512e25", record.Head);
    }

    [Fact]
    public void Parse_DoesNotMistakeADigitRunForASha()
    {
        // A date or a build number is 7+ characters and looks hex-shaped until you require a hex letter.
        StatusRecord? record = StatusRecord.Parse("PR #4595 was last built at 20260823 and is still gated.");

        Assert.NotNull(record);
        Assert.Equal(4595, record.PrNumber);
        Assert.Null(record.Head);
    }

    [Fact]
    public void Parse_FallsBackToInferenceWhenTheSentinelLacksAPrNumber()
    {
        StatusRecord? record = StatusRecord.Parse(Boxed(
            "Round 2 is complete for PR 4563.",
            "NIGHTSHIFT-STATUS head=f5c2b3fac waiting=none next=round-3"));

        Assert.NotNull(record);
        Assert.Equal(RecordSource.Inferred, record.Source);
        Assert.Equal(4563, record.PrNumber);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   \n  \n")]
    [InlineData("Build succeeded. 0 warnings, 0 errors.")]
    public void Parse_ReturnsNullWhenNoPrIsIdentifiable(string paneText)
        => Assert.Null(StatusRecord.Parse(paneText));

    // Expectations are the wire form, which keeps the signature free of an internal enum and covers the
    // round-trip through ToString() at the same time.
    [Theory]
    [InlineData("check:ci-required", "check:ci-required")]
    [InlineData("merge", "merge")]
    [InlineData("review", "review")]
    [InlineData("operator", "operator")]
    [InlineData("none", "none")]
    [InlineData("ci", "none")]
    [InlineData("soon", "unknown")]
    [InlineData("check:", "unknown")]
    [InlineData(null, "unknown")]
    public void WaitingPredicate_Parses(string? value, string expected)
        => Assert.Equal(expected, WaitingPredicate.Parse(value).ToString());
}
