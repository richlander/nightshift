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
/// <param name="Assurance">How far the evidence behind it can be relied on, and why not further.</param>
internal readonly record struct WaitingVerdict(WaitingState State, RowOwner Owner, string Reason, Assurance Assurance)
{
    public bool NeedsAttention => Owner == RowOwner.Operator;

    /// <summary>Whether a tool may speak to this window unattended.</summary>
    public bool MayAct => Assurance.MayAct && State is WaitingState.Unblocked or WaitingState.Ready;

    /// <summary>
    /// Sort key for the operator's queue, most urgent first. Confirmed problems rank above "cannot tell
    /// yet": GitHub computes mergeability lazily and can leave a PR `unknown` across many reads, so
    /// unverified rows are common and must not bury a branch that is definitely conflicting.
    /// </summary>
    public int Severity => State switch
    {
        WaitingState.NeedsOperator => 0,   // someone is blocked on a person right now
        WaitingState.Contradicted => 1,    // said done, demonstrably is not
        WaitingState.Stale => 2,
        WaitingState.Closed => 3,
        WaitingState.Ready or WaitingState.Unblocked => 4, // actionable merge/resume queue
        WaitingState.Merged => 5,          // window is finished, can be reclaimed
        WaitingState.Conflicting => 6,
        WaitingState.MergeUnverified => 7, // GitHub has not answered yet
        WaitingState.Unknown => 8,
        _ => 9,
    };

    /// <summary>
    /// Resolves a record that named nothing this reader can look up. There is no GitHub side to join it
    /// against — no PR to fetch, no head to falsify it with — so this is the whole decision.
    /// </summary>
    /// <remarks>
    /// The row is reported and never actionable, and both halves of that are deliberate. Reported,
    /// because an agent published something and the one thing certain about it is that it is wrong;
    /// dropping it is how <c>rec=stop</c> in a window named <c>worker</c> became silence. Never
    /// actionable, twice over: assurance is low, and neither state a record without an identity can reach
    /// is one <see cref="MayAct"/> admits. An escalation still reaches the operator as an escalation —
    /// what it has lost is the subject, so the row says so rather than implying one.
    /// </remarks>
    public static WaitingVerdict Unidentified(UnidentifiedState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        // The same words the empty-shell row uses, because it is the same missing fact — and said once
        // here rather than restated from the reason, which already carries what the record asked for and
        // is followed in the detail column by every defect verbatim.
        Assurance assurance = Assurance.Low("nothing identifies this window");

        return state.Recommendation switch
        {
            Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator,
                "asking to stop, but the record names no PR or issue", assurance),
            Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator,
                "asking to authorise more rounds, but the record names no PR or issue", assurance),
            _ => new(WaitingState.Untrustworthy, RowOwner.Operator,
                "published state that names no PR or issue", assurance),
        };
    }

    /// <summary>
    /// Resolves a window's state against GitHub's account of the same PR. Pure — the whole decision table
    /// is testable without a pane or a network.
    /// </summary>
    public static WaitingVerdict Resolve(AgentState state, PrFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(state);

        Assurance assurance = Assess(state, facts);

        // An issue-tracking window has no PR to check, so there is nothing on GitHub to join it against.
        if (state.IsIssue)
        {
            return state.Recommendation switch
            {
                Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to stop; grant or decline", assurance),
                Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to authorise more rounds", assurance),
                _ => new(WaitingState.Holding, RowOwner.Nobody, $"tracking issue #{state.PrNumber}; no PR yet", assurance),
            };
        }

        if (facts is null)
        {
            return new(WaitingState.Unknown, RowOwner.Operator, $"could not read PR #{state.PrNumber} from GitHub", assurance);
        }

        if (facts.Merged)
        {
            return new(WaitingState.Merged, RowOwner.Operator, $"PR #{facts.Number} merged; the window is done", assurance);
        }

        if (string.Equals(facts.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return new(WaitingState.Closed, RowOwner.Operator, $"PR #{facts.Number} closed without merging", assurance);
        }

        // Head divergence voids the record before anything in it is evaluated: every claim was made about
        // code GitHub is no longer serving.
        if (state.Head is not null && !ShaMatches(state.Head, facts.HeadSha))
        {
            return new(WaitingState.Stale, RowOwner.Operator,
                $"record describes {Short(state.Head)}, GitHub head is {Short(facts.HeadSha)}", assurance);
        }

        // Stop and Approve both need an answer before anything moves, and an escalation outranks whatever
        // the branch looks like — a person is already required.
        if (state.Recommendation == Recommendation.Stop)
        {
            return new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to stop; grant or decline", assurance);
        }

        if (state.Recommendation == Recommendation.Approve)
        {
            return new(WaitingState.NeedsOperator, RowOwner.Operator, "asking to authorise more rounds", assurance);
        }

        // Everything below decides whether a window is finished, so every one of these gates fails closed.
        // A claim that cannot be checked is not a claim that passes.
        //
        // `rec=merge` alone is deliberately NOT enough: it is the agent's request, not its evidence, and
        // the evidence is the review count measured against the repository bar.
        //
        // `continue` and `wait` are the other half of that rule, and the half this originally missed. They
        // are the agent stating it is NOT finished, which outranks any count it also published — observed
        // live as `reviews=2/2 rec=continue` on a window whose own round report read "converging" and
        // "round 4 next". A recommendation cannot manufacture readiness; it can always deny it.
        bool declaredDone = state.ReviewsMeetBar
            && state.Recommendation is not (Recommendation.Continue or Recommendation.Wait);

        // A record that contradicts itself cannot be trusted to say it is finished — and a discarded
        // blocker is exactly how `blocked=ci rec=wait` would otherwise fall through into the merge queue.
        if (declaredDone && state.Defects.Count > 0)
        {
            return new(WaitingState.Untrustworthy, RowOwner.Operator, "reported done, but the state contradicts itself", assurance);
        }

        // Falsifiability is the point of `head`. Without one there is nothing tying the claim to a
        // revision, so it can be neither confirmed nor refuted.
        if (declaredDone && state.Head is null)
        {
            return new(WaitingState.Untrustworthy, RowOwner.Operator, "reported done without a head to check it against", assurance);
        }

        // A named blocker is an explicit unresolved dependency. A predicate beside it can add another
        // reason to wait, but clearing that predicate cannot clear the issue or PR the record still names.
        // It also outranks a conflict against today's main: waking the agent before its dependency lands
        // buys a conflict pass that the dependency is likely to invalidate. Stop/approve escalations were
        // handled above, while a contradictory merge recommendation was already made untrustworthy.
        if (state.Blocked.Count > 0)
        {
            return new(WaitingState.Holding, RowOwner.Nobody,
                $"parked behind {string.Join(", ", state.Blocked.Select(b => "#" + b))}", assurance);
        }

        if (facts.IsConflicting)
        {
            // The agent believes it is finished and the branch does not merge. Do NOT send it back round:
            // sequencing against a moving main is an operator call, and repeated conflict passes are the
            // waste this tool exists to remove.
            return declaredDone
                ? new(WaitingState.Contradicted, RowOwner.Operator, "reported done, but the branch is CONFLICTING", assurance)
                : new(WaitingState.Conflicting, RowOwner.Agent, "CONFLICTING; integrate a later main", assurance);
        }

        // The declared predicate is evaluated before the generic gates, because it is the agent's own
        // statement of what would make it interesting again — including `merge`, which is precisely the
        // uncomputed-mergeability case.
        if (state.Waiting.Kind != WaitKind.None)
        {
            WaitingVerdict? predicate = EvaluateWait(state, facts, assurance);
            if (predicate is { } resolved)
            {
                return resolved;
            }
        }

        if (!facts.MergeabilityKnown)
        {
            return declaredDone
                ? new(WaitingState.MergeUnverified, RowOwner.Operator, "reported done, but GitHub has not computed mergeability", assurance)
                : new(WaitingState.MergeUnverified, RowOwner.Nobody, "mergeability not yet computed", assurance);
        }

        if (!declaredDone)
        {
            // Say which fact is holding it, so a count that meets the bar next to a recommendation that
            // denies it does not read as "so why is this not ready?".
            if (state.ReviewsMeetBar)
            {
                return new(WaitingState.Holding, RowOwner.Nobody,
                    $"rec={state.Recommendation.ToString().ToLowerInvariant()}; reviews {state.ReviewsClean}/{state.ReviewsRequired} is not a claim of done", assurance);
            }

            return state.ReviewsRequired is > 0
                ? new(WaitingState.Holding, RowOwner.Nobody, $"reviews {state.ReviewsClean ?? 0}/{state.ReviewsRequired}", assurance)
                : new(WaitingState.Holding, RowOwner.Nobody, "in progress", assurance);
        }

        // Known, and not conflicting, but still not a state that says the branch can merge: `behind`,
        // `blocked`, `draft`, or something GitHub added since. Naming it beats guessing at it.
        if (!facts.IsMergeable)
        {
            return new(WaitingState.NotMergeable, RowOwner.Operator,
                $"reported done, but GitHub says {facts.MergeableState}", assurance);
        }

        // Reviews meet the bar and the branch merges. CI is reported but is deliberately not a gate: it
        // goes red for reasons unrelated to the change, and clearing it is the operator's call.
        CheckRunFact? failed = facts.Checks.FirstOrDefault(c => c.IsFailure);
        string ci = !facts.ChecksKnown
            ? "; CI unreadable"
            : failed is not null
                ? $"; CI red ({failed.Name})"
                : facts.Checks.Any(c => !c.IsComplete) ? "; CI still running" : string.Empty;

        // `reviews` is self-reported and measurably unreliable: two windows on one fleet published 2/2
        // while their own round reports read "converging". So a Ready that rests on the count alone is
        // never high — it needs a second, independently written field saying the same thing. Where both
        // agree and the record is otherwise clean, every observed case was correct.
        Assurance readiness = assurance.Level == Confidence.High && state.Recommendation != Recommendation.Merge
            ? Assurance.Medium("reviews=2/2 is uncorroborated; rec does not also say merge")
            : assurance;

        return new(WaitingState.Ready, RowOwner.Operator, $"reviews {state.ReviewsClean}/{state.ReviewsRequired}, mergeable{ci}", readiness);
    }

    /// <summary>
    /// Resolves a <c>waiting=</c> predicate, or null to fall through to the generic gates. Clearing is
    /// reported rather than acted on: nothing here wakes the agent, so the row goes to the operator.
    /// </summary>
    private static WaitingVerdict? EvaluateWait(AgentState state, PrFacts facts, Assurance assurance)
    {
        switch (state.Waiting.Kind)
        {
            case WaitKind.Review:
                // Nothing on GitHub can answer this one; only the agent knows.
                return new(WaitingState.Holding, RowOwner.Nobody, "review round outstanding", assurance);

            case WaitKind.Merge:
                return !facts.MergeabilityKnown
                    ? new(WaitingState.Holding, RowOwner.Nobody, "waiting on mergeability; not yet computed", assurance)
                    : facts.IsMergeable
                        ? new(WaitingState.Unblocked, RowOwner.Operator, "waited on mergeability, and the branch now merges", assurance)
                        : new(WaitingState.NotMergeable, RowOwner.Operator, $"waited on mergeability; GitHub says {facts.MergeableState}", assurance);

            case WaitKind.Checks:
                if (!facts.ChecksKnown)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, "waiting on checks; the check list could not be read", assurance);
                }

                return facts.Checks.Any(c => !c.IsComplete)
                    ? new(WaitingState.Holding, RowOwner.Nobody, $"waiting on {facts.Checks.Count(c => !c.IsComplete)} check(s)", assurance)
                    : new(WaitingState.Unblocked, RowOwner.Operator, "waited on checks, and they have all concluded", assurance);

            case WaitKind.Check:
                string name = state.Waiting.CheckName!;
                if (!facts.ChecksKnown)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"waiting on {name}; the check list could not be read", assurance);
                }

                CheckRunFact? check = facts.FindCheck(name);
                if (check is null)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"{name} has not reported on {Short(facts.HeadSha)}", assurance);
                }

                if (!check.IsComplete)
                {
                    return new(WaitingState.Holding, RowOwner.Nobody, $"{name} is {check.Status}", assurance);
                }

                return check.IsFailure
                    ? new(WaitingState.Unblocked, RowOwner.Operator, $"waited on {name}, which concluded {check.Conclusion}", assurance)
                    : new(WaitingState.Unblocked, RowOwner.Operator, $"waited on {name}, which passed", assurance);

            default:
                return null;
        }
    }

    /// <summary>
    /// Grades the evidence a verdict will rest on. Deliberately pessimistic: every downgrade here was
    /// observed on a live fleet rather than imagined, and the cost of overrating a record is that a tool
    /// eventually speaks to an agent on the strength of something the agent did not mean.
    /// </summary>
    private static Assurance Assess(AgentState state, PrFacts? facts)
    {
        if (facts is null && !state.IsIssue)
        {
            return Assurance.Low("GitHub could not be read");
        }

        // A record that contradicts itself tells you the agent was not tracking the contract, which is a
        // statement about everything else it wrote, not only the field that is wrong.
        if (state.Defects.Count > 0)
        {
            return Assurance.Low($"the record contradicts itself ({state.Defects.Count} defect(s))");
        }

        if (state.Source == StateSource.WindowName)
        {
            return Assurance.Medium("identity read from the window name; the agent published no state");
        }

        // Without a head nothing ties the claims to a revision, so they can be neither confirmed nor
        // refuted — the same reason a headless record cannot claim to be done.
        if (state.Head is null)
        {
            return Assurance.Medium("no head to check the claims against");
        }

        return Assurance.High;
    }

    private static bool ShaMatches(string recorded, string actual)
    {
        int length = Math.Min(recorded.Length, actual.Length);
        return length >= 7 && recorded.AsSpan(0, length).Equals(actual.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];
}
