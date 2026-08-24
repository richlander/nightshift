namespace Octoshift.Waiting;

using Octoshift.GitHub;

/// <summary>What a window's self-reported state resolves to once GitHub's side is known.</summary>
internal enum WaitingState
{
    /// <summary>GitHub could not be read; nothing is asserted.</summary>
    Unknown,

    /// <summary>The record describes a sha GitHub is no longer at. Its claims are void.</summary>
    Stale,

    /// <summary>Reviews are in and the branch merges. This is the merge queue.</summary>
    Ready,

    /// <summary>The agent declared itself done and GitHub disagrees.</summary>
    Contradicted,

    /// <summary>The branch cannot merge without integrating a later main.</summary>
    Conflicting,

    /// <summary>GitHub has not finished computing mergeability, so nothing can be claimed yet.</summary>
    MergeUnverified,

    /// <summary>A person has to decide before anything moves.</summary>
    NeedsOperator,

    /// <summary>Legitimately parked or still working. Nothing to do.</summary>
    Holding,

    /// <summary>The PR merged.</summary>
    Merged,

    /// <summary>The PR closed without merging.</summary>
    Closed,
}

/// <summary>Whose attention a row belongs to.</summary>
internal enum RowOwner
{
    /// <summary>Nobody: the window is progressing or legitimately parked.</summary>
    Nobody,

    /// <summary>The operator. A person has to look.</summary>
    Operator,

    /// <summary>The agent, which is still working and has not handed anything over.</summary>
    Agent,
}

/// <summary>
/// The join of what a window declared with what GitHub reports.
/// </summary>
/// <param name="State">The resolved state.</param>
/// <param name="Owner">Whose attention this row belongs to.</param>
/// <param name="Reason">One line naming the specific fact behind the state.</param>
internal readonly record struct WaitingVerdict(WaitingState State, RowOwner Owner, string Reason)
{
    public bool NeedsAttention => Owner == RowOwner.Operator;

    /// <summary>
    /// Resolves a window's state against GitHub's account of the same PR. Pure — the whole decision table
    /// is testable without a pane or a network.
    /// </summary>
    public static WaitingVerdict Resolve(AgentState state, PrFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(state);

        if (facts is null)
        {
            return new(WaitingState.Unknown, RowOwner.Operator, $"could not read PR #{state.PrNumber} from GitHub");
        }

        if (facts.Merged)
        {
            return new(WaitingState.Merged, RowOwner.Operator, $"PR #{facts.Number} merged; the window is done");
        }

        if (string.Equals(facts.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return new(WaitingState.Closed, RowOwner.Operator, $"PR #{facts.Number} closed without merging");
        }

        // Head divergence voids the record before anything in it is evaluated: every claim was made about
        // code GitHub is no longer serving.
        if (state.Head is not null && !ShaMatches(state.Head, facts.HeadSha))
        {
            return new(WaitingState.Stale, RowOwner.Operator,
                $"record describes {Short(state.Head)}, GitHub head is {Short(facts.HeadSha)}");
        }

        // Stop and Approve both need an answer before anything moves, and an escalation outranks whatever
        // the branch looks like — a person is already required.
        if (state.Recommendation == Recommendation.Stop)
        {
            return new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to stop; grant or decline");
        }

        if (state.Recommendation == Recommendation.Approve)
        {
            return new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to authorise more rounds");
        }

        bool declaredDone = state.Recommendation == Recommendation.Merge || state.ReviewsComplete;

        if (facts.IsConflicting)
        {
            // The agent believes it is finished and the branch does not merge. Do NOT send it back round:
            // sequencing against a moving main is an operator call, and repeated conflict passes are the
            // waste this tool exists to remove.
            return declaredDone
                ? new(WaitingState.Contradicted, RowOwner.Operator, "reported done, but the branch is CONFLICTING")
                : new(WaitingState.Conflicting, RowOwner.Agent, "CONFLICTING; integrate a later main");
        }

        if (!facts.MergeabilityKnown)
        {
            return declaredDone
                ? new(WaitingState.MergeUnverified, RowOwner.Operator, "reported done, but GitHub has not computed mergeability")
                : new(WaitingState.MergeUnverified, RowOwner.Nobody, "mergeability not yet computed");
        }

        // Wait is the one recommendation that needs no decision — the agent resumes itself when the
        // numbers it named close. It stays quiet, and becomes interesting again exactly then.
        if (state.Recommendation == Recommendation.Wait && state.Blocked.Count > 0)
        {
            return new(WaitingState.Holding, RowOwner.Nobody,
                $"parked behind {string.Join(", ", state.Blocked.Select(b => "#" + b))}");
        }

        if (!declaredDone)
        {
            return state.ReviewsRequired is > 0
                ? new(WaitingState.Holding, RowOwner.Nobody, $"reviews {state.ReviewsClean ?? 0}/{state.ReviewsRequired}")
                : new(WaitingState.Holding, RowOwner.Nobody, "in progress");
        }

        // Reviews are in and the branch merges. CI is reported but is deliberately not a gate: it goes
        // red for reasons unrelated to the change, and clearing it is the operator's call.
        CheckRunFact? failed = facts.Checks.FirstOrDefault(c => c.IsFailure);
        string ci = failed is not null
            ? $"; CI red ({failed.Name})"
            : facts.Checks.Any(c => !c.IsComplete) ? "; CI still running" : string.Empty;

        return new(WaitingState.Ready, RowOwner.Operator, $"reviews {state.ReviewsClean}/{state.ReviewsRequired}, mergeable{ci}");
    }

    private static bool ShaMatches(string recorded, string actual)
    {
        int length = Math.Min(recorded.Length, actual.Length);
        return length >= 7 && recorded.AsSpan(0, length).Equals(actual.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];
}
