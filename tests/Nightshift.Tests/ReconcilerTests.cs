namespace Nightshift.Tests;

using Nightshift.Commands;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// End-to-end reconciler tests against a live daemon. Each test uses a unique plan id so their keys never
/// collide on the shared daemon — the reconciler only touches its own plan's orders, so cross-plan ready
/// rows are invisible to it.
/// </summary>
public class ReconcilerTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public ReconcilerTests(TurnstileFixture fixture) => _fixture = fixture;

    // Serial -> parallel: a is the root; b and c both depend on a.
    private static Plan MakePlan(string planId) => Plan.Parse(
        $$"""
        { "plan": "{{planId}}",
          "orders": [ { "order": "a" }, { "order": "b", "after": ["a"] }, { "order": "c", "after": ["a"] } ] }
        """, "sha");

    private static string PlanId() => $"rt-{Guid.NewGuid():N}"[..12];

    [Fact]
    public async Task Seed_SerialParallel_OnlyRootReady()
    {
        using TurnstileClient client = _fixture.Connect();
        Plan plan = MakePlan(PlanId());

        Reconciler.Result result = await Reconciler.RunAsync(client, plan, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.SpecsCreated);
        Assert.Equal(1, result.Added);
        Assert.Equal(0, result.Removed);
        await AssertReady(client, plan, "a", expected: true);
        await AssertReady(client, plan, "b", expected: false);
        await AssertReady(client, plan, "c", expected: false);
    }

    [Fact]
    public async Task Reconcile_IsIdempotent()
    {
        using TurnstileClient client = _fixture.Connect();
        Plan plan = MakePlan(PlanId());
        CancellationToken ct = TestContext.Current.CancellationToken;

        await Reconciler.RunAsync(client, plan, ct);
        Reconciler.Result second = await Reconciler.RunAsync(client, plan, ct);

        Assert.Equal(0, second.SpecsCreated);
        Assert.Equal(0, second.Added);
        Assert.Equal(0, second.Removed);
    }

    [Fact]
    public async Task Landed_Root_OpensDependents()
    {
        using TurnstileClient client = _fixture.Connect();
        Plan plan = MakePlan(PlanId());
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Reconciler.RunAsync(client, plan, ct);

        // A merge lands the root.
        await OrderState.WriteAsync(client, plan.Orders[0].Base, "landed", null, "test", ct);
        Reconciler.Result result = await Reconciler.RunAsync(client, plan, ct);

        Assert.Equal(2, result.Added);   // b and c open
        Assert.Equal(1, result.Removed); // a leaves (it is landed)
        await AssertReady(client, plan, "a", expected: false);
        await AssertReady(client, plan, "b", expected: true);
        await AssertReady(client, plan, "c", expected: true);
    }

    [Fact]
    public async Task Done_DoesNotOpenDependents()
    {
        using TurnstileClient client = _fixture.Connect();
        Plan plan = MakePlan(PlanId());
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Reconciler.RunAsync(client, plan, ct);

        // `done` = submitted, awaiting merge — must NOT advance the DAG.
        await OrderState.WriteAsync(client, plan.Orders[0].Base, "done", null, "test", ct);
        Reconciler.Result result = await Reconciler.RunAsync(client, plan, ct);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Removed); // a leaves ready (in-flight)
        await AssertReady(client, plan, "b", expected: false);
        await AssertReady(client, plan, "c", expected: false);
    }

    [Fact]
    public async Task Claimed_Root_LeavesReady()
    {
        using TurnstileClient client = _fixture.Connect();
        Plan plan = MakePlan(PlanId());
        CancellationToken ct = TestContext.Current.CancellationToken;
        await Reconciler.RunAsync(client, plan, ct);

        await client.SetAsync($"{plan.Orders[0].Base}/claim", "agent", ct);
        Reconciler.Result result = await Reconciler.RunAsync(client, plan, ct);

        Assert.Equal(0, result.Added);
        Assert.Equal(1, result.Removed);
        await AssertReady(client, plan, "a", expected: false);
    }

    private static async Task AssertReady(TurnstileClient client, Plan plan, string orderId, bool expected)
    {
        Order order = plan.Orders.Single(o => o.Id == orderId);
        KvItem? row = await client.GetAsync(plan.ReadyKey(order), TestContext.Current.CancellationToken);
        Assert.Equal(expected, row is not null);
    }
}
