namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The outbound conflict/CI→rework mapping (design doc §4.2): octoshift bounces exactly the open nightshift
/// order-PRs that cannot merge (a conflict or a red check) and whose order the board still shows at
/// <c>done</c>, escalates closed-unmerged PRs (§4.3 default), and does nothing otherwise — idempotently and
/// against a fake board and a fake gh source, so nothing touches the network or a live daemon.
/// </summary>
public class ReworkDecisionTests
{
    private static PrCheck CheckRun(string name, string conclusion, string status = "COMPLETED", string? link = null)
        => new(name, conclusion, State: null, Status: status, Link: link);

    private static PrCheck StatusContext(string name, string state, string? link = null)
        => new(name, Conclusion: null, State: state, Status: null, Link: link);

    private static OpenPr Open(int number, string branch, Mergeability mergeable = Mergeability.Mergeable, params PrCheck[] checks)
        => new(number, branch, PrLifecycle.Open, mergeable, checks);

    private static OpenPr Closed(int number, string branch)
        => new(number, branch, PrLifecycle.Closed, Mergeability.Unknown, []);

    private static BoardState Board(params (string OrderBase, string Status)[] rows)
        => BoardState.FromRows(rows.Select(r => new BoardRow { OrderBase = r.OrderBase, Status = r.Status }));

