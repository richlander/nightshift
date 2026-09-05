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
    /// The single pane-activity policy: whether a pane's published record may be trusted as a
    /// <em>handover</em>. Only an idle pane has stopped and left its status as its final word; a pane
    /// mid-turn, holding a prompt, stalled, or unreadable is doing (or hiding) something its last published
    /// record no longer describes. Every place that reads a published <c>rec=</c>/<c>reviews=</c> as fact —
    /// the verdict gate (<see cref="ForActivity"/>), retirement (<see cref="Retirement.For"/>), and
    /// follower promotion (<see cref="Claim.IsReleasing"/>) — asks this one predicate, so a stale record
    /// under a non-idle pane can never drive one decision path while another blocks it. It is exactly the
    /// set of activities <see cref="ForActivity"/> defers to <c>resolveIdle</c> for.
    /// </summary>
    public static bool IsHandover(PaneActivity activity) => activity == PaneActivity.Idle;

    /// <summary>
    /// Wraps the idle-path resolution with the pane-activity gate, so a published record is only ever read
    /// as a handover when the pane is actually idle. A window mid-turn, one holding a prompt open, one whose
    /// runtime stalled, and one that could not be captured each resolve from what the pane is <em>doing</em>
    /// right now, never from what it last <em>published</em> — because a stale <c>reviews=2/2 rec=merge</c>
    /// under a spinner would otherwise reach a high-confidence, actionable verdict on evidence the pane
    /// itself contradicts. Only the idle/handover case (<see cref="IsHandover"/>) defers to
    /// <paramref name="resolveIdle"/>, which joins the published state with GitHub. This is the single copy
    /// of that policy: both <c>waiting</c> and <c>pr</c> call it, so the two surfaces cannot drift into
    /// answering the same pane differently.
    /// </summary>
    /// <param name="activity">What the pane is doing now, from its capture — the gate.</param>
    /// <param name="capture">The pane body, for the one non-idle case (a stall) that names its reason from it.</param>
    /// <param name="resolveIdle">The idle-path verdict, evaluated only when the pane has handed something over.</param>
    public static WaitingVerdict ForActivity(PaneActivity activity, string capture, Func<WaitingVerdict> resolveIdle)
        => activity switch
        {
            // A pane mid-turn has not handed anything over; there is nothing to resolve and nothing to do,
            // but it still holds a claim, so it gets a row that is never actionable rather than vanishing.
            PaneActivity.Working => new WaitingVerdict(
                WaitingState.Unknown, RowOwner.Agent, "agent is mid-turn; nothing handed over yet",
                Assurance.Low("the agent has not handed anything over")),

            // The agent runtime failed. No GitHub lookup can explain it and none can clear it, so this goes
            // straight to a person with the text that says why.
            PaneActivity.Stalled => new WaitingVerdict(
                WaitingState.NeedsOperator, RowOwner.Operator,
                $"agent stalled: {TmuxScanner.StallReason(capture)}", Assurance.High),

            // A held-open prompt is answered with a keystroke, not a GitHub lookup. The pane itself is the
            // evidence, and it is unambiguous: a prompt is open.
            PaneActivity.Blocked => new WaitingVerdict(
                WaitingState.NeedsOperator, RowOwner.Operator, "prompt open; awaiting a keystroke", Assurance.High),

            // A pane nobody could read is reported, never resolved. Whether the agent is mid-turn is exactly
            // what the capture was for, so this must not reach the idle path, where a published record is
            // taken as a handover and can reach a high-confidence, actionable verdict on unread evidence.
            PaneActivity.Unreadable => new WaitingVerdict(
                WaitingState.Unknown, RowOwner.Operator, "pane could not be captured; its state is unread",
                Assurance.Low("the pane could not be read")),

            _ => resolveIdle(),
        };

    /// <summary>
    /// Resolves a window's state against a multi-repo PR resolution, so the null-facts case can tell an
    /// affirmative "no such PR in the repos I searched" from an unreadable GitHub — different facts with
    /// different remedies (widen <c>--repo</c> versus wait out an outage) — and can flag a number that
    /// collides across repos rather than silently picking one. A found PR joins exactly as the
    /// <see cref="Resolve(AgentState, PrFacts?)"/> overload does, against the facts stamped with their repo.
    /// </summary>
    public static WaitingVerdict Resolve(AgentState state, PrFetch fetch, IReadOnlyDictionary<int, BlockerFetch>? blockers = null)
    {
        ArgumentNullException.ThrowIfNull(state);

        // A found PR — or an issue window, which has no PR to look up across repos — joins through the
        // single-facts table unchanged. Only the genuinely absent outcomes need the multi-repo wording.
        if (fetch.Facts is { } facts)
        {
            return Resolve(state, facts, blockers);
        }

        if (state.IsIssue)
        {
            return Resolve(state, null, blockers);
        }

        string scope = fetch.Searched.Count > 0 ? string.Join(", ", fetch.Searched) : "the searched repo(s)";

        if (fetch.Status == PrFetchStatus.Ambiguous)
        {
            string repos = fetch.FoundIn.Count > 0 ? string.Join(", ", fetch.FoundIn) : scope;
            return new(WaitingState.NeedsOperator, RowOwner.Operator,
                $"PR #{state.PrNumber} is ambiguous — found in {repos}; pass a single --repo to disambiguate",
                Assurance.Low($"#{state.PrNumber} exists in more than one searched repo"));
        }

        if (fetch.Status == PrFetchStatus.Unavailable)
        {
            // A partial hit: the PR exists in at least one repo, but the rest of the configured scope could
            // not be read or was cut short by the shared budget, so uniqueness is unproven — the found repo
            // might not be the only one. Existence is known, so this is not the bare "could not read"; it is
            // non-actionable all the same, since acting would mean choosing a repo that may not be unique.
            if (fetch.FoundIn.Count > 0)
            {
                string where = string.Join(", ", fetch.FoundIn);
                string tail = fetch.Unsearched.Count > 0
                    ? $"{string.Join(", ", fetch.Unsearched)} not searched (budget spent)"
                    : "part of the scope could not be read";
                Assurance partial = Assurance.Low($"found in {where}; uniqueness unproven — {tail}");
                return state.Recommendation switch
                {
                    Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator,
                        $"asking to stop; PR #{state.PrNumber} found in {where} but uniqueness is unproven — {tail}", partial),
                    Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator,
                        $"asking to authorise more rounds; PR #{state.PrNumber} found in {where} but uniqueness is unproven — {tail}", partial),
                    _ => new(WaitingState.Unknown, RowOwner.Operator,
                        $"PR #{state.PrNumber} found in {where}, but uniqueness unproven — {tail}", partial),
                };
            }

            // A pure outage: no repo confirmed the PR, so existence itself is unknown.
            return Resolve(state, null, blockers);
        }

        // Affirmative not-found: every searched repo answered 404. Distinct from an outage, and its remedy
        // is to widen the scope, not to wait. An explicit stop/approve escalation still reaches the operator.
        Assurance absent = Assurance.Low($"searched {scope}; pass --repo if the PR is in another repo");
        return state.Recommendation switch
        {
            Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator,
                $"asking to stop, but PR #{state.PrNumber} is not in {scope}", absent),
            Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator,
                $"asking to authorise more rounds, but PR #{state.PrNumber} is not in {scope}", absent),
            _ => new(WaitingState.Unknown, RowOwner.Operator, $"no such PR #{state.PrNumber} in {scope}", absent),
        };
    }

    /// <summary>
    /// Resolves a window's state against GitHub's account of the same PR. Pure — the whole decision table
    /// is testable without a pane or a network.
    /// </summary>
    public static WaitingVerdict Resolve(AgentState state, PrFacts? facts, IReadOnlyDictionary<int, BlockerFetch>? blockers = null)
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

                // `merge` on an issue window is impossible — there is no PR to merge — and the parser
                // records it as a defect. Surfaced as untrustworthy and operator-owned rather than falling
                // to the quiet Holding below, so the impossible request is not filtered out of view.
                Recommendation.Merge => new(WaitingState.Untrustworthy, RowOwner.Operator,
                    $"asking to merge issue #{state.PrNumber}, which has no PR to merge", assurance),

                _ => new(WaitingState.Holding, RowOwner.Nobody, $"tracking issue #{state.PrNumber}; no PR yet", assurance),
            };
        }

        if (facts is null)
        {
            // GitHub is unreadable, so there is no branch to join the record against. An explicit
            // escalation survives that anyway: `stop` and `approve` are the agent asking a person to
            // decide, and that request does not depend on the PR being legible — dropping it to Unknown is
            // the same erasure as the field never surviving the read, and it happens exactly when GitHub is
            // down or rate-limited and an operator most needs to see the ask. So keep it as the operator's,
            // at the low assurance the unreadable side already earns, and say both halves: what was asked,
            // and that the PR could not be read. Every other recommendation has no evidence left to stand
            // on once GitHub is silent, so it stays Unknown.
            return state.Recommendation switch
            {
                Recommendation.Stop => new(WaitingState.NeedsOperator, RowOwner.Operator,
                    $"asking to stop, but PR #{state.PrNumber} could not be read from GitHub", assurance),
                Recommendation.Approve => new(WaitingState.NeedsOperator, RowOwner.Operator,
                    $"asking to authorise more rounds, but PR #{state.PrNumber} could not be read from GitHub", assurance),
                _ => new(WaitingState.Unknown, RowOwner.Operator, $"could not read PR #{state.PrNumber} from GitHub", assurance),
            };
        }

        // Stop and Approve both need an answer before anything moves, and an explicit escalation outranks
        // whatever the branch looks like — a merged, closed, or stale PR does not retract the agent's ask,
        // and a person is already required. Placed before the branch-state gates so the escalation is not
        // swallowed by a Merged/Closed/Stale return. Assurance is still whatever the facts and defects
        // earn, so a stale or contradictory record's escalation reaches the operator graded low.
        if (state.Recommendation == Recommendation.Stop)
        {
            return Escalation(state, facts, assurance, "asking to stop; grant or decline");
        }

        if (state.Recommendation == Recommendation.Approve)
        {
            return Escalation(state, facts, assurance, "asking to authorise more rounds");
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
        if (IsStale(state, facts))
        {
            return new(WaitingState.Stale, RowOwner.Operator,
                DescribeMismatch(state.Head!, facts.HeadSha), assurance);
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
            if (blockers is not null)
            {
                WaitingVerdict? cleared = EvaluateBlockers(state, blockers, assurance);
                if (cleared is { } resolved)
                {
                    return resolved;
                }
            }

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

        // The declared predicate is the agent's own statement of what would make it interesting again —
        // including `merge`, the uncomputed-mergeability case — so it is evaluated before the generic
        // gates, but only when the agent is actually parked on it. `continue` says the window is still
        // working, not idle behind a predicate, so a cleared `waiting=` cannot wake a window that never
        // stopped: evaluating it turned "still working" into an actionable Unblocked, observed as
        // `waiting=check:ci rec=continue` on a branch whose ci had gone green. Continue denies readiness
        // exactly as it denies done-ness, so it falls through to the in-progress gate below instead.
        if (state.Recommendation != Recommendation.Continue && state.Waiting.Kind != WaitKind.None)
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
    /// Resolves every number the window named in <c>blocked=</c> against the fleet's blocker reads
    /// (#218), or null to fall through to the unresolved "parked behind #N" wording when a name was not
    /// looked up this sweep. A blocker whose read is missing or unavailable keeps the whole set unresolved
    /// — never treated as cleared — since a blocker that cannot be read is not evidence it closed.
    /// </summary>
    private static WaitingVerdict? EvaluateBlockers(AgentState state, IReadOnlyDictionary<int, BlockerFetch> blockers, Assurance assurance)
    {
        var open = new List<int>();
        var cleared = new List<int>();

        foreach (int number in state.Blocked)
        {
            if (!blockers.TryGetValue(number, out BlockerFetch fetch))
            {
                // Not looked up this sweep (an unreadable dependent repo, or simply not asked) — cannot
                // assert anything about it, so the whole named set stays the unresolved wording.
                return null;
            }

            switch (fetch.Status)
            {
                case BlockerFetchStatus.Found when fetch.Facts is { IsOpen: false }:
                    cleared.Add(number);
                    break;
                case BlockerFetchStatus.Found:
                    open.Add(number);
                    break;
                case BlockerFetchStatus.NotFound:
                    // A blocker that no longer resolves anywhere in its repo cannot be distinguished from
                    // one that was renumbered or deleted — it is not evidence of a merge or a close, so it
                    // stays counted as unresolved rather than silently treated as cleared.
                    return null;
                case BlockerFetchStatus.Unavailable:
                    return null;
            }
        }

        if (open.Count == 0)
        {
            // Every named blocker closed or merged: this is the transition #218 exists to surface. Nothing
            // here wakes the agent — the row is reported to the operator so a person decides whether the
            // resumed work is still wanted, exactly as every other Unblocked case does.
            string names = string.Join(", ", cleared.Select(b => "#" + b));
            string subject = cleared.Count == 1 ? "it" : "them";
            return new(WaitingState.Unblocked, RowOwner.Operator,
                $"blocker {names} cleared; the wait behind {subject} is over", assurance);
        }

        if (cleared.Count == 0)
        {
            // Nothing has changed since the record was written; keep the plain unresolved wording rather
            // than a redundant "(still open)" on every blocker.
            return null;
        }

        // A mixed set: report which of the named blockers are still open so a partial clearance is not
        // read as a full one, while staying Holding — the agent is not released until every named blocker
        // is gone.
        string stillOpen = string.Join(", ", open.Select(b => "#" + b));
        string alreadyCleared = string.Join(", ", cleared.Select(b => "#" + b));
        return new(WaitingState.Holding, RowOwner.Nobody,
            $"parked behind {stillOpen} ({alreadyCleared} cleared)", assurance);
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
            return Assurance.Medium("agent published no identity; identity read from the window name");
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

    /// <summary>
    /// Whether the record describes a head GitHub has moved past. A record naming no head names no
    /// revision, so it can never be stale — there is nothing to diverge from.
    /// </summary>
    private static bool IsStale(AgentState state, PrFacts facts) =>
        state.Head is not null && !ShaMatches(state.Head, facts.HeadSha);

    /// <summary>
    /// Builds a stop or approve escalation once GitHub is readable. The ask to stop or authorise is about
    /// the window's work, not the code, so it reaches the operator whatever the branch looks like. But a
    /// record describing a head GitHub has moved past is asking about code that is gone: the escalation
    /// still stands and stays non-actionable, yet its assurance is graded down to low and the head
    /// mismatch is named, so a stale escalation cannot reach the operator wearing the high confidence a
    /// clean record would have earned.
    /// </summary>
    private static WaitingVerdict Escalation(AgentState state, PrFacts facts, Assurance assurance, string ask)
    {
        if (IsStale(state, facts))
        {
            string mismatch = DescribeMismatch(state.Head!, facts.HeadSha);
            return new(WaitingState.NeedsOperator, RowOwner.Operator, $"{ask} — but the {mismatch}", Assurance.Low(mismatch));
        }

        return new(WaitingState.NeedsOperator, RowOwner.Operator, ask, assurance);
    }

    private static string Short(string sha) => sha.Length <= ShortWidth ? sha : sha[..ShortWidth];

    /// <summary>The concise default width a lone sha is displayed at.</summary>
    private const int ShortWidth = 9;

    /// <summary>
    /// Renders a stale head mismatch so the recorded sha and the GitHub head are always visibly distinct.
    /// Ordinary different shas diverge within the first few characters and print at the concise default
    /// width; two shas that agree past it — the failure this exists for, where both would otherwise clip
    /// to the same nine characters — are widened by just enough to expose the first differing character.
    /// Both sides are clipped to one shared width so the reason and the assurance caveat are byte-for-byte
    /// the same diagnostic.
    /// </summary>
    private static string DescribeMismatch(string recorded, string actual)
    {
        int width = DistinguishingWidth(recorded, actual);
        return $"record describes {Clip(recorded, width)}, GitHub head is {Clip(actual, width)}";
    }

    /// <summary>
    /// The display width that keeps two shas distinct: the concise default when they differ early, widened
    /// to one character past a longer shared prefix, and the full length of the longer value when one is an
    /// abbreviated prefix of the other (no character distinguishes them, so the whole of each is shown).
    /// When both values are shorter than the concise default they already differ within their length, so
    /// the lower bound collapses to that length — clamping to <see cref="ShortWidth"/> there would ask for
    /// a minimum wider than the maximum and throw.
    /// </summary>
    private static int DistinguishingWidth(string recorded, string actual)
    {
        int shared = CommonPrefixLength(recorded, actual);
        int longer = Math.Max(recorded.Length, actual.Length);
        if (shared >= Math.Min(recorded.Length, actual.Length))
        {
            return longer;
        }

        return Math.Clamp(shared + 1, Math.Min(ShortWidth, longer), longer);
    }

    /// <summary>Length of the leading run the two shas share, compared case-insensitively as <see cref="ShaMatches"/> does.</summary>
    private static int CommonPrefixLength(string a, string b)
    {
        int length = Math.Min(a.Length, b.Length);
        int i = 0;
        while (i < length && char.ToLowerInvariant(a[i]) == char.ToLowerInvariant(b[i]))
        {
            i++;
        }

        return i;
    }

    private static string Clip(string sha, int width) => sha.Length <= width ? sha : sha[..width];
}
