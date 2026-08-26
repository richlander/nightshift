namespace Octoshift.Tests;

using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The decision table joining a window's declared state with GitHub's. Its two governing rules: ready is
/// dual-clean and mergeable rather than green CI, and an agent that has declared itself done is never
/// sent back round — that row belongs to the operator.
/// </summary>
public class WaitingVerdictTests
{
    private const string Head = "722512e25f0c1d4a9b8e7360a1c2d3e4f5061728";

    private static AgentState State(string record, string window = "pr4595")
        => AgentState.Parse(record, window)!;

    private static PrFacts Facts(
        string mergeableState = "clean",
        bool merged = false,
        string state = "open",
        string head = Head,
        CheckRunFact[]? checks = null)
        => new()
        {
            Number = 4595,
            HeadSha = head,
            State = state,
            Merged = merged,
            MergeableState = mergeableState,
            Checks = checks ?? [],
        };

    [Fact]
    public void Resolve_ADeclaredWaitThatHasClearedIsTheWholePoint()
    {
        // The case the tool exists for: the agent is idle, the check it named passed hours ago, and
        // nothing has told it. Until a nudge exists, that row is the operator's.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "success")]));

        Assert.Equal(WaitingState.Unblocked, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains("passed", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AClearedPredicateDoesNotOverrideANamedBlocker()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "success")]));

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.False(v.MayAct);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("continue")]
    public void Resolve_ANamedBlockerPreventsPredicateWakeupWithoutWaitRecommendation(string? recommendation)
    {
        string rec = recommendation is null ? string.Empty : $" rec={recommendation}";
        WaitingVerdict v = WaitingVerdict.Resolve(
            State($"pr=4595 head=722512e25 reviews=1/2 blocked=4629 waiting=merge{rec}"),
            Facts());

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.False(v.MayAct);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ANamedBlockerPreventsPrematureConflictWork()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"),
            Facts(mergeableState: "dirty"));

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AFailedCheckAlsoClearsTheWait()
    {
        // "Concluded" is the condition, not "passed": a red result is news the agent needs just as much.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "failure")]));

        Assert.Equal(WaitingState.Unblocked, v.State);
        Assert.Contains("failure", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnUnreportedCheckIsStillQuiet()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("build", "completed", "success")]));

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
    }

    [Fact]
    public void Resolve_WaitingOnMergeAnswersTheUncomputedCase()
    {
        AgentState state = State("pr=4595 head=722512e25 reviews=1/2 waiting=merge rec=wait");

        Assert.Equal(WaitingState.Holding, WaitingVerdict.Resolve(state, Facts(mergeableState: "unknown")).State);
        Assert.Equal(WaitingState.Unblocked, WaitingVerdict.Resolve(state, Facts(mergeableState: "clean")).State);
    }

    [Fact]
    public void Resolve_AnIssueWindowIsNeverJoinedAgainstAPr()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(AgentState.Parse("issue=4611 head=8d5f22a22 rec=continue", "i4611")!, null);

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Contains("no PR yet", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnIssueWindowAskingToStopReachesTheOperator()
    {
        // The end of the dropped-fields bug: `pr=none … rec=stop` in an i#### window used to resolve as a
        // window holding quietly, because the `rec` never survived the read. An agent asking to be
        // released is the row a person most needs to see.
        WaitingVerdict v = WaitingVerdict.Resolve(
            AgentState.Parse("pr=none head=pending round=0 reviews=0/2 rec=stop", "i4613")!, null);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.True(v.NeedsAttention);
        Assert.Contains("stop", v.Reason, StringComparison.Ordinal);

        // Reported, never acted on: the identity is inferred and the record contradicts itself.
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Confidence_ACleanCorroboratedRecordIsTheOnlyThingActedOn()
    {
        // Two independently written fields saying the same thing, on a head GitHub agrees with. In the
        // observed fleet every record of this shape was correct.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 round=2 reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.Equal(Confidence.High, v.Assurance.Level);
        Assert.True(v.MayAct);
    }

    [Fact]
    public void Confidence_ReviewsAloneIsNeverEnoughToActOn()
    {
        // `reviews=2/2` with no recommendation corroborating it: two windows published a 2/2 count while
        // their own round reports read "converging". The verdict still shows, but nothing may be sent.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 round=2 reviews=2/2"), Facts());

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.NotEqual(Confidence.High, v.Assurance.Level);
        Assert.False(v.MayAct);
        Assert.Contains("uncorroborated", v.Assurance.Caveat!, StringComparison.Ordinal);
    }

    [Fact]
    public void Confidence_ADefectiveRecordIsLowEverywhereNotOnlyInTheFieldThatIsWrong()
    {
        // A record that contradicts itself says the agent was not tracking the contract, which bears on
        // everything else it wrote.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 blocked=ci rec=merge"), Facts());

        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Confidence_AnInferredIdentityCapsAtMedium()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(AgentState.Parse(null, "pr4595")!, Facts());

        Assert.Equal(Confidence.Medium, v.Assurance.Level);
        Assert.False(v.MayAct);
        Assert.Contains("window name", v.Assurance.Caveat!, StringComparison.Ordinal);
    }

    [Fact]
    public void Confidence_AClearedWaitOnACleanRecordMayBeActedOn()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "success")]));

        Assert.Equal(WaitingState.Unblocked, v.State);
        Assert.True(v.MayAct);
    }

    [Fact]
    public void Confidence_HoldingIsNeverActedOnEvenAtHighConfidence()
    {
        // Confidence is about the evidence; acting also needs a state where speaking means something.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=0/2 blocked=4629 rec=wait"), Facts());

        Assert.Equal(Confidence.High, v.Assurance.Level);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Resolve_UnreadablePrGoesToTheOperator()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=1/2"), null);
        Assert.Equal(WaitingState.Unknown, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_MergedAndClosedAreReported()
    {
        Assert.Equal(WaitingState.Merged, WaitingVerdict.Resolve(State("pr=4595 head=722512e25"), Facts(merged: true, state: "closed")).State);
        Assert.Equal(WaitingState.Closed, WaitingVerdict.Resolve(State("pr=4595 head=722512e25"), Facts(state: "closed")).State);
    }

    [Fact]
    public void Resolve_HeadDivergenceVoidsTheRecord()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=aaaaaaa11 reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Stale, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_StopAndApproveNeedAPerson()
    {
        Assert.Equal(WaitingState.NeedsOperator, WaitingVerdict.Resolve(State("pr=4595 head=722512e25 rec=stop"), Facts()).State);
        Assert.Equal(WaitingState.NeedsOperator, WaitingVerdict.Resolve(State("pr=4595 head=722512e25 rec=approve"), Facts()).State);
    }

    [Fact]
    public void Resolve_DeclaredDoneAndConflictingGoesToTheOperatorNotBackToTheAgent()
    {
        // The rule this tool exists for: repeated conflict passes on a PR the agent thinks is finished
        // are the waste being removed. Sequencing against a moving main is an operator call.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"),
            Facts(mergeableState: "dirty"));

        Assert.Equal(WaitingState.Contradicted, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_MidWorkAndConflictingIsTheAgentsToFix()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=0/2"),
            Facts(mergeableState: "dirty"));

        Assert.Equal(WaitingState.Conflicting, v.State);
        Assert.Equal(RowOwner.Agent, v.Owner);
        Assert.DoesNotContain("rebase", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("")]
    public void Resolve_UncomputedMergeabilityIsNeverTreatedAsMergeable(string mergeableState)
    {
        // GitHub computes this lazily and answers `unknown` on the first read after a change. Letting it
        // fall through as "not conflicting" is how a conflicted PR reads as ready.
        WaitingVerdict done = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"), Facts(mergeableState: mergeableState));

        Assert.Equal(WaitingState.MergeUnverified, done.State);
        Assert.Equal(RowOwner.Operator, done.Owner);

        WaitingVerdict working = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=0/2"), Facts(mergeableState: mergeableState));

        Assert.Equal(RowOwner.Nobody, working.Owner);
    }

    [Fact]
    public void Resolve_WaitWithCitableBlockersStaysQuiet()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 blocked=4629,4630 rec=wait"), Facts());

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
        Assert.False(v.NeedsAttention);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_IncompleteReviewsAreNobodysProblemYet()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=1/2"), Facts());

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
        Assert.Contains("1/2", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_DualCleanAndMergeableIsTheMergeQueue()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_RedCiDoesNotBlockReady()
    {
        // Ready is dual-clean and mergeable. CI is reported because it is worth seeing, but it is not a
        // gate: it goes red for reasons unrelated to the change, and clearing it is the operator's call.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "failure")]));

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.Contains("CI red (ci-required)", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PendingCiIsNotedButStillReady()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"),
            Facts(checks: [new CheckRunFact("ci-required", "in_progress", null)]));

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.Contains("CI still running", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReviewEvidenceIsWhatCountsNotTheRecommendation()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2"), Facts());

        Assert.Equal(WaitingState.Ready, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Theory]
    [InlineData("continue")]
    [InlineData("wait")]
    public void Resolve_AnAgentSayingItIsNotFinishedIsNotFinished(string rec)
    {
        // Observed live: `reviews=2/2 rec=continue` on a window whose round report read "converging" and
        // whose next step was another round. The count meant "two reviewers reported", not "two clean";
        // the recommendation is the agent stating plainly that it is still working, and it wins.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State($"pr=4595 head=722512e25 round=3 reviews=2/2 blocked=4629 rec={rec}"), Facts());

        Assert.NotEqual(WaitingState.Ready, v.State);
    }

    [Fact]
    public void Resolve_RecMergeAloneDoesNotMakeAPrReady()
    {
        // `rec=merge` is the agent's request, not its evidence. The evidence is the review count.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=0/2 rec=merge"), Facts());

        Assert.NotEqual(WaitingState.Ready, v.State);
        Assert.NotEqual(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_ASelfLoweredBarIsNotTheBar()
    {
        // reviews=1/1 satisfies "clean equals required" and still has not met the two-clean repository
        // bar. A record does not get to choose what it is measured against.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=1/1"), Facts());

        Assert.Equal(WaitingState.Holding, v.State);
    }

    [Fact]
    public void Resolve_ADiscardedBlockerCannotFallThroughIntoTheMergeQueue()
    {
        // Observed live: `blocked=ci reviews=2/2 rec=wait`. "ci" is not citable so the blocker list is
        // empty and the wait branch is skipped. Two independent gates now stop it reaching the queue —
        // `wait` denying readiness, and the defect gate below — and it needs only one of them to hold.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 blocked=ci rec=wait"), Facts());

        Assert.NotEqual(WaitingState.Ready, v.State);
    }

    [Fact]
    public void Resolve_ASelfContradictingRecordCannotClaimToBeDone()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 blocked=ci rec=merge"), Facts());

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
    }

    [Fact]
    public void Resolve_DoneWithoutAHeadCannotBeChecked()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.Contains("without a head", v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("behind")]
    [InlineData("blocked")]
    [InlineData("draft")]
    [InlineData("some_state_github_added_later")]
    public void Resolve_OnlyAffirmativeMergeabilityCounts(string mergeableState)
    {
        // Anything that does not positively say the branch can merge fails closed and is named.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"), Facts(mergeableState: mergeableState));

        Assert.Equal(WaitingState.NotMergeable, v.State);
        Assert.Contains(mergeableState, v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnstableIsStillMergeableBecauseCiIsNotTheBar()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 rec=merge"), Facts(mergeableState: "unstable"));

        Assert.Equal(WaitingState.Ready, v.State);
    }

    [Fact]
    public void Resolve_WindowNameOnlyRecordIsNotTreatedAsDone()
    {
        // No declared state at all: nothing has been handed over, so nothing is claimed on its behalf.
        WaitingVerdict v = WaitingVerdict.Resolve(AgentState.Parse(null, "pr4595")!, Facts());

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
    }
}
