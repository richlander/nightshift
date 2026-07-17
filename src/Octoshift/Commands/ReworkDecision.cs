namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>How octoshift routes an order the rework sweep decided is not on a clean path to merge.</summary>
internal enum ReworkKind
{
    /// <summary>Bounce back to the pool as <c>changes-requested</c> via <c>nightshift rework</c>, carrying a directive.</summary>
    Rework,

    /// <summary>Surface to a human (§4.3 closed-unmerged default); octoshift takes no coordination action.</summary>
    Escalate,
}

/// <summary>
/// One order the rework sweep decided to act on: the routing <see cref="Kind"/>, the human-readable
/// <see cref="Directive"/> (passed to <c>nightshift rework --reason</c> for a bounce, or logged for an
/// escalation), and the PR number that triggered it.
/// </summary>
internal readonly record struct ReworkAction(string OrderBase, ReworkKind Kind, string Directive, int PrNumber);

/// <summary>
/// The pure rework-mapping decision, isolated from GitHub and the nightshift subprocess so it is
/// exhaustively unit-testable: given the open (and closed-unmerged) order-PRs and a board snapshot, it
/// yields the orders to bounce back for rework — the symmetric sibling of <see cref="LandDecision"/> for the
/// outbound direction (design doc §4.2). It is idempotent by construction: it only acts on an order the
/// board still shows at <c>done</c> (submitted, awaiting merge), so once <c>nightshift rework</c> has flipped
/// it to <c>changes-requested</c>, re-seeing the same conflicted/red PR on the next poll produces no second
/// bounce. Foreign branches, unknown mergeability, and pending checks are all no-ops.
/// </summary>
internal static class ReworkDecision
{
    private const string RebaseDirective = "rebase onto main";
    private const string ClosedUnmergedDirective = "closed without merging";

    /// <summary>
    /// The <c>CheckRun.conclusion</c> values that mean a definitively-failed check. Pending/in-progress
    /// (a null conclusion while <c>status</c> is not <c>COMPLETED</c>) and passing conclusions never bounce.
    /// </summary>
    private static readonly HashSet<string> FailedConclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE",
    };

    /// <summary>The <c>StatusContext.state</c> values that mean a definitively-failed check.</summary>
    private static readonly HashSet<string> FailedStates = new(StringComparer.OrdinalIgnoreCase)
    {
        "FAILURE", "ERROR",
    };

    /// <summary>
    /// Decides which open order-PRs must bounce against <paramref name="board"/>. Ignores PRs whose head is
    /// not a nightshift branch and any order the board does not currently show at <c>done</c> (which covers
    /// already-<c>changes-requested</c>, <c>landed</c>, and unknown orders — the idempotency gate). A
    /// <c>CONFLICTING</c> PR routes to a rebase bounce; an otherwise-mergeable PR with a definitively-failed
    /// check routes to a CI bounce; a closed-unmerged PR routes to the §4.3 escalate default; everything else
    /// (unknown mergeability, pending checks, clean PRs) is a no-op.
    /// </summary>
    public static IReadOnlyList<ReworkAction> Decide(IEnumerable<OpenPr> open, BoardState board)
    {
        var actions = new List<ReworkAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (OpenPr pr in open)
        {
            if (OrderRef.FromBranch(pr.HeadBranch) is not { } order)
            {
                continue; // not a nightshift branch — never invent an order
            }

            string orderBase = order.Base;
            if (seen.Contains(orderBase))
            {
                continue; // one action per order per sweep
            }

            // Idempotency + safety gate: only an order still 'done' (submitted, awaiting merge) is eligible.
            // An order already 'changes-requested' (a rework is in flight) or 'landed' (merged) is never
            // re-bounced, and an unknown/foreign order is never invented.
            if (!board.IsDone(orderBase))
            {
                continue;
            }

            if (Classify(orderBase, pr) is { } action)
            {
                actions.Add(action);
                seen.Add(orderBase);
            }
        }

        return actions;
    }

    private static ReworkAction? Classify(string orderBase, OpenPr pr)
    {
        // Closed-unmerged (§4.3). §9.5 leaves this an OPEN decision (rework / pool / escalate). We default to
        // ESCALATE-to-human: a human closing a PR without merging is a deliberate act, and auto-bouncing it
        // straight to rework would just re-ready it into a loop. Octoshift has no in-scope 'escalate'
        // subprocess, so the escalation leaves the order untouched and only surfaces it for judgment
        // (see ApplyReworkAsync). (docs/design/octoshift.md §9.5)
        if (pr.State == PrLifecycle.Closed)
        {
            return new ReworkAction(orderBase, ReworkKind.Escalate, ClosedUnmergedDirective, pr.Number);
        }

        // A conflict needs a rebase regardless of check state — route it first. UNKNOWN mergeability is
        // GitHub computing asynchronously (transient), never a conflict, so it falls through to the checks.
        if (pr.Mergeable == Mergeability.Conflicting)
        {
            return new ReworkAction(orderBase, ReworkKind.Rework, RebaseDirective, pr.Number);
        }

        if (FirstFailedCheck(pr.Checks) is { } failed)
        {
            return new ReworkAction(orderBase, ReworkKind.Rework, CiDirective(failed), pr.Number);
        }

        return null; // clean-so-far, or only pending checks — leave it awaiting merge
    }

    /// <summary>The first definitively-failed check in rollup order, or null when none has failed.</summary>
    private static PrCheck? FirstFailedCheck(IReadOnlyList<PrCheck> checks)
    {
        foreach (PrCheck check in checks)
        {
            if (IsFailed(check))
            {
                return check;
            }
        }

        return null;
    }

    /// <summary>
    /// True when a rollup entry is a definitively-failed check, across both shapes gh returns: a
    /// <c>CheckRun</c> whose <c>conclusion</c> is a failure, or a <c>StatusContext</c> whose <c>state</c> is
    /// a failure. A null/empty conclusion and state (pending/in-progress) is not a failure.
    /// </summary>
    private static bool IsFailed(PrCheck check)
        => (check.Conclusion is { Length: > 0 } conclusion && FailedConclusions.Contains(conclusion))
            || (check.State is { Length: > 0 } state && FailedStates.Contains(state));

    private static string CiDirective(PrCheck failed)
    {
        string name = failed.Name is { Length: > 0 } ? failed.Name : "check";
        return failed.Link is { Length: > 0 } link
            ? $"CI failed: {name} ({link})"
            : $"CI failed: {name}";
    }
}
