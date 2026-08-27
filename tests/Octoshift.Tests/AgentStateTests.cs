namespace Octoshift.Tests;

using Octoshift.GitHub;
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
    public void Parse_FlagsMergeRecommendedOnAnIssueWindow()
    {
        // An issue window has no PR, so `merge` names something that cannot exist yet. Recorded as a
        // defect so the row grades low and reaches the operator rather than resolving to a quiet Holding.
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 reviews=2/2 rec=merge", "i4611");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(Recommendation.Merge, state.Recommendation);
        Assert.Contains(state.Defects, d => d.Contains("rec=merge on issue #4611", StringComparison.Ordinal));
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

    [Theory]
    [InlineData("issue=")]
    [InlineData("reviews=")]
    [InlineData("rec=")]
    [InlineData("head=")]
    public void Parse_EmptyScalarFieldsAreDefective(string field)
    {
        AgentState? state = AgentState.Parse($"pr=4595 {field} head=722512e25 reviews=2/2 rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains($"{field} is empty", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(",")]
    [InlineData("4629,")]
    [InlineData(",4629")]
    [InlineData("4629,,4700")]
    public void Parse_EmptyBlockerEntriesAreDefective(string blocked)
    {
        AgentState? state = AgentState.Parse(
            $"pr=4595 head=722512e25 reviews=2/2 blocked={blocked} rec=merge",
            "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("is not a citable issue or PR number", StringComparison.Ordinal));
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

    [Fact]
    public void Parse_ReadsAnIssueWindowName()
    {
        // Observed live: an agent with no PR wrote `pr=none head=pending` rather than leave the field
        // out. The window was named i4613 the whole time.
        AgentState? state = AgentState.Parse("pr=none head=pending round=0 reviews=0/2 rec=continue", "i4613");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4613, state.PrNumber);
    }

    [Fact]
    public void Parse_KeepsTheRestOfTheRecordWhenOnlyTheWindowNameIdentifiesIt()
    {
        // The blocking finding, on the same observed value with `rec=stop`: identity used to short-circuit
        // the read, so a malformed `pr=` threw away every field beside it — including the one field that
        // was an agent asking to be released. An escalation became a window quietly getting on with things.
        AgentState? state = AgentState.Parse("pr=none head=pending round=0 reviews=0/2 rec=stop", "i4613");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4613, state.PrNumber);
        Assert.Equal(Recommendation.Stop, state.Recommendation);
        Assert.Equal(0, state.Round);
        Assert.Equal(0, state.ReviewsClean);
        Assert.Equal(2, state.ReviewsRequired);

        // Said honestly rather than repaired: the identity is the window's, and both bad fields are named.
        Assert.Equal(StateSource.WindowName, state.Source);
        Assert.Null(state.Head);
        Assert.Contains(state.Defects, d => d.Contains("pr=none", StringComparison.Ordinal));
        Assert.Contains(state.Defects, d => d.Contains("head=pending", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ReadsEveryFieldOnAFallbackIssueWindow()
    {
        AgentState? state = AgentState.Parse(
            "pr=none head=8d5f22a22 round=3 reviews=1/2 waiting=check:ci-required blocked=4700 rec=wait", "i4613");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4613, state.PrNumber);
        Assert.Equal(StateSource.WindowName, state.Source);
        Assert.Equal("8d5f22a22", state.Head);
        Assert.Equal(3, state.Round);
        Assert.Equal(1, state.ReviewsClean);
        Assert.Equal(2, state.ReviewsRequired);
        Assert.Equal(WaitKind.Check, state.Waiting.Kind);
        Assert.Equal([4700], state.Blocked);
        Assert.Equal(Recommendation.Wait, state.Recommendation);

        // `pr=none` is still wrong, and is still the only thing reported wrong.
        Assert.Contains(state.Defects, d => d.Contains("pr=none", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Defects, d => d.Contains("nothing in blocked or waiting", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ContinueOnAFallbackIssueWindowIsStillContinue()
    {
        AgentState? state = AgentState.Parse("pr=none head=8d5f22a22 round=2 reviews=0/2 rec=continue", "i4613");

        Assert.NotNull(state);
        Assert.Equal(Recommendation.Continue, state.Recommendation);
        Assert.Equal(2, state.Round);
        Assert.Equal("8d5f22a22", state.Head);
    }

    [Fact]
    public void Parse_ReadsTheWholeRecordOfAnIssueThatDeclaredItself()
    {
        // The declared issue path dropped the same fields for the same reason. An issue window publishes
        // rounds and reviews long before a PR exists; they are the only account of what it is doing.
        AgentState? state = AgentState.Parse(
            "issue=4611 head=8d5f22a22 round=4 reviews=1/2 waiting=review rec=wait", "i4611");

        Assert.NotNull(state);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Equal(4, state.Round);
        Assert.Equal(1, state.ReviewsClean);
        Assert.Equal(WaitKind.Review, state.Waiting.Kind);
        Assert.Equal(Recommendation.Wait, state.Recommendation);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_AnIssueBlockedOnItselfIsNotCalledAPr()
    {
        // Self-reference is as wrong here as on a PR, but an issue window has no PR — calling its issue
        // one puts a second wrong fact in a message about wrongness.
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 blocked=4611 rec=wait", "i4611");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("its own issue #4611", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Defects, d => d.Contains("PR", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_KeepsTheRestOfTheRecordWhenOnlyTheWindowNameIdentifiesAPr()
    {
        // The same rule on the PR side: a window named pr4595 whose `pr=` is unusable still published a
        // head and a count, and they still describe this window.
        AgentState? state = AgentState.Parse("pr=#4595 head=722512e25 reviews=2/2 rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.False(state.IsIssue);
        Assert.Equal(4595, state.PrNumber);
        Assert.Equal(StateSource.WindowName, state.Source);
        Assert.Equal("722512e25", state.Head);
        Assert.True(state.ReviewsMeetBar);
        Assert.Equal(Recommendation.Merge, state.Recommendation);
        Assert.Contains(state.Defects, d => d.Contains("pr=#4595", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsAPrBlockedOnItself()
    {
        AgentState? state = AgentState.Parse("pr=4636 head=c11c50d91 reviews=1/2 blocked=4636 rec=approve", "pr4636");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("its own PR #4636", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("resolve-ci-test-flakes")]
    [InlineData("zsh")]
    [InlineData(null)]
    public void Parse_ReturnsNullWhenNothingIdentifiesAPr(string? windowName)
        => Assert.Null(AgentState.Parse(null, windowName));

    [Fact]
    public void Parse_FlagsStateThatTheWindowsOwnOutputContradicts()
    {
        // The failure TmuxWindows.tla exposed: an untargeted publish clobbers a neighbour's state AND
        // name together, so the two durable channels agree with each other about a PR that window never
        // touched, and there is nothing in them to notice. Its own output is what disagrees, because a
        // process writes to its own terminal and nowhere else.
        AgentState? state = AgentState.Parse(
            "pr=4551 head=abc1234 reviews=1/2 rec=wait", "pr4551",
            paneContradictsPr: _ => true);

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("never mentions pr=4551", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_SilenceFromAnEmptyPaneIsNotContradiction()
    {
        // This UI runs on the alternate screen with no scrollback, so a window whose report has
        // scrolled past the top shows nothing but chrome. Measured across the fleet, treating that as
        // disagreement flagged a window whose state was perfectly good.
        AgentState? state = AgentState.Parse(
            "pr=4448 head=abc1234 reviews=0/2 rec=continue", "pr4448",
            paneContradictsPr: _ => false);

        Assert.NotNull(state);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_ADuplicatedWindowNameIdentifiesNothing()
    {
        // Observed live on fernie: two windows both named pr4551-blocked, one of them actually working
        // on 4663. A rename had landed on a neighbour. With no published state the name is the only
        // identity available, and a duplicated name is not one.
        Assert.Null(AgentState.Parse(null, "pr4551-blocked", nameIsAmbiguous: true));
        Assert.NotNull(AgentState.Parse(null, "pr4551-blocked", nameIsAmbiguous: false));
    }

    [Fact]
    public void Parse_ADuplicatedNameIsADefectEvenWhenTheRecordIsAuthoritative()
    {
        // The published state still identifies the window correctly, so the row is usable — but the
        // collision is evidence that some agent is renaming windows it does not own, which is worth
        // saying out loud rather than silently preferring the record.
        AgentState? state = AgentState.Parse(
            "pr=4663 head=0ab4d7473 round=2 reviews=0/2 rec=wait", "pr4551-blocked", nameIsAmbiguous: true);

        Assert.NotNull(state);
        Assert.Equal(4663, state.PrNumber);
        Assert.Contains(state.Defects, d => d.Contains("shares the name", StringComparison.Ordinal));
    }

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
    public void Parse_FlagsAPrRecordInAnIssueWindow()
    {
        // The other half of "another window's state, verbatim": the kind can disagree as well as the
        // number, and while only PR-against-PR numbers were compared this was invisible — a PR record sat
        // in an issue window looking perfectly coherent.
        AgentState? state = AgentState.Parse("pr=4626 head=f4a8d1c84 round=1 reviews=2/2 rec=merge", "i4613");

        Assert.NotNull(state);
        Assert.False(state.IsIssue);
        Assert.Equal(4626, state.PrNumber);
        Assert.Contains(state.Defects, d => d.Contains("named i4613", StringComparison.Ordinal) && d.Contains("declares a PR", StringComparison.Ordinal));

        // Both halves disagree here, and both are said.
        Assert.Contains(state.Defects, d => d.Contains("the record says pr=4626", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsAnIssueRecordInAPrWindow()
    {
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 round=2 rec=continue", "pr4611");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4611, state.PrNumber);

        // The numbers agree, so the kind is the only thing wrong and the only thing reported.
        Assert.Contains(state.Defects, d => d.Contains("named pr4611", StringComparison.Ordinal) && d.Contains("declares an issue", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Defects, d => d.Contains("the record says", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsAnIssueWindowNameThatDisagreesWithTheRecord()
    {
        // Numbers were only ever compared on the PR side, so one issue window carrying another's state
        // passed as clean.
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 rec=continue", "i4613");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4611, state.PrNumber);
        Assert.Contains(state.Defects, d => d.Contains("named i4613", StringComparison.Ordinal) && d.Contains("the record says issue=4611", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Defects, d => d.Contains("declares", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_AMalformedPrDoesNotSuppressAValidIssue()
    {
        // The two identity fields used to be read as one — `issue` was consulted only when no `pr` key was
        // present — so `pr=none` next to a perfectly good `issue=` threw the issue away and fell back to
        // the window name. A malformed field is a defect in that field, not evidence about its neighbour.
        AgentState? state = AgentState.Parse("pr=none issue=4611 head=8d5f22a22 round=3 rec=continue", "i4611");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4611, state.PrNumber);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Equal(3, state.Round);

        // Still wrong, and still said.
        Assert.Contains(state.Defects, d => d.Contains("pr=none", StringComparison.Ordinal));
        Assert.DoesNotContain(state.Defects, d => d.Contains("window is named", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_FlagsARecordThatDeclaresBothIdentities()
    {
        // A window tracks one thing. Two identities is a contradiction in the record, settled by a fixed
        // rule (`pr` is the later claim about the same work) and reported rather than repaired — so it
        // cannot come out looking corroborated even when the window name agrees with one of them.
        AgentState? state = AgentState.Parse("pr=4626 issue=4611 head=f4a8d1c84 reviews=2/2 rec=merge", "i4611");

        Assert.NotNull(state);
        Assert.False(state.IsIssue);
        Assert.Equal(4626, state.PrNumber);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Contains(state.Defects, d => d.Contains("declares both pr=4626 and issue=4611", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_AnIdentityMismatchIsNotActionable()
    {
        // The defect has to reach the confidence logic, or the row is still something a tool would speak
        // to unattended on the strength of a number that belongs to another window.
        AgentState state = AgentState.Parse("pr=4626 head=f4a8d1c84 round=1 reviews=2/2 rec=merge", "i4613")!;

        WaitingVerdict verdict = WaitingVerdict.Resolve(state, new PrFacts
        {
            Number = 4626,
            HeadSha = "f4a8d1c84000000000000000000000000000000a",
            State = "open",
            Merged = false,
            MergeableState = "clean",
            Checks = [],
        });

        Assert.Equal(Confidence.Low, verdict.Assurance.Level);
        Assert.False(verdict.MayAct);
        Assert.Equal(WaitingState.Untrustworthy, verdict.State);
    }

    [Fact]
    public void Parse_RepeatedIdentityFieldsAreDefective()
    {
        AgentState? state = AgentState.Parse(
            "pr=4595 pr=4626 head=722512e25 reviews=2/2 rec=merge",
            "pr4595");

        Assert.NotNull(state);
        Assert.Equal(4595, state.PrNumber);
        Assert.Contains(state.Defects, d => d.Contains("field 'pr' is declared more than once", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("i1", "pr=1", "named i1")]
    [InlineData("i1234567", "pr=1234567", "named i1234567")]
    public void Parse_WindowIdentityRangeMatchesDeclaredIdentity(string window, string declared, string expected)
    {
        AgentState? state = AgentState.Parse($"{declared} head=722512e25 reviews=2/2 rec=merge", window);

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains(expected, StringComparison.Ordinal) && d.Contains("declares a PR", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("pr0")]
    [InlineData("i0")]
    public void Parse_RejectsZeroWindowIdentity(string window)
        => Assert.Null(AgentState.Parse(null, window));

    [Fact]
    public void Parse_MergeRecommendationWhileBlockedIsDefective()
    {
        AgentState? state = AgentState.Parse(
            "pr=4595 head=722512e25 reviews=2/2 blocked=4629 rec=merge",
            "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("rec=merge while blocked on #4629", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("3/2")]
    [InlineData("0/1")]
    [InlineData("-1/2")]
    public void Parse_ReviewCountsOutsideTheContractAreDefective(string reviews)
    {
        AgentState? state = AgentState.Parse($"pr=4595 head=722512e25 reviews={reviews} rec=continue", "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains($"reviews={reviews}", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ACoherentPrRecordStaysClean()
    {
        AgentState? state = AgentState.Parse("pr=4626 head=f4a8d1c84 round=1 reviews=2/2 rec=merge", "pr4626");

        Assert.NotNull(state);
        Assert.False(state.IsIssue);
        Assert.Equal(4626, state.PrNumber);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_ACoherentIssueRecordStaysClean()
    {
        AgentState? state = AgentState.Parse("issue=4611 head=8d5f22a22 round=2 reviews=1/2 waiting=review rec=wait", "i4611");

        Assert.NotNull(state);
        Assert.True(state.IsIssue);
        Assert.Equal(4611, state.PrNumber);
        Assert.Equal(StateSource.Declared, state.Source);
        Assert.Empty(state.Defects);
    }

    [Fact]
    public void Parse_FlagsANonShaHead()
    {
        AgentState? state = AgentState.Parse("pr=4600 head=HEAD reviews=1/2", "pr4600");

        Assert.NotNull(state);
        Assert.Null(state.Head);
        Assert.Contains(state.Defects, d => d.Contains("head=HEAD", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ASpaceInAValueSplitsIntoAFragmentAndIsDefective()
    {
        // The one that inverts a record's meaning: whitespace is the field separator and values carry no
        // spaces, so `blocked= 4629` arrives as the accepted empty sentinel plus a stray token. Read
        // leniently that is "nothing is blocking me" — the opposite of what was typed — with the number
        // silently gone. There is deliberately no quoting to rescue it; the record is malformed.
        AgentState? state = AgentState.Parse("pr=4595 head=722512e25 reviews=2/2 blocked= 4629 rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.Empty(state.Blocked);
        Assert.Contains(state.Defects, d => d.Contains("'4629' is not a key=value field", StringComparison.Ordinal));

        // And the point of saying so: it can no longer be a high-confidence, actionable claim of done.
        WaitingVerdict verdict = WaitingVerdict.Resolve(state, new PrFacts
        {
            Number = 4595,
            HeadSha = "722512e25f0c1d4a9b8e7360a1c2d3e4f5061728",
            State = "open",
            MergeableState = "clean",
        });

        Assert.Equal(WaitingState.Untrustworthy, verdict.State);
        Assert.Equal(Confidence.Low, verdict.Assurance.Level);
        Assert.False(verdict.MayAct);
    }

    [Fact]
    public void Parse_AMisspelledFieldIsDefectiveRatherThanIgnored()
    {
        // Dropped silently, `blockd=4629` is a coherent record with no blocker at all — which is the
        // opposite of what the agent wrote, and coherent enough to be acted on.
        AgentState? state = AgentState.Parse("pr=4595 head=722512e25 reviews=2/2 blockd=4629 rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.Empty(state.Blocked);
        Assert.Contains(state.Defects, d => d.Contains("field 'blockd' is not one of", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("next=round-3")]
    [InlineData("next=")]
    [InlineData("NEXT=round-3")]
    public void Parse_AFieldOutsideTheSchemaIsDefectiveEvenWhenEmpty(string field)
    {
        AgentState? state = AgentState.Parse($"pr=4595 head=722512e25 reviews=2/2 {field} rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("is not one of", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_TheKnownKeysAreExactlyTheSchema()
    {
        // The list in the message is the contract an agent is being held to, so it is asserted rather
        // than left to drift from the fields the reader actually consumes.
        AgentState? state = AgentState.Parse("pr=4595 head=722512e25 reviews=2/2 unknown=x rec=merge", "pr4595");

        Assert.NotNull(state);
        string defect = Assert.Single(state.Defects);
        Assert.Equal("field 'unknown' is not one of pr|issue|head|round|reviews|blocked|waiting|rec", defect);
    }

    [Theory]
    [InlineData("=4629")]
    [InlineData("=")]
    public void Parse_AFieldWithNoNameIsDefective(string token)
    {
        AgentState? state = AgentState.Parse($"pr=4595 head=722512e25 reviews=2/2 {token} rec=merge", "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("declares no field name", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_ProseInTheRecordIsDefective()
    {
        AgentState? state = AgentState.Parse("pr=4595 head=722512e25 reviews=2/2 rec=merge still converging", "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("'still' is not a key=value field", StringComparison.Ordinal));
        Assert.Contains(state.Defects, d => d.Contains("'converging' is not a key=value field", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("0", 0)]
    [InlineData("1", 1)]
    [InlineData("27", 27)]
    public void Parse_ReadsARoundNumber(string value, int expected)
    {
        AgentState? state = AgentState.Parse($"pr=4595 head=722512e25 round={value} reviews=1/2", "pr4595");

        Assert.NotNull(state);
        Assert.Equal(expected, state.Round);
        Assert.Empty(state.Defects);
    }

    [Theory]
    [InlineData("-1")]              // a round before the first one
    [InlineData("next")]
    [InlineData("1.5")]
    [InlineData("3rd")]
    [InlineData(" 3")]              // trimmed to a bare fragment, so never a value at all
    [InlineData("99999999999999")]  // past int, and a bare TryParse read this as "no round declared"
    [InlineData("01")]              // one value, one spelling: the tmux zero sentinel rule
    public void Parse_ARoundOutsideItsDomainIsDefective(string value)
    {
        AgentState? state = AgentState.Parse($"pr=4595 head=722512e25 round={value} reviews=1/2", "pr4595");

        Assert.NotNull(state);
        Assert.Null(state.Round);
        Assert.NotEmpty(state.Defects);
    }

    [Fact]
    public void Parse_MergeRecommendationWhileWaitingIsDefective()
    {
        // `blocked` and `waiting` differ only in who can act on them; either one is the record still
        // asserting something is outstanding, which cannot be true beside "merge this".
        AgentState? state = AgentState.Parse(
            "pr=4595 head=722512e25 reviews=2/2 waiting=review rec=merge",
            "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains("rec=merge while waiting on review", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("checks")]
    [InlineData("merge")]
    [InlineData("check:ci-required")]
    public void Parse_EveryWaitPredicateContradictsAMergeRecommendation(string waiting)
    {
        AgentState? state = AgentState.Parse(
            $"pr=4595 head=722512e25 reviews=2/2 waiting={waiting} rec=merge",
            "pr4595");

        Assert.NotNull(state);
        Assert.Contains(state.Defects, d => d.Contains($"rec=merge while waiting on {waiting}", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_KeepsAnEscalationThatIdentifiesNothing()
    {
        // The blocking finding. The same observed value as the `i4613` case above, in a window whose name
        // cannot rescue the identity: `pr=none` is not a number, `worker` is not `pr####`, and the record
        // was dropped whole — so an agent asking to be released disappeared from the default report
        // entirely. There is nothing here to look up, and that is the one thing it must not be silent
        // about.
        StateReading reading = AgentState.Read("pr=none head=pending rec=stop", "worker");

        Assert.Null(reading.Identified);
        UnidentifiedState unusable = Assert.IsType<UnidentifiedState>(reading.Unidentified);

        // The disposition survives the identity that should have named its subject.
        Assert.Equal(Recommendation.Stop, unusable.Recommendation);

        // And every way the record fails its own grammar is still said, including the missing identity.
        Assert.Contains(unusable.Defects, d => d.Contains("pr=none", StringComparison.Ordinal));
        Assert.Contains(unusable.Defects, d => d.Contains("head=pending", StringComparison.Ordinal));
        Assert.Contains(unusable.Defects, d => d.Contains("neither the record nor the window name identifies", StringComparison.Ordinal));
    }

    [Fact]
    public void Read_AnUnidentifiedRecordIsNeverAnIdentifiedOne()
        // No invented number reaches the type every consumer treats as a thing on GitHub. `Parse` keeps
        // meaning exactly what it meant: a record with an identity, or nothing.
        => Assert.Null(AgentState.Parse("pr=none head=pending rec=stop", "worker"));

    [Theory]
    [InlineData("blocked")]                       // a bare token: not a field at all
    [InlineData("waiting-ci")]
    [InlineData("still converging")]
    [InlineData("pr=0 rec=continue")]             // a number outside the domain
    [InlineData("issue=none")]
    [InlineData("blockd=4629")]                   // a misspelling, which loses a real field
    public void Read_AnythingPublishedThatNamesNothingIsKept(string option)
    {
        StateReading reading = AgentState.Read(option, "worker");

        Assert.Null(reading.Identified);
        Assert.NotNull(reading.Unidentified);
        Assert.NotEmpty(reading.Unidentified.Defects);
        Assert.Contains(reading.Unidentified.Defects, d => d.Contains("identifies a PR or an issue", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t")]
    public void Read_AnEmptyOptionWithNoIdentityIsAbsentRatherThanDefective(string? option)
    {
        // An empty shell published nothing, so there is no record to be wrong about and no agent to say
        // anything about. Distinct from the case above, and the distinction is what keeps `--all` full of
        // idle shells rather than the default view.
        StateReading reading = AgentState.Read(option, "zsh");

        Assert.Null(reading.Identified);
        Assert.Null(reading.Unidentified);
        Assert.Empty(reading.Defects);
    }

    [Fact]
    public void Read_AMalformedIdentityStillFallsBackToTheWindowName()
    {
        // Unchanged by any of the above: where the name can rescue the identity, it still does, and the
        // record is still read whole.
        StateReading reading = AgentState.Read("pr=none head=pending round=0 reviews=0/2 rec=stop", "i4613");

        Assert.Null(reading.Unidentified);
        AgentState state = Assert.IsType<AgentState>(reading.Identified);
        Assert.Equal(4613, state.PrNumber);
        Assert.True(state.IsIssue);
        Assert.Equal(StateSource.WindowName, state.Source);
        Assert.Equal(Recommendation.Stop, state.Recommendation);
    }
}
