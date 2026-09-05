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
    public void Resolve_AClosedBlockerUnblocksTheDependent()
    {
        // #218's live scenario: the only fact that changed is the named blocker's own lifecycle, and that
        // alone is enough to turn a quiet Holding row into the operator's Unblocked queue.
        var blockers = new Dictionary<int, BlockerFetch>
        {
            [4629] = BlockerFetch.Found(new BlockerFacts(4629, "owner/repo", IsOpen: false, "Fix", DateTimeOffset.UtcNow)),
        };

        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"), Facts(), blockers);

        Assert.Equal(WaitingState.Unblocked, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AStillOpenBlockerStaysHoldingEvenWhenResolved()
    {
        var blockers = new Dictionary<int, BlockerFetch>
        {
            [4629] = BlockerFetch.Found(new BlockerFacts(4629, "owner/repo", IsOpen: true, "Fix", null)),
        };

        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"), Facts(), blockers);

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
    }

    [Fact]
    public void Resolve_AMixOfClearedAndOpenBlockersStaysHoldingAndNamesBoth()
    {
        var blockers = new Dictionary<int, BlockerFetch>
        {
            [4629] = BlockerFetch.Found(new BlockerFacts(4629, "owner/repo", IsOpen: false, null, null)),
            [4630] = BlockerFetch.Found(new BlockerFacts(4630, "owner/repo", IsOpen: true, null, null)),
        };

        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629,4630 rec=wait"), Facts(), blockers);

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Contains("#4630", v.Reason, StringComparison.Ordinal);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
        Assert.Contains("cleared", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnUnreadableBlockerNeverReadsAsCleared()
    {
        // An unavailable read is not evidence of anything: never let a blocker that could not be checked
        // silently release the dependent.
        var blockers = new Dictionary<int, BlockerFetch>
        {
            [4629] = BlockerFetch.Unavailable,
        };

        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"), Facts(), blockers);

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.NotEqual(WaitingState.Unblocked, v.State);
    }

    [Fact]
    public void Resolve_ABlockerNumberThatVanishedNeverReadsAsCleared()
    {
        var blockers = new Dictionary<int, BlockerFetch>
        {
            [4629] = BlockerFetch.NotFound,
        };

        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"), Facts(), blockers);

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.NotEqual(WaitingState.Unblocked, v.State);
    }

    [Fact]
    public void Resolve_ABlockerNotLookedUpThisSweepFallsBackToTheUnresolvedWording()
    {
        // An empty dictionary — nothing was resolved this sweep — must behave exactly like the null case:
        // the plain "parked behind #N" the tool has always shown.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 blocked=4629 rec=wait"),
            Facts(),
            new Dictionary<int, BlockerFetch>());

        Assert.Equal(WaitingState.Holding, v.State);
        Assert.Contains("#4629", v.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("cleared", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Severity_UnblockedRanksWithTheActionableQueue()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=1/2 waiting=check:ci-required rec=wait"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "success")]));

        Assert.Equal(WaitingState.Unblocked, v.State);
        Assert.Equal(4, v.Severity);
        Assert.True(v.Severity < new WaitingVerdict(WaitingState.Unknown, RowOwner.Operator, "unknown", Assurance.Low("unknown")).Severity);
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
    public void Confidence_AnInferredIdentityCaveatDoesNotClaimNoStateWasPublished()
    {
        // The window name only rescued the identity; the record published other fields. The caveat must
        // say the identity was missing, not that nothing at all was published.
        WaitingVerdict v = WaitingVerdict.Resolve(
            AgentState.Parse("head=722512e25 reviews=1/2 rec=continue", "pr4595")!, Facts());

        Assert.Equal(Confidence.Medium, v.Assurance.Level);
        Assert.Contains("no identity", v.Assurance.Caveat!, StringComparison.Ordinal);
        Assert.DoesNotContain("no state", v.Assurance.Caveat!, StringComparison.Ordinal);
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
    public void Resolve_UnreadablePrStillSurfacesAStopEscalation()
    {
        // GitHub is down or rate-limited, so the PR cannot be read — but the agent asking to be released
        // is the row a person most needs to see, and it does not depend on the branch being legible.
        // Dropping it to Unknown would erase the ask exactly when it matters most.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 rec=stop"), null);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.True(v.NeedsAttention);
        Assert.Contains("stop", v.Reason, StringComparison.Ordinal);
        Assert.Contains("could not be read", v.Reason, StringComparison.Ordinal);

        // Low assurance and a state MayAct does not admit: reported to a person, never acted on unattended.
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Resolve_UnreadablePrStillSurfacesAnApprovalEscalation()
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 rec=approve"), null);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains("authorise more rounds", v.Reason, StringComparison.Ordinal);
        Assert.Contains("could not be read", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_UnreadablePrRescuedByWindowNameStillSurfacesAStop()
    {
        // A malformed identity is a defect in one field; the window name rescues the PR number, and the
        // escalation beside it still reaches the operator rather than dropping to Unknown when GitHub is
        // silent.
        WaitingVerdict v = WaitingVerdict.Resolve(
            AgentState.Parse("pr=none head=pending round=0 reviews=0/2 rec=stop", "pr4595")!, null);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains("stop", v.Reason, StringComparison.Ordinal);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Resolve_UnreadablePrWithAnOrdinaryRecommendationStaysUnknown()
    {
        // Everything that is not an explicit escalation loses its evidence when GitHub goes silent, so the
        // ordinary unreadable behaviour is unchanged.
        Assert.Equal(WaitingState.Unknown, WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 rec=merge"), null).State);
        Assert.Equal(WaitingState.Unknown, WaitingVerdict.Resolve(State("pr=4595 head=722512e25 rec=continue"), null).State);
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

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("approve", "authorise more rounds")]
    public void Resolve_AnEscalationOutranksAMergedBranch(string rec, string reasonFragment)
    {
        // An explicit escalation is the agent asking a person to decide, and a merged PR does not retract
        // that ask. It outranks the branch state rather than being swallowed by the Merged return.
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head=722512e25 rec={rec}"), Facts(merged: true, state: "closed"));

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("approve", "authorise more rounds")]
    public void Resolve_AnEscalationOutranksAClosedBranch(string rec, string reasonFragment)
    {
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head=722512e25 rec={rec}"), Facts(state: "closed"));

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("approve", "authorise more rounds")]
    public void Resolve_AnEscalationOutranksAStaleRecord(string rec, string reasonFragment)
    {
        // The record describes a head GitHub has moved past, but the ask to stop or approve is about the
        // window's work, not the code, so it still reaches the operator. The evidence behind it is stale,
        // though, so assurance is graded down and the head mismatch is named (see the assurance tests
        // below); it stays non-actionable throughout.
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head=aaaaaaa11 reviews=1/2 rec={rec}"), Facts());

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
        Assert.False(v.MayAct);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("approve", "authorise more rounds")]
    public void Resolve_AStaleEscalationIsGradedLowAndNamesTheHeadMismatch(string rec, string reasonFragment)
    {
        // The exact regression: a clean record whose only flaw is a stale head would otherwise reach the
        // operator at high confidence with no caveat, because assurance was assessed before staleness was
        // checked. The escalation must survive, but its assurance is the stale evidence's, not a clean
        // record's, and the reason exposes the recorded/GitHub head mismatch.
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head=aaaaaaa11 rec={rec}"), Facts());

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
        Assert.Contains("aaaaaaa11", v.Reason, StringComparison.Ordinal);
        Assert.Contains("722512e25", v.Reason, StringComparison.Ordinal);
        Assert.Contains("aaaaaaa11", v.Assurance.Caveat!, StringComparison.Ordinal);
        Assert.False(v.MayAct);
    }

    [Theory]
    [InlineData("stop", "stop")]
    [InlineData("approve", "authorise more rounds")]
    public void Resolve_AMatchingEscalationKeepsHighAssuranceAndNoMismatch(string rec, string reasonFragment)
    {
        // The counterpart: when the recorded head matches GitHub, the escalation is on a clean record and
        // keeps the high confidence it earns, with no head-mismatch caveat manufactured.
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head=722512e25 rec={rec}"), Facts());

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Equal(Confidence.High, v.Assurance.Level);
        Assert.Contains(reasonFragment, v.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("GitHub head is", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_StaleHeadsSharingNineCharactersRenderDistinctly()
    {
        // The regression the fix targets: two 40-character shas that agree on the first nine characters but
        // diverge at the tenth would both clip to "722512e25" and print an identical displayed head. The
        // diagnostic must widen just enough to expose the divergence.
        const string recorded = "722512e25a0c1d4a9b8e7360a1c2d3e4f5061728";
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head={recorded} reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Stale, v.State);
        Assert.Contains("722512e25a", v.Reason, StringComparison.Ordinal);
        Assert.Contains("722512e25f", v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("approve")]
    public void Resolve_StaleEscalationSharingNineCharactersDistinguishesHeadsInOneSharedDiagnostic(string rec)
    {
        // The same collision reached through the stop/approve escalation path: the widened mismatch must
        // distinguish the heads, and the reason and the assurance caveat must be the exact same diagnostic.
        const string recorded = "722512e25a0c1d4a9b8e7360a1c2d3e4f5061728";
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head={recorded} rec={rec}"), Facts());

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.Contains("722512e25a", v.Reason, StringComparison.Ordinal);
        Assert.Contains("722512e25f", v.Reason, StringComparison.Ordinal);
        Assert.Contains("722512e25a", v.Assurance.Caveat!, StringComparison.Ordinal);
        Assert.Contains("722512e25f", v.Assurance.Caveat!, StringComparison.Ordinal);
        Assert.Contains(v.Assurance.Caveat!, v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_StaleShortDivergentHeadsRenderInFullWithoutThrowing()
    {
        // The accepted blocker: two divergent heads both shorter than the concise nine-character default
        // (here eight). The displayed width can never reach nine, so clamping the lower bound to nine asked
        // Math.Clamp for a minimum wider than the maximum and threw. The whole of each short value is shown,
        // and the two stay distinct.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=12345678 reviews=2/2 rec=merge"), Facts(head: "87654321"));

        Assert.Equal(WaitingState.Stale, v.State);
        Assert.Contains("record describes 12345678, GitHub head is 87654321", v.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("stop", 8)]
    [InlineData("approve", 8)]
    [InlineData("stop", 7)]
    [InlineData("approve", 7)]
    public void Resolve_StaleEscalationWithShortDivergentHeadsDistinguishesThemInOneSharedDiagnostic(string rec, int width)
    {
        // The same short-head collision reached through the stop/approve escalation path, at both edge
        // lengths seven and eight: no exception, the heads stay distinct, and the reason and the assurance
        // caveat remain byte-for-byte the same diagnostic.
        string recorded = "1234567890"[..width];
        string github = "0987654321"[..width];
        WaitingVerdict v = WaitingVerdict.Resolve(State($"pr=4595 head={recorded} rec={rec}"), Facts(head: github));

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.Contains(recorded, v.Reason, StringComparison.Ordinal);
        Assert.Contains(github, v.Reason, StringComparison.Ordinal);
        Assert.NotEqual(recorded, github);
        Assert.Contains(v.Assurance.Caveat!, v.Reason, StringComparison.Ordinal);
        Assert.Contains(recorded, v.Assurance.Caveat!, StringComparison.Ordinal);
        Assert.Contains(github, v.Assurance.Caveat!, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_OrdinaryDifferentHeadsStayConciseAtNineCharacters()
    {
        // Heads that diverge in the first character need no widening; the concise nine-character form is
        // kept and the tenth character of the GitHub head is never shown.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=aaaaaaa11 reviews=2/2 rec=merge"), Facts());

        Assert.Equal(WaitingState.Stale, v.State);
        Assert.Contains("record describes aaaaaaa11, GitHub head is 722512e25", v.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("722512e25f", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnAbbreviatedRecordedHeadThatPrefixesGitHubIsNotStale()
    {
        // The recorded head is a seven-character prefix of GitHub's, so it names the same revision: not
        // stale, and no mismatch diagnostic is produced.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e round=2 reviews=2/2 rec=merge"), Facts());

        Assert.NotEqual(WaitingState.Stale, v.State);
    }

    [Fact]
    public void Resolve_AContinueBesideAClearedPredicateIsNotActionable()
    {
        // `continue` says the window is still working, not parked behind the predicate, so a cleared
        // `waiting=` cannot wake a window that never stopped. Evaluating the predicate first would have
        // returned an actionable Unblocked; continue denies readiness, so this stays a quiet Holding.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 waiting=check:ci rec=continue"),
            Facts(checks: [new CheckRunFact("ci", "completed", "success")]));

        Assert.NotEqual(WaitingState.Unblocked, v.State);
        Assert.Equal(RowOwner.Nobody, v.Owner);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Resolve_AnIssueWindowAskingToMergeIsUntrustworthy()
    {
        // An issue window has no PR, so `merge` names something that cannot exist. It surfaces to the
        // operator as untrustworthy rather than resolving to the quiet Holding an issue window otherwise
        // gets, which would have filtered the impossible request out of view.
        WaitingVerdict v = WaitingVerdict.Resolve(AgentState.Parse("issue=4611 head=8d5f22a22 rec=merge", "i4611")!, null);

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.False(v.MayAct);
        Assert.Contains("no PR", v.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_AnIssueWindowStillEscalatesStopAndApprove()
    {
        Assert.Equal(WaitingState.NeedsOperator,
            WaitingVerdict.Resolve(AgentState.Parse("issue=4611 head=8d5f22a22 rec=stop", "i4611")!, null).State);
        Assert.Equal(WaitingState.NeedsOperator,
            WaitingVerdict.Resolve(AgentState.Parse("issue=4611 head=8d5f22a22 rec=approve", "i4611")!, null).State);
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

    [Fact]
    public void Resolve_AMergeRecommendationBesideAWaitIsVisibleRatherThanQuietlyHolding()
    {
        // The mirror of the blocked case, and the one that hid: `rec=merge waiting=review` resolved
        // through the predicate to Holding, owned by nobody, with no defect to keep it in the default
        // view — so a record asking to merge while saying a review round is outstanding was the one
        // contradiction the reader never saw.
        WaitingVerdict v = WaitingVerdict.Resolve(State("pr=4595 head=722512e25 reviews=2/2 waiting=review rec=merge"), Facts());

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Resolve_AMergeRecommendationBesideAClearedWaitIsStillUntrustworthy()
    {
        // Clearing the predicate is not what makes the pair coherent: the record still published two
        // statements that cannot both be true, which is a fact about the agent rather than about CI.
        WaitingVerdict v = WaitingVerdict.Resolve(
            State("pr=4595 head=722512e25 reviews=2/2 waiting=check:ci-required rec=merge"),
            Facts(checks: [new CheckRunFact("ci-required", "completed", "success")]));

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Unidentified_AnEscalationWithNoSubjectStillReachesTheOperator()
    {
        // The window named `worker` publishing `pr=none head=pending rec=stop`: an agent asking to be
        // released, about nothing this reader can look up. It is in the default view because the request
        // is real, and it names no PR because the record did not.
        UnidentifiedState unusable = AgentState.Read("pr=none head=pending rec=stop", "worker").Unidentified!;
        WaitingVerdict v = WaitingVerdict.Unidentified(unusable);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.True(v.NeedsAttention);
        Assert.Contains("stop", v.Reason, StringComparison.Ordinal);
        Assert.Contains("names no PR or issue", v.Reason, StringComparison.Ordinal);

        // Visible, and never speakable-to: nothing identifies what a tool would be speaking about.
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.False(v.MayAct);
    }

    [Fact]
    public void Unidentified_AnApprovalRequestWithNoSubjectIsTheOperatorsToo()
    {
        WaitingVerdict v = WaitingVerdict.Unidentified(
            AgentState.Read("pr=none rec=approve", "worker").Unidentified!);

        Assert.Equal(WaitingState.NeedsOperator, v.State);
        Assert.Contains("authorise more rounds", v.Reason, StringComparison.Ordinal);
        Assert.False(v.MayAct);
    }

    [Theory]
    [InlineData("pr=none head=pending rec=continue")]
    [InlineData("blocked")]
    [InlineData("blockd=4629")]
    public void Unidentified_AnythingElseIsUntrustworthyRatherThanQuiet(string option)
    {
        // No escalation to carry, but a record that named nothing is still an agent that tried to report
        // and got it wrong. Owned by the operator so it is not filtered out of the default view.
        WaitingVerdict v = WaitingVerdict.Unidentified(AgentState.Read(option, "worker").Unidentified!);

        Assert.Equal(WaitingState.Untrustworthy, v.State);
        Assert.Equal(RowOwner.Operator, v.Owner);
        Assert.Equal(Confidence.Low, v.Assurance.Level);
        Assert.False(v.MayAct);
    }
}
