namespace Octoshift.Tests;

using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The decision table that turns "what the agent said" plus "what GitHub says" into a state and, where it
/// is safe, a directive. Pure input to pure output — no pane, no network.
/// </summary>
public class WaitingVerdictTests
{
    private const string Head = "722512e25f0c1d4a9b8e7360a1c2d3e4f5061728";

    private static StatusRecord Declared(string waiting, string? next = "round-3", string? head = Head, int pr = 4595)
        => new()
        {
            PrNumber = pr,
            Head = head,
            Round = 2,
            Verdict = "gated",
            Waiting = WaitingPredicate.Parse(waiting),
            Next = next,
            Source = RecordSource.Declared,
        };

    private static StatusRecord Inferred(string? head = Head, int pr = 4595)
        => new() { PrNumber = pr, Head = head, Source = RecordSource.Inferred };

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

    private static CheckRunFact Check(string name, string status = "completed", string? conclusion = "success")
        => new(name, status, conclusion);

    [Fact]
    public void Resolve_UnreadablePrIsUnknown()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("check:ci-required"), null);

        Assert.Equal(WaitingState.Unknown, verdict.State);
        Assert.False(verdict.Releasable);
    }

    [Fact]
    public void Resolve_MergedPrEndsTheWait()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("check:ci-required"), Facts(merged: true, state: "closed"));

        Assert.Equal(WaitingState.Merged, verdict.State);
        Assert.True(verdict.NeedsAttention);
    }

    [Fact]
    public void Resolve_ClosedUnmergedPrIsReported()
        => Assert.Equal(WaitingState.Closed, WaitingVerdict.Resolve(Declared("check:ci-required"), Facts(state: "closed")).State);

    [Fact]
    public void Resolve_HeadDivergenceVoidsTheRecord()
    {
        // The agent pushed after writing the record, so the checks fetched here belong to a different sha
        // than the one it asked about. Answering the question anyway would be answering a different one.
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required", head: "aaaaaaa11"),
            Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Stale, verdict.State);
        Assert.False(verdict.Releasable);
        Assert.Contains("aaaaaaa11", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("722512e25", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ShortRecordedShaMatchesFullGitHubSha()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required", head: "722512e25"),
            Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Ready, verdict.State);
    }

    [Fact]
    public void Resolve_OperatorWaitIsNeverReleased()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("operator", next: "escalate"), Facts());

        Assert.Equal(WaitingState.NeedsOperator, verdict.State);
        Assert.False(verdict.Releasable);
        Assert.Null(verdict.Directive);
    }

    [Fact]
    public void Resolve_ConflictOutranksAPassingCheck()
    {
        // A green check on an unmergeable branch still cannot land, so resuming the declared next would
        // burn a round that ends in the same place.
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required"),
            Facts(mergeableState: "dirty", checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Blocked, verdict.State);
        Assert.Equal("rebase onto main and push", verdict.Directive);
        Assert.False(verdict.Releasable);
    }

    [Fact]
    public void Resolve_OperatorOutranksAConflict()
    {
        // Already escalated: a human is coming either way, and a nudge would race them.
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("operator"), Facts(mergeableState: "dirty"));

        Assert.Equal(WaitingState.NeedsOperator, verdict.State);
    }

    [Fact]
    public void Resolve_NamedCheckAbsentIsStillHolding()
    {
        // The 2026-08-22 case: a rerun was requested and the required check has not appeared on the head.
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required"),
            Facts(checks: [Check("build"), Check("test")]));

        Assert.Equal(WaitingState.Holding, verdict.State);
        Assert.False(verdict.NeedsAttention);
        Assert.Contains("ci-required has not reported", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NamedCheckInProgressIsHolding()
        => Assert.Equal(
            WaitingState.Holding,
            WaitingVerdict.Resolve(
                Declared("check:ci-required"),
                Facts(checks: [Check("ci-required", status: "in_progress", conclusion: null)])).State);

    [Fact]
    public void Resolve_NamedCheckFailureBlocksWithoutReleasing()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required"),
            Facts(checks: [Check("ci-required", conclusion: "failure")]));

        Assert.Equal(WaitingState.Blocked, verdict.State);
        Assert.Equal("fix ci-required and push", verdict.Directive);
        Assert.False(verdict.Releasable);
    }

    [Fact]
    public void Resolve_NamedCheckPassReleasesTheAgentsOwnNext()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required", next: "round-2-review"),
            Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Ready, verdict.State);
        Assert.Equal("round-2-review", verdict.Directive);
        Assert.True(verdict.Releasable);
    }

    [Theory]
    [InlineData("neutral")]
    [InlineData("skipped")]
    public void Resolve_NonFailingConclusionsCount(string conclusion)
        => Assert.Equal(
            WaitingState.Ready,
            WaitingVerdict.Resolve(Declared("check:ci-required"), Facts(checks: [Check("ci-required", conclusion: conclusion)])).State);

    [Fact]
    public void Resolve_ReadyWithoutADeclaredNextIsNotReleasable()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("check:ci-required", next: null),
            Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Ready, verdict.State);
        Assert.False(verdict.Releasable);
        Assert.Null(verdict.Directive);
    }

    [Fact]
    public void Resolve_InferredRecordIsNeverReleasable()
    {
        // Nothing in an inferred record is the agent's word, so there is no decision to repeat.
        WaitingVerdict verdict = WaitingVerdict.Resolve(Inferred(), Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Ready, verdict.State);
        Assert.False(verdict.Releasable);
    }

    [Fact]
    public void Resolve_WaitingNoneFallsBackToOverallHealth()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("none"),
            Facts(checks: [Check("build"), Check("test")]));

        Assert.Equal(WaitingState.Ready, verdict.State);
        Assert.Equal("round-3", verdict.Directive);
        Assert.True(verdict.Releasable);
    }

    [Fact]
    public void Resolve_OverallFailureNamesTheFailingJob()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("none"),
            Facts(checks: [Check("build"), Check("test", conclusion: "timed_out")]));

        Assert.Equal(WaitingState.Blocked, verdict.State);
        Assert.Equal("fix test and push", verdict.Directive);
    }

    [Fact]
    public void Resolve_OverallPendingListsWhatIsOutstanding()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(
            Declared("none"),
            Facts(checks: [Check("build"), Check("windows", status: "queued", conclusion: null)]));

        Assert.Equal(WaitingState.Holding, verdict.State);
        Assert.Contains("windows", verdict.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_NoChecksAtAllIsHoldingNotGreen()
    {
        // An empty rollup means CI has not started, which reads identically to "all green" if you only
        // count failures. Holding is the honest answer.
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("none"), Facts());

        Assert.Equal(WaitingState.Holding, verdict.State);
    }

    [Fact]
    public void Resolve_ReviewWaitNeverPollsGitHubAgain()
    {
        WaitingVerdict verdict = WaitingVerdict.Resolve(Declared("review"), Facts(checks: [Check("ci-required")]));

        Assert.Equal(WaitingState.Holding, verdict.State);
        Assert.False(verdict.NeedsAttention);
    }
}
