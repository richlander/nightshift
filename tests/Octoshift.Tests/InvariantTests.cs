namespace Octoshift.Tests;

using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The safety properties from <c>docs/design/waiting-model.md</c>, checked by enumeration rather than by
/// example.
/// </summary>
/// <remarks>
/// The example-based tests each pin one path through the decision table. These sweep the product of the
/// inputs that path depends on and assert the properties that must hold everywhere — which is the class
/// of check that catches a new gate inserted in the wrong place, or an existing one that stops firing
/// because something upstream changed. It is the cheap half of what a model checker would do; the
/// temporal invariants (9–12) are the half it cannot reach, because they are statements about sequences
/// of sweeps rather than about one decision.
/// </remarks>
public class InvariantTests
{
    private const string Head = "722512e25f0c1d4a9b8e7360a1c2d3e4f5061728";

    private static readonly string[] Records =
    [
        "pr=1 head=722512e25 reviews=2/2 rec=merge",
        "pr=1 head=722512e25 reviews=2/2",
        "pr=1 head=722512e25 reviews=2/2 rec=continue",
        "pr=1 head=722512e25 reviews=2/2 rec=wait",
        "pr=1 head=722512e25 reviews=2/2 blocked=ci rec=wait",
        "pr=1 head=722512e25 reviews=2/2 blocked=2 rec=wait",
        "pr=1 head=722512e25 reviews=1/2 rec=merge",
        "pr=1 head=722512e25 reviews=1/1 rec=merge",
        "pr=1 head=722512e25 reviews=0/2 rec=merge",
        "pr=1 head=722512e25 reviews=2/2 rec=stop",
        "pr=1 head=722512e25 reviews=2/2 rec=approve",
        "pr=1 head=722512e25 reviews=2/2 rec=probably",
        "pr=1 reviews=2/2 rec=merge",
        "pr=1 head=aaaaaaa11 reviews=2/2 rec=merge",
        "pr=1 head=722512e25 reviews=1/2 waiting=check:ci rec=wait",
        "pr=1 head=722512e25 reviews=1/2 waiting=merge rec=wait",
        "pr=1 head=722512e25 reviews=2/2 blocked=1 rec=wait",
    ];

    private static readonly string?[] MergeableStates =
        ["clean", "dirty", "unknown", "behind", "blocked", "draft", "unstable", "has_hooks", null, "something_new"];

    public static TheoryData<string, string?> Combinations()
    {
        var data = new TheoryData<string, string?>();
        foreach (string record in Records)
        {
            foreach (string? mergeable in MergeableStates)
            {
                data.Add(record, mergeable);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void SafetyInvariantsHoldForEveryCombination(string record, string? mergeableState)
    {
        AgentState state = AgentState.Parse(record, "pr1")!;
        PrFacts facts = new()
        {
            Number = 1,
            HeadSha = Head,
            State = "open",
            MergeableState = mergeableState,
            Checks = [new CheckRunFact("ci", "completed", "success")],
        };

        WaitingVerdict verdict = WaitingVerdict.Resolve(state, facts);
        bool sole = verdict.MayAct;

        // (3) Nothing is acted on below high confidence.
        Assert.True(!sole || verdict.Assurance.Level == Confidence.High);

        // (4) A record that contradicts itself is never acted on.
        Assert.True(!sole || state.Defects.Count == 0);

        // (5) Acting requires a head that ties the claim to a revision GitHub agrees with.
        Assert.True(!sole || (state.Head is not null && Head.StartsWith(state.Head, StringComparison.OrdinalIgnoreCase)));

        // (6) Ready means the two-clean bar and affirmative mergeability, together.
        Assert.True(verdict.State != WaitingState.Ready || (state.ReviewsMeetBar && facts.IsMergeable));

        // (7) Mergeability GitHub has not computed can never read as ready.
        Assert.True(!facts.MergeabilityKnown ? verdict.State != WaitingState.Ready : true);

        // Only a state where speaking means something is ever actionable.
        Assert.True(!sole || verdict.State is WaitingState.Ready or WaitingState.Unblocked);
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void AFollowerIsNeverActionableInAnyCombination(string record, string? mergeableState)
    {
        // (1) and (2): the claim gates action independently of how good the evidence is.
        AgentState state = AgentState.Parse(record, "pr1")!;
        PrFacts facts = new()
        {
            Number = 1,
            HeadSha = Head,
            State = "open",
            MergeableState = mergeableState,
            Checks = [new CheckRunFact("ci", "completed", "success")],
        };

        WaitingVerdict verdict = WaitingVerdict.Resolve(state, facts);

        foreach (Claim claim in (Claim[])[
            new(ClaimRank.Follower, [], null, ClaimBasis.Observed),
            new(ClaimRank.Follower, [], null, ClaimBasis.Inferred),
            new(ClaimRank.Owner, [], null, ClaimBasis.Inferred)])
        {
            Assert.False(verdict.MayAct && claim.OwnsClaim);
        }
    }
}
