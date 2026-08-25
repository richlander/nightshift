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

    /// <summary>
    /// The condition the agent said it was waiting on has cleared, and it does not know yet. This is the
    /// case the whole tool exists for: an idle window whose blocker went away hours ago.
    /// </summary>
    Unblocked,

    /// <summary>The agent declared itself done and GitHub disagrees.</summary>
    Contradicted,

    /// <summary>The branch cannot merge without integrating a later main.</summary>
    Conflicting,

    /// <summary>GitHub does not affirmatively say the branch can merge, for some reason other than a conflict.</summary>
    NotMergeable,

    /// <summary>The declared state contradicts itself, so nothing in it can be relied on.</summary>
    Untrustworthy,

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

        // An issue-tracking window has no PR to check, so there is nothing on GitHub to join it against.
        if (state.IsIssue)
        {
            return state.Recommendation switch
            {
                Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to stop; grant or decline"),
                Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to authorise more rounds"),
                _ => new(WaitingState.Holding, RowOwner.Nobody, $"tracking issue #{state.PrNumber}; no PR yet"),
            };
        }

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

        // Everything below decides whether a window is finished, so every one of these gates fails closed.
        // A claim that cannot be checked is not a claim that passes.
        //
        // `rec=merge` alone is deliberately NOT enough. It is the agent's request, not its evidence, and
        // the evidence is the review count measured against the repository bar.
        bool declaredDone = state.ReviewsMeetBar;

        // A record that contradicts itself cannot be trusted to say it is finished — and a discarded
        // blocker is exactly how `blocked=ci rec=wait` would otherwise fall through into the merge queue.
        if (declaredDone && state.Defects.Count > 0)
        {
            return new(WaitingState.Untrustworthy, RowOwner.Operator, "reported done, but the state contradicts itself");
        }

        // Falsifiability is the point of `head`. Without one there is nothing tying the claim to a
        // revision, so it can be neither confirmed nor refuted.
        if (declaredDone && state.Head is null)
        {
            return new(WaitingState.Untrustworthy, RowOwner.Operator, "reported done without a head to check it against");
        }

        if (facts.IsConflicting)
        {
            // The agent believes it is finished and the branch does not merge. Do NOT send it back round:
            // sequencing against a moving main is an operator call, and repeated conflict passes are the
            // waste this tool exists to remove.
            return declaredDone
                ? new(WaitingState.Contradicted, RowOwner.Operator, "reported done, but the branch is CONFLICTING")
                : new(WaitingState.Conflicting, RowOwner.Agent, "CONFLICTING; integrate a later main");
        }

        // The declared predicate is evaluated before the generic gates, because it is the agent's own
        // statement of what would make it interesting again — including `merge`, which is precisely the
        // uncomputed-mergeability case.
        if (state.Waiting.Kind != WaitKind.None)
        {
            WaitingVerdict? predicate = EvaluateWait(state, facts);
            if (predicate is { } resolved)
            {
                return resolved;
            }
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

        // Known, and not conflicting, but still not a state that says the branch can merge: `behind`,
        // `blocked`, `draft`, or something GitHub added since. Naming it beats guessing at it.
        if (!facts.IsMergeable)
        {
            return new(WaitingState.NotMergeable, RowOwner.Operator,
                $"reported done, but GitHub says {facts.MergeableState}");
        }

        // Reviews meet the bar and the branch merges. CI is reported but is deliberately not a gate: it
        // goes red for reasons unrelated to the change, and clearing it is the operator's call.
        CheckRunFact? failed = facts.Checks.FirstOrDefault(c => c.IsFailure);
        string ci = failed is not null
            ? $"; CI red ({failed.Name})"
            : facts.Checks.Any(c => !c.IsComplete) ? "; CI still running" : string.Empty;

        return new(WaitingState.Ready, RowOwner.Operator, $"reviews {state.ReviewsClean}/{state.ReviewsRequired}, mergeable{ci}");
    }

    /// <summary>
    /// Resolves a <c>waiting=</c> predicate, or null to fall through to the generic gates. Clearing is
    /// reported rather than acted on: nothing here wakes the agent, so the row goes to the operator.
    /// </summary>
    private static WaitingVerdict? EvaluateWait(AgentState state, PrFacts facts)
    {
        switch (state.Waiting.Kind)
        {
            case WaitKind.Review:
                // Nothing on GitHub can answer this one; only the agent knows.
                return new(WaitingState.Holding, RowOwner.Nobody, "review round outstanding");

            case WaitKind.Merge:
                return !facts.MergeabilityKnown
                    ? new(WaitingState.Holding, RowOwner.Nobody, "waiting on mergeability; not yet computed")
                    : facts.IsMergeable
                        ? new(WaitingState.Unblocked, RowOwner.Operator, "waited on mergeability, and the branch now merges")
                        : new(WaitingState.NotMergeable, RowOwner.Operator, $"waited on mergeability; GitHub says {facts.MergeableState}");

            case WaitKind.Checks:
                if (!facts.ChecksKnown)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, "waiting on checks; the check list could not be read");
                }

                return facts.Checks.Any(c => !c.IsComplete)
                    ? new(WaitingState.Holding, RowOwner.Nobody, $"waiting on {facts.Checks.Count(c => !c.IsComplete)} check(s)")
                    : new(WaitingState.Unblocked, RowOwner.Operator, "waited on checks, and they have all concluded");

            case WaitKind.Check:
                string name = state.Waiting.CheckName!;
                if (!facts.ChecksKnown)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"waiting on {name}; the check list could not be read");
                }

                CheckRunFact? check = facts.FindCheck(name);
                if (check is null)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"{name} has not reported on {Short(facts.HeadSha)}");
                }

                if (!check.IsComplete)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"{name} is {check.Status}");
                }

                return check.IsFailure
                    ? new(WaitingState.Unblocked, RowOwner.Operator, $"waited on {name}, which concluded {check.Conclusion}")
                    : new(WaitingState.Unblocked, RowOwner.Operator, $"waited on {name}, which passed");

            default:
                return null;
        }
    }

    private static bool ShaMatches(string recorded, string actual)
    {
        int length = Math.Min(recorded.Length, actual.Length);
        return length >= 7 && recorded.AsSpan(0, length).Equals(actual.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];
}