    [Fact]
    public void Decide_ConflictingBouncesWithRebaseDirective()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Conflicting)],
            Board(("/plan/2/order/op-a", "done")));

        ReworkAction action = Assert.Single(actions);
        Assert.Equal("/plan/2/order/op-a", action.OrderBase);
        Assert.Equal(ReworkKind.Rework, action.Kind);
        Assert.Equal("rebase onto main", action.Directive);
        Assert.Equal(42, action.PrNumber);
    }

    [Fact]
    public void Decide_RedCheckRunBouncesWithCiDirective()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Mergeable, CheckRun("build", "FAILURE", link: "https://ci/build/1"))],
            Board(("/plan/2/order/op-a", "done")));

        ReworkAction action = Assert.Single(actions);
        Assert.Equal(ReworkKind.Rework, action.Kind);
        Assert.Equal("CI failed: build (https://ci/build/1)", action.Directive);
    }

    [Fact]
    public void Decide_RedStatusContextBouncesWithCiDirective()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Mergeable, StatusContext("legacy-ci", "ERROR"))],
            Board(("/plan/2/order/op-a", "done")));

        ReworkAction action = Assert.Single(actions);
        Assert.Equal(ReworkKind.Rework, action.Kind);
        Assert.Equal("CI failed: legacy-ci", action.Directive);
    }

    [Theory]
    [InlineData("FAILURE")]
    [InlineData("TIMED_OUT")]
    [InlineData("CANCELLED")]
    [InlineData("ACTION_REQUIRED")]
    [InlineData("STARTUP_FAILURE")]
    public void Decide_AllFailedCheckRunConclusionsBounce(string conclusion)
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(1, "nightshift/2/op-a", Mergeability.Mergeable, CheckRun("job", conclusion))],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Equal(ReworkKind.Rework, Assert.Single(actions).Kind);
    }

    [Fact]
    public void Decide_UnknownMergeableWithNoFailedChecksDoesNotBounce()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Unknown)],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_PendingChecksDoNotBounce()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Mergeable,
                CheckRun("build", conclusion: "", status: "IN_PROGRESS"),
                StatusContext("legacy", "PENDING"))],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_PassingChecksDoNotBounce()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Mergeable,
                CheckRun("build", "SUCCESS"), CheckRun("test", "NEUTRAL"), StatusContext("legacy", "SUCCESS"))],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_ConflictTakesPrecedenceOverRedCheck()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Conflicting, CheckRun("build", "FAILURE"))],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Equal("rebase onto main", Assert.Single(actions).Directive);
    }

    [Fact]
    public void Decide_SkipsOrderAtChangesRequested()
    {
        // A rework is already in flight — never re-bounce it (the idempotency gate).
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Conflicting)],
            Board(("/plan/2/order/op-a", "changes-requested")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_SkipsLandedOrder()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(42, "nightshift/2/op-a", Mergeability.Conflicting)],
            Board(("/plan/2/order/op-a", "landed")));

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_SkipsForeignBranch()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(7, "feature/some-branch", Mergeability.Conflicting), Open(8, "main", Mergeability.Conflicting)],
            BoardState.Empty);

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_SkipsOrderAbsentFromBoard()
    {
        // Absent from the board => not at 'done' => never invent a bounce for an unknown order.
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(11, "nightshift/2/op-new", Mergeability.Conflicting)],
            BoardState.Empty);

        Assert.Empty(actions);
    }

    [Fact]
    public void Decide_IdempotentAcrossPolls_BoardGateStopsSecondBounce()
    {
        var pr = Open(42, "nightshift/2/op-a", Mergeability.Conflicting);

        // First poll: order is 'done' => one bounce.
        Assert.Single(ReworkDecision.Decide([pr], Board(("/plan/2/order/op-a", "done"))));

        // The bounce flipped it to 'changes-requested'; re-seeing the same conflicted PR => no second bounce.
        Assert.Empty(ReworkDecision.Decide([pr], Board(("/plan/2/order/op-a", "changes-requested"))));
    }

    [Fact]
    public void Decide_ClosedUnmergedEscalates()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Closed(42, "nightshift/2/op-a")],
            Board(("/plan/2/order/op-a", "done")));

        ReworkAction action = Assert.Single(actions);
        Assert.Equal(ReworkKind.Escalate, action.Kind);
        Assert.Equal("/plan/2/order/op-a", action.OrderBase);
        Assert.Equal("closed without merging", action.Directive);
    }

    [Fact]
    public void Decide_CollapsesToOneActionPerOrder()
    {
        // Two open PRs on the same branch (pathological) collapse to a single bounce.
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [Open(50, "nightshift/2/op-a", Mergeability.Conflicting), Open(40, "nightshift/2/op-a", Mergeability.Conflicting)],
            Board(("/plan/2/order/op-a", "done")));

        Assert.Equal(50, Assert.Single(actions).PrNumber);
    }

    [Fact]
    public void Decide_MixedBatch_BouncesOnlyEligible()
    {
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(
            [
                Open(1, "nightshift/2/op-a", Mergeability.Conflicting),                         // bounce: rebase
                Open(2, "nightshift/2/op-b", Mergeability.Mergeable, CheckRun("ci", "FAILURE")), // bounce: CI
                Open(3, "nightshift/2/op-c", Mergeability.Mergeable),                            // clean -> skip
                Open(4, "nightshift/2/op-d", Mergeability.Conflicting),                          // not done -> skip
                Open(5, "hotfix/thing", Mergeability.Conflicting),                              // foreign -> skip
            ],
            Board(
                ("/plan/2/order/op-a", "done"),
                ("/plan/2/order/op-b", "done"),
                ("/plan/2/order/op-c", "done"),
                ("/plan/2/order/op-d", "landed")));

        Assert.Equal(
            new[] { "/plan/2/order/op-a", "/plan/2/order/op-b" },
            actions.Select(a => a.OrderBase).ToArray());
        Assert.Equal("rebase onto main", actions[0].Directive);
        Assert.Equal("CI failed: ci", actions[1].Directive);
    }

    [Fact]
    public async Task Sweep_InvokesReworkExactlyForEligibleOrders()
    {
        var nightshift = new FakeNightshiftClient(Board(
            ("/plan/2/order/op-a", "done"),
            ("/plan/2/order/op-b", "done"),
            ("/plan/2/order/op-c", "landed")));
        var source = new FakeOpenPrSource(
            Open(1, "nightshift/2/op-a", Mergeability.Conflicting),                          // rework
            Open(2, "nightshift/2/op-b", Mergeability.Mergeable, CheckRun("ci", "FAILURE")),  // rework
            Open(3, "nightshift/2/op-c", Mergeability.Conflicting),                           // landed -> skip
            Open(4, "release/1.0", Mergeability.Conflicting));                               // foreign -> skip

        await ReconcileCommand.SweepReworkOnceAsync(nightshift, source, TestContext.Current.CancellationToken);

        Assert.Equal(
            new[] { "/plan/2/order/op-a", "/plan/2/order/op-b" },
            nightshift.Reworks.Select(r => r.OrderBase).ToArray());
        Assert.Equal("rebase onto main", nightshift.Reworks[0].Directive);
        Assert.Equal("CI failed: ci", nightshift.Reworks[1].Directive);
    }

    [Fact]
    public async Task Sweep_ClosedUnmergedDoesNotInvokeRework()
    {
        // The escalate default takes no coordination action — the order is left untouched for a human.
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var source = new FakeOpenPrSource(Closed(1, "nightshift/2/op-a"));

        IReadOnlyList<ReworkAction> actions = await ReconcileCommand.SweepReworkOnceAsync(
            nightshift, source, TestContext.Current.CancellationToken);

        Assert.Empty(nightshift.Reworks);
        Assert.Equal(ReworkKind.Escalate, Assert.Single(actions).Kind);
    }
}
