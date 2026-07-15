namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>One order the sweep decided to land, with the reason string recorded on its state.</summary>
internal readonly record struct LandAction(string OrderBase, int PrNumber)
{
    /// <summary>The land reason passed to <c>nightshift land --reason</c>, e.g. <c>merged #42</c>.</summary>
    public string Reason => $"merged #{PrNumber}";
}

/// <summary>
/// The pure land-mapping decision, isolated from GitHub and the nightshift subprocess so it is
/// exhaustively unit-testable: given a batch of merged PRs and a board snapshot, it yields the orders to
/// land. The rules are trivial and idempotent — map branch→order, skip anything not on a
/// <c>nightshift/{plan}/{order}</c> branch, and skip anything already landed — so re-seeing the same merges
/// produces no new work. <c>nightshift land</c> itself is the final guard against unknown orders.
/// </summary>
internal static class LandDecision
{
    /// <summary>
    /// Decides which merged PRs are landable against <paramref name="board"/>. Ignores PRs whose head is
    /// not a nightshift branch and orders already landed. When two merged PRs map to the same order, the
    /// first (newest, since the source sorts newest-first) wins and the rest collapse (one land).
    /// </summary>
    public static IReadOnlyList<LandAction> Decide(IEnumerable<MergedPr> merged, BoardState board)
    {
        var actions = new List<LandAction>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (MergedPr pr in merged)
        {
            if (OrderRef.FromBranch(pr.HeadBranch) is not { } order)
            {
                continue; // not a nightshift branch — never invent an order
            }

            string orderBase = order.Base;
            if (board.IsLanded(orderBase))
            {
                continue; // already landed (idempotent no-op)
            }

            if (seen.Add(orderBase))
            {
                actions.Add(new LandAction(orderBase, pr.Number));
            }
        }

        return actions;
    }
}
