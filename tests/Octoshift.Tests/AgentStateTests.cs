namespace Octoshift.Tests;

using Octoshift.Waiting;
using Xunit;

/// <summary>
/// Reading a window's <c>@agent_state</c>. Every fixture here is a verbatim option value taken from the
/// live fleet the day after the convention was published, which is why several of them are malformed:
/// partial adoption is the condition the reader has to work in, not an edge case.
/// </summary>
public class AgentStateTests
{
    [Fact]
    public void Parse_ReadsAWellFormedRecord()
    {
        AgentState? state = AgentState.Parse("pr=4626 head=f4a8d1c84 round=1 reviews=2/2 blocked=4629 rec=wait", "pr4626");

        Assert.NotNull(state);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Equal(4626, state.PrNumber);
        Assert.Equal("f4a8d1c84", state.Head);
        Assert.Equal(1, state.Round);
        Assert.Equal(2, state.ReviewsClean);
        Assert.Equal(2, state.ReviewsRequired);
        Assert.True(state.ReviewsMeetBar);
        Assert.Equal([4629], state.Blocked);
        Assert.Equal(Recommendation.Wait, state.Recommendation);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_FlagsANonCitableBlocker()
    {
        // The field exists so a reader can open the thing and a second agent hitting the same wall can
        // find it. "ci" names nothing openable.
        AgentState? state = AgentState.Parse("pr=4142 head=872837ba6 round=16 reviews=2/2 blocked=ci rec=wait", "pr4142");

        Assert.NotNull(state);
        Assert.Empty(state.Blocked);
        Assert.Contains(state.Defects, d => d.Contains("blocked=ci", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsMergeRecommendedBeforeTheReviewsAreIn()
    {
        AgentState? state = AgentState.Parse("pr=3967 head=6a20ad7c4 round=20 reviews=0/2 rec=merge", "pr3967");

        Assert.NotNull(state);
        Assert.Equal(Recommendation.Merge, state.Recommendation);
        Assert.False(state.ReviewsMeetBar);
        Assert.Contains(state.Defects, d => d.Contains("rec=merge with reviews=0/2", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_AcceptsContinue()
    {
        // The fleet's contract added `continue` after this reader was first written; it is a real value.
        AgentState? state = AgentState.Parse("pr=4618 head=25c9a13a round=3 reviews=0/2 rec=continue", "pr4618");

        Assert.NotNull(state);
        Assert.Equal(Recommendation.Continue, state.Recommendation);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_FlagsAnUnrecognisedRecommendation()
    {
        AgentState? state = AgentState.Parse("pr=4618 head=25c9a13a reviews=0/2 rec=probably", "pr4618");

        Assert.NotNull(state);
        Assert.Equal(Recommendation.Unrecognised, state.Recommendation);
        Assert.Contains(state.Defects, d => d.Contains("rec=probably", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_AWaitingPredicateSatisfiesWaitWithoutACitableBlocker()
    {
        // The case that produced `blocked=ci` in the field: a check that has not reported is not a defect
        // and does not deserve an issue, so it belongs in waiting rather than blocked.
        AgentState? state = AgentState.Parse("pr=4142 head=872837ba6 reviews=2/2 waiting=check:ci-required rec=wait", "pr4142");

        Assert.NotNull(state);
        Assert.Equal(WaitKind.Check, state.Waiting.Kind);
        Assert.Equal("ci-required", state.Waiting.CheckName);
        Assert.Empty(state.Defects);
    }

    [Theory]
    [InlineData("checks", "checks")]
    [InlineData("merge", "merge")]
    [InlineData("review", "review")]
    [InlineData("check:test-windows", "check:test-windows")]
    public void Parse_ReadsEveryWaitPredicate(string value, string expected)
        => Assert.Equal(expected, AgentState.Parse($"pr=1 head=abc1234 waiting={value}", "pr1")!.Waiting.ToString());

    [Fact]
    public void Parse_FlagsAnUnevaluableWaitPredicate()
    {
        AgentState? state = AgentState.Parse("pr=1 head=abc1234 waiting=ci", "pr1");

        Assert.NotNull(state);
        Assert.Equal(WaitKind.None, state.Waiting.Kind);
        Assert.Contains(state.Defects, d => d.Contains("waiting=ci", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TracksAnIssueBeforeAPrExists()
    {
        // A worker branch is local until the coordinator pushes it, so early rounds have no PR.
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 rec=continue", "i4611");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4611, state.PrNumber);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_FlagsWaitWithNothingToWaitOn()
    {
        AgentState? state = AgentState.Parse("pr=4600 head=abc1234 reviews=1/2 rec=wait", "pr4600");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("nothing in blocked or waiting", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TreatsAnEmptyBlockedAsAbsent()
    {
        // `blocked=` is an agent saying "nothing here"; it should read the same as omitting the field and
        // must not be reported as a malformed entry.
        AgentState? state = AgentState.Parse("pr=4625 head=05e61730 round=1 reviews=2/2 blocked= rec=merge", "pr4625");

        Assert.NotNull(state);
        Assert.Empty(state.Blocked);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_AcceptsAFullLengthHead()
    {
        AgentState? state = AgentState.Parse(
            "pr=4130 head=0fbdf15cd90b9d05e03056a81427ec43ccad77cd round=27 reviews=0/2", "pr4130");

        Assert.NotNull(state);
        Assert.Equal("0fbdf15cd90b9d05e03056a81427ec43ccad77cd", state.Head);
    }

    [Theory]
    [InlineData("blocked")]
    [InlineData("waiting-ci")]
    [InlineData("")]
    [InlineData(null)]
    public void Parse_FallsBackToTheWindowNameWhenTheOptionIsNotFields(string? option)
    {
        // Free text in the option is common during rollout. The window name still identifies the PR, and
        // it is a better anchor than the pane: set once, unaffected by the report scrolling away.
        AgentState? state = AgentState.Parse(option, "pr4453");

        Assert.NotNull(state);
        Assert.Equal(StateSource.WindowName, state.Source);
        Assert.Equal(4453, state.PrNumber);
        Assert.Equal(Recommendation.None, state.Recommendation);
    }

    [Theory]
    [InlineData("pr4551-blocked", 4551)]
    [InlineData("pr4610-blocked", 4610)]
    [InlineData("PR4610", 4610)]
    public void Parse_ReadsThePrThroughAStateSuffix(string windowName, int expected)
        => Assert.Equal(expected, AgentState.Parse(null, windowName)?.PrNumber);

    [Theory]
    [InlineData("i4611")]
    [InlineData("resolve-ci-test-flakes")]
    [InlineData("zsh")]
    [InlineData(null)]
    public void Parse_ReturnsNullWhenNothingIdentifiesAPr(string? windowName)
        => Assert.Null(AgentState.Parse(null, windowName));

    [Fact]
    public void Parse_FlagsAWindowNameThatDisagreesWithTheRecord()
    {
        // Four windows on one host carried another window's state verbatim, from a `tmux set` that
        // omitted -t "$TMUX_PANE". The disagreement is the only way to see it from outside.
        AgentState? state = AgentState.Parse("pr=4560 head=11e4e257c round=4 reviews=0/2", "pr4488");

        Assert.NotNull(state);
        Assert.Equal(4560, state.PrNumber);
        Assert.Contains(state.Defects, d => d.Contains("window is named pr4488", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsANonShaHead()
    {
        AgentState? state = AgentState.Parse("pr=4600 head=HEAD reviews=1/2", "pr4600");

        Assert.NotNull(state);
        Assert.Null(state.Head);
        Assert.Contains(state.Defects, d => d.Contains("head=HEAD", StringComparison.Ordinal));
    }
}
