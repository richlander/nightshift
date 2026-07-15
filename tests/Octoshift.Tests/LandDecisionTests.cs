namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The inbound merge→land mapping: octoshift lands exactly the merged nightshift branches that are not
/// already landed, ignores foreign branches, and collapses duplicate PRs for one order — all idempotently
/// and against a fake board and a fake gh source, so nothing touches the network or a live daemon.
/// </summary>
public class LandDecisionTests
{
    private static MergedPr Pr(int number, string branch, int minutesAgo = 0)
        => new(number, branch, DateTimeOffset.UtcNow.AddMinutes(-minutesAgo));

    private static BoardState Board(params (string OrderBase, string Status)[] rows)
        => BoardState.FromRows(rows.Select(r => new BoardRow { OrderBase = r.OrderBase, Status = r.Status }));

    [Fact]
    public void Decide_LandsUnlandedNightshiftBranch()
    {
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [Pr(42, "nightshift/2/op-a")],
            Board(("/plan/2/order/op-a", "done")));

        LandAction action = Assert.Single(actions);
        Assert.Equal("/plan/2/order/op-a", action.OrderBase);
        Assert.Equal(42, action.PrNumber);
        Assert.Equal("merged #42", action.Reason);
    }

    [Fact]
    public void Decide_SkipsAlreadyLandedOrder()
    {
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [Pr(42, "nightshift/2/op-a")],
            Board(("/plan/2/order/op-a", "landed")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_SkipsForeignBranch()
    {
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [Pr(7, "feature/some-branch"), Pr(8, "main"), Pr(9, "dependabot/nuget/x")],
            BoardState.Empty);

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_LandsOrderAbsentFromBoard()
    {
        // An order not on the board is not landed; nightshift land is the final guard for unknown orders.
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [Pr(11, "nightshift/2/op-new")],
            BoardState.Empty);

        Assert.Equal("/plan/2/order/op-new", Assert.Single(actions).OrderBase);
    }

    [Fact]
    public void Decide_CollapsesDuplicatePrsForOneOrder()
    {
        // Newest-first ordering means the first (newest) PR wins the single land.
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [Pr(50, "nightshift/2/op-a", minutesAgo: 1), Pr(40, "nightshift/2/op-a", minutesAgo: 30)],
            BoardState.Empty);

        Assert.Equal(50, Assert.Single(actions).PrNumber);
    }

    [Fact]
    public void Decide_MixedBatch_LandsOnlyEligible()
    {
        IReadOnlyList<LandAction> actions = LandDecision.Decide(
            [
                Pr(1, "nightshift/2/op-a"),   // land
                Pr(2, "nightshift/2/op-b"),   // already landed -> skip
                Pr(3, "hotfix/thing"),        // foreign -> skip
                Pr(4, "nightshift/2/op-c"),   // land
            ],
            Board(("/plan/2/order/op-b", "landed")));

        Assert.Equal(
            new[] { "/plan/2/order/op-a", "/plan/2/order/op-c" },
            actions.Select(a => a.OrderBase).ToArray());
    }

    [Fact]
    public async Task Sweep_InvokesLandExactlyForEligibleOrders()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-b", "landed")));
        var source = new FakeMergedPrSource(
            Pr(1, "nightshift/2/op-a"),
            Pr(2, "nightshift/2/op-b"),   // landed -> no land
            Pr(3, "release/1.0"),         // foreign -> no land
            Pr(4, "nightshift/2/op-c"));

        await ReconcileCommand.SweepOnceAsync(nightshift, source, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "/plan/2/order/op-a", "/plan/2/order/op-c" },
            nightshift.Lands.Select(l => l.OrderBase).ToArray());
        Assert.Equal("merged #1", nightshift.Lands[0].Reason);
    }

    [Fact]
    public async Task Sweep_LandsNothing_WhenAllLandedOrForeign()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "landed")));
        var source = new FakeMergedPrSource(
            Pr(1, "nightshift/2/op-a"),   // landed
            Pr(2, "chore/tidy"));         // foreign

        await ReconcileCommand.SweepOnceAsync(nightshift, source, TestContext.Current.CancellationToken);

        Assert.Empty(nightshift.Lands);
    }
}
