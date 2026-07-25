namespace Nightshift.Tests;

using System.Text;
using System.Text.Json;
using Nightshift.Commands;
using Nightshift.Output;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The per-order <b>base ref</b> plumbing (stacked orders, issue 120): the coordinator-written
/// <c>{base}/base-ref</c> key that <see cref="OrderView.LoadAsync"/> surfaces into the WORK packet as a
/// <c>base-ref:</c> body line — defaulting to <c>main</c> when unset — plus the plan-schema tolerance for
/// an <c>after</c> edge that declares a dependency kind. The WORK packet stays byte-reproducible across
/// <c>next</c> / <c>show</c> / <c>recover</c>, which all render through <see cref="OrderView.LoadAsync"/>.
/// </summary>
public class BaseRefPlumbingTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public BaseRefPlumbingTests(TurnstileFixture fixture) => _fixture = fixture;

    private static string NewBase() => $"/plan/br{Guid.NewGuid():N}/order/op1";

    [Fact]
    public async Task LoadAsync_NoKey_DefaultsToMainInPacket()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"Independent\" }", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Equal("main", view.BaseRef);
        Assert.Contains("base-ref: main", Render(view, orderBase));
    }

    [Fact]
    public async Task LoadAsync_ReadsWrittenBaseRef()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"Stacked child\" }", ct);
        await client.SetAsync($"{orderBase}/base-ref", "a1b2c3d4e5f6", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Equal("a1b2c3d4e5f6", view.BaseRef);
        Assert.Contains("base-ref: a1b2c3d4e5f6", Render(view, orderBase));
    }

    [Fact]
    public async Task LoadAsync_BlankBaseRef_FallsBackToMain()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\" }", ct);
        await client.SetAsync($"{orderBase}/base-ref", "   ", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Equal("main", view.BaseRef);
    }

    [Fact]
    public async Task Packet_BaseRefSitsRightAfterBranch()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\", \"brief\": \"b\" }", ct);
        await client.SetAsync($"{orderBase}/base-ref", "feature/contract", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);
        string[] lines = Render(view, orderBase).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        int branchIdx = Array.IndexOf(lines, $"branch: {OrderRef.FromBase(orderBase)!.Value.Branch}");
        Assert.True(branchIdx >= 0);
        Assert.Equal("base-ref: feature/contract", lines[branchIdx + 1]);
    }

    [Fact]
    public async Task Packet_Rework_BaseRefPrecedesMode()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\" }", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);
        await ReworkCommand.RunAsync(client, orderBase, "reviewer: harden it", null, "operator", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);
        string[] lines = Render(view, orderBase).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

        int baseRefIdx = Array.IndexOf(lines, "base-ref: main");
        int modeIdx = Array.IndexOf(lines, "mode: rework");
        Assert.True(baseRefIdx >= 0 && modeIdx >= 0);
        Assert.True(baseRefIdx < modeIdx, "base-ref must precede mode in the WORK packet");
    }

    [Fact]
    public async Task Show_PlaintextMatchesWorkPacketWithBaseRef()
    {
        // `show` recovers the claim by reparsing this, so its plaintext must stay byte-identical to the
        // packet `next` printed — base-ref line included.
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\", \"brief\": \"b\" }", ct);
        await client.SetAsync($"{orderBase}/base-ref", "deadbeef", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        using var shown = new StringWriter();
        ShowCommand.Render(view, orderBase, fence: 7, OutputFormat.Plaintext, shown);

        using var expected = new StringWriter();
        view.PrintWork(expected, orderBase, fence: 7);

        Assert.Equal(expected.ToString(), shown.ToString());
        Assert.Contains("base-ref: deadbeef", shown.ToString());
    }

    [Fact]
    public async Task ShowFields_BaseRefRowFollowsBranch()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\" }", ct);
        await client.SetAsync($"{orderBase}/base-ref", "abc123", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);
        List<OrderField> fields = ShowCommand.BuildFields(view, orderBase, fence: 1);

        int baseRefIdx = fields.FindIndex(f => f.Field == "base-ref");
        Assert.True(baseRefIdx > 0);
        Assert.Equal("branch", fields[baseRefIdx - 1].Field);
        Assert.Equal("abc123", fields[baseRefIdx].Value);
    }

    [Fact]
    public void Parse_UsesSpecOnly_HasNoBaseRef()
    {
        // The base ref is per-order coordination state, never part of the immutable spec: the spec-only
        // Parse path leaves it null, so a plan authoring `base-ref` cannot inject one.
        OrderView view = OrderView.Parse("""{ "title": "T", "base-ref": "sneaky" }""");

        Assert.Null(view.BaseRef);

        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        view.PrintWork(writer, "/plan/p/order/x", fence: 1);
        Assert.DoesNotContain("base-ref:", sb.ToString());
    }

    [Fact]
    public void PlanSchema_AfterAcceptsDependencyKindObjects()
    {
        // The settled authoring model: a planner declares an `after` edge as { order, kind } to express a
        // dependency kind. Parsing extracts the id for the DAG; the spec normalizes to ids.
        Plan plan = Plan.Parse(
            """
            { "plan": "stacked", "orders": [
                { "order": "contract" },
                { "order": "variant", "after": [ { "order": "contract", "kind": "stacked" }, "other" ] } ] }
            """,
            "deadbeefcafe");

        Order variant = plan.Orders[1];
        Assert.Equal(["contract", "other"], variant.After);

        using JsonDocument doc = JsonDocument.Parse(variant.SpecJson);
        string[] specAfter = doc.RootElement.GetProperty("after").EnumerateArray().Select(e => e.GetString()!).ToArray();
        Assert.Equal(["contract", "other"], specAfter);
    }

    private static string Render(OrderView view, string orderBase)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        view.PrintWork(writer, orderBase, fence: 1);
        return sb.ToString();
    }
}
