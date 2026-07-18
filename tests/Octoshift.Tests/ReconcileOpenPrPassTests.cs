namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// Integration coverage for the outbound done→PR-open pass in reconcile: open eligibility, resident-loop
/// dedup across polls, and fail-closed behavior when existing-PR discovery fails.
/// </summary>
public class ReconcileOpenPrPassTests
{
    private static BoardState Board(params (string OrderBase, string Status)[] rows)
        => BoardState.FromRows(rows.Select(r => new BoardRow { OrderBase = r.OrderBase, Status = r.Status }));

    [Fact]
    public async Task SweepOpenPrOnce_OpensEligibleDoneOrder()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var existing = new FakeExistingOrderPrSource(
            new ExistingOrderPrsSnapshot(Success: true, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal)));
        var opener = new FakePrOpenSource(new PrOpenOutcome(PrOpenOutcomeKind.Opened, 42));

        IReadOnlyList<OpenPrAction> actions = await ReconcileCommand.SweepOpenPrOnceAsync(
            nightshift,
            existing,
            opener,
            TestContext.Current.CancellationToken);

        OpenPrAction action = Assert.Single(actions);
        Assert.Equal("/plan/2/order/op-a", action.OrderBase);
        Assert.Equal([("/plan/2/order/op-a", "nightshift/2/op-a")], opener.Opens);
    }

    [Fact]
    public async Task OpenPrPass_DedupsAcrossPollsViaRunState()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var existing = new FakeExistingOrderPrSource(
            new ExistingOrderPrsSnapshot(Success: true, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal)),
            new ExistingOrderPrsSnapshot(Success: true, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal)));
        var opener = new FakePrOpenSource(
            new PrOpenOutcome(PrOpenOutcomeKind.Opened, 42),
            new PrOpenOutcome(PrOpenOutcomeKind.Opened, 42));
        var state = new ReconcileCommand.ReconcileState();

        await ReconcileCommand.OpenPrPassAsync(nightshift, existing, opener, state, TestContext.Current.CancellationToken);
        await ReconcileCommand.OpenPrPassAsync(nightshift, existing, opener, state, TestContext.Current.CancellationToken);

        Assert.Single(opener.Opens);
        Assert.Contains("/plan/2/order/op-a", state.OpenedOrders);
    }

    [Fact]
    public async Task SweepOpenPrOnce_FetchFailureFailsClosed()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var existing = new FakeExistingOrderPrSource(
            new ExistingOrderPrsSnapshot(Success: false, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal)));
        var opener = new FakePrOpenSource(new PrOpenOutcome(PrOpenOutcomeKind.Opened, 42));

        IReadOnlyList<OpenPrAction> actions = await ReconcileCommand.SweepOpenPrOnceAsync(
            nightshift,
            existing,
            opener,
            TestContext.Current.CancellationToken);

        Assert.Empty(actions);
        Assert.Empty(opener.Opens);
    }

    [Fact]
    public async Task OpenPrPass_TransientOpenFailure_DoesNotThrowOrMarkOpened()
    {
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var existing = new FakeExistingOrderPrSource(
            new ExistingOrderPrsSnapshot(Success: true, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal)));
        var opener = new GhPrOpenSource(
            "owner/repo",
            new GitHubActorIdentity("nightshift-bot[app]"),
            (_, _) => throw new InvalidOperationException("token exchange failed"));
        var state = new ReconcileCommand.ReconcileState();

        IReadOnlyList<OpenPrAction> actions = await ReconcileCommand.OpenPrPassAsync(
            nightshift,
            existing,
            opener,
            state,
            TestContext.Current.CancellationToken);

        Assert.Single(actions);
        Assert.Empty(state.OpenedOrders);
    }
}
