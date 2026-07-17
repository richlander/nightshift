namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>
/// One issue the fan-out close pass decided is eligible to close: every bound order PR is merged and at
/// least one merged order PR exists.
/// </summary>
internal readonly record struct IssueCloseAction(int IssueNumber, IReadOnlyList<string> Orders);

/// <summary>
/// Pure §4.3 fan-out issue-closing decision: given nightshift order PRs with issue bindings, decide which
/// issues are closable. Only PRs on parseable nightshift branches participate, and an issue is closable iff
/// it has at least one merged bound order PR and no open/closed-unmerged bound order PR remains.
/// </summary>
internal static class IssueCloseDecision
{
    public static IReadOnlyList<IssueCloseAction> Decide(IEnumerable<OrderIssuePr> orderPrs)
    {
        var chosen = new Dictionary<string, CollapsedOrder>(StringComparer.Ordinal);
        foreach (OrderIssuePr pr in orderPrs)
        {
            if (OrderRef.FromBranch(pr.HeadBranch) is not { } orderRef)
            {
                continue; // never invent an order from a foreign/unparseable branch
            }

            string orderBase = orderRef.Base;
            if (!chosen.TryGetValue(orderBase, out CollapsedOrder? current))
            {
                chosen[orderBase] = new CollapsedOrder(orderBase, pr.State, pr.ClosingIssueNumbers);
                continue;
            }

            int incomingRank = Rank(pr.State);
            int currentRank = Rank(current.State);
            if (incomingRank > currentRank)
            {
                chosen[orderBase] = new CollapsedOrder(orderBase, pr.State, pr.ClosingIssueNumbers);
                continue;
            }

            if (incomingRank == currentRank)
            {
                current.IssueNumbers.UnionWith(pr.ClosingIssueNumbers);
            }
        }

        var byIssue = new Dictionary<int, Aggregate>();
        foreach (CollapsedOrder order in chosen.Values)
        {
            foreach (int issueNumber in order.IssueNumbers)
            {
                if (issueNumber <= 0)
                {
                    continue;
                }

                if (!byIssue.TryGetValue(issueNumber, out Aggregate? aggregate))
                {
                    aggregate = new Aggregate();
                    byIssue.Add(issueNumber, aggregate);
                }

                if (order.State == OrderPrState.Merged)
                {
                    aggregate.HasMerged = true;
                    aggregate.MergedOrders.Add(order.OrderBase);
                }
                else
                {
                    aggregate.HasUnmerged = true;
                }
            }
        }

        var actions = new List<IssueCloseAction>();
        foreach ((int issueNumber, Aggregate aggregate) in byIssue)
        {
            if (!aggregate.HasMerged || aggregate.HasUnmerged)
            {
                continue;
            }

            actions.Add(new IssueCloseAction(issueNumber, aggregate.MergedOrders.OrderBy(static order => order, StringComparer.Ordinal).ToArray()));
        }

        actions.Sort(static (a, b) => a.IssueNumber.CompareTo(b.IssueNumber));
        return actions;
    }

    private static int Rank(OrderPrState state) => state switch
    {
        OrderPrState.Merged => 2,
        OrderPrState.Open => 1,
        _ => 0,
    };

    private sealed class Aggregate
    {
        public bool HasMerged { get; set; }

        public bool HasUnmerged { get; set; }

        public HashSet<string> MergedOrders { get; } = new(StringComparer.Ordinal);
    }

    private sealed class CollapsedOrder
    {
        public CollapsedOrder(string orderBase, OrderPrState state, IEnumerable<int> issueNumbers)
        {
            OrderBase = orderBase;
            State = state;
            IssueNumbers = new HashSet<int>(issueNumbers.Where(static number => number > 0));
        }

        public string OrderBase { get; }

        public OrderPrState State { get; set; }

        public HashSet<int> IssueNumbers { get; }
    }
}
