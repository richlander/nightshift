namespace Octoshift.Commands;

using Octoshift.Coordination;

/// <summary>
/// One order the outbound pass decided is eligible for PR creation.
/// </summary>
internal readonly record struct OpenPrAction(string OrderBase, string HeadBranch);

/// <summary>
/// Pure remote-dev outbound decision (§5): a PR is openable when the board still shows the order at
/// <c>done</c> and no OPEN/MERGED PR already exists for that order branch.
/// </summary>
internal static class OpenPrDecision
{
    /// <summary>
    /// Computes PR-open actions from a board snapshot plus the known set of order branches that already have
    /// OPEN or MERGED PRs.
    /// </summary>
    public static IReadOnlyList<OpenPrAction> Decide(BoardState board, IReadOnlySet<string> openOrMergedHeadBranches)
    {
        var actions = new List<OpenPrAction>();
        foreach (string orderBase in board.GetDoneOrderBases())
        {
            if (OrderRef.FromBase(orderBase) is not { } order)
            {
                continue;
            }

            string headBranch = order.Branch;
            if (openOrMergedHeadBranches.Contains(headBranch))
            {
                continue;
            }

            actions.Add(new OpenPrAction(orderBase, headBranch));
        }

        return actions;
    }
}
