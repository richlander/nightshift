namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Xunit;

/// <summary>
/// Pure done→PR-open eligibility: only board-done orders without an existing OPEN/MERGED branch PR are
/// openable, with branch identity resolved from the order base.
/// </summary>
public class OpenPrDecisionTests
{
    private static BoardState Board(params (string OrderBase, string Status)[] rows)
        => BoardState.FromRows(rows.Select(r => new BoardRow { OrderBase = r.OrderBase, Status = r.Status }));

    [Fact]
    public void Decide_DoneWithoutExistingBranch_IsEligible()
    {
        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(
            Board(("/plan/2/order/op-a", "done")),
            new HashSet<string>(StringComparer.Ordinal));

        OpenPrAction action = Assert.Single(actions);
        Assert.Equal("/plan/2/order/op-a", action.OrderBase);
        Assert.Equal("nightshift/2/op-a", action.HeadBranch);
    }

    [Fact]
    public void Decide_DoneWithExistingBranch_Skips()
    {
        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(
            Board(("/plan/2/order/op-a", "done")),
            new HashSet<string>(["nightshift/2/op-a"], StringComparer.Ordinal));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_NotDone_Skips()
    {
        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(
            Board(("/plan/2/order/op-a", "open"), ("/plan/2/order/op-b", "landed")),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_DoneButUnparseableOrderBase_Skips()
    {
        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(
            Board(("/plan/2/not-an-order/op-a", "done")),
            new HashSet<string>(StringComparer.Ordinal));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_UsesBranchNamespaceInsteadOfPrNumber()
    {
        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(
            Board(("/plan/2/order/op-a", "done"), ("/plan/2/order/op-b", "done")),
            new HashSet<string>(["nightshift/2/op-a"], StringComparer.Ordinal));

        OpenPrAction action = Assert.Single(actions);
        Assert.Equal("/plan/2/order/op-b", action.OrderBase);
        Assert.Equal("nightshift/2/op-b", action.HeadBranch);
    }
}
