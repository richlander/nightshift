namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// <see cref="OrderView.LoadAsync"/> is the shared claim/recover loader for <c>next</c> and <c>show</c>: it
/// reads the immutable spec and, only when the order is at <c>changes-requested</c>, layers the rework
/// <c>mode</c> and <c>findings</c> on top so the emitted WORK packet tells the worker to continue the
/// existing branch. A normal (spec-only) claim carries neither.
/// </summary>
public class OrderViewLoadTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public OrderViewLoadTests(TurnstileFixture fixture) => _fixture = fixture;

    private static string NewBase() => $"/plan/ovl{Guid.NewGuid():N}/order/op1";

    [Fact]
    public async Task LoadAsync_ChangesRequested_AddsModeAndFindings()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"Retry loop\", \"brief\": \"do it\" }", ct);
        await OrderState.WriteAsync(client, orderBase, "changes-requested", "fix", "operator", ct);
        await client.SetAsync($"{orderBase}/rework", "reviewer: harden the sweeper", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Equal("rework", view.Mode);
        Assert.Equal("reviewer: harden the sweeper", view.Findings);
        Assert.Equal("Retry loop", view.Title); // normal spec fields are still present

        string packet = Render(view, orderBase);
        Assert.Contains("mode: rework", packet);
        Assert.Contains("findings: reviewer: harden the sweeper", packet);
        Assert.Contains($"branch: {OrderRef.FromBase(orderBase)!.Value.Branch}", packet);
    }

    [Fact]
    public async Task LoadAsync_SpecOnly_HasNoModeOrFindings()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"Fresh\" }", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Null(view.Mode);
        Assert.Null(view.Findings);

        string packet = Render(view, orderBase);
        Assert.DoesNotContain("mode:", packet);
        Assert.DoesNotContain("findings:", packet);
    }

    [Fact]
    public async Task LoadAsync_Done_IsNotTreatedAsRework()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"Submitted\" }", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);

        Assert.Null(view.Mode);
        Assert.Null(view.Findings);
    }

    [Fact]
    public async Task LoadAsync_Rework_PacketNeverHasModeWithoutFindings()
    {
        // The write-order + empty-file guards guarantee a changes-requested order always has non-empty
        // findings; the packet must therefore never show `mode: rework` without a `findings:` line.
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\" }", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);
        await ReworkCommand.RunAsync(client, orderBase, "reviewer: address the race", null, "operator", ct);

        OrderView view = await OrderView.LoadAsync(client, orderBase, ct);
        string packet = Render(view, orderBase);

        Assert.Contains("mode: rework", packet);
        Assert.Contains("findings:", packet);
    }

    private static string Render(OrderView view, string orderBase)
    {
        var sb = new StringBuilder();
        using var writer = new StringWriter(sb);
        view.PrintWork(writer, orderBase, fence: 1);
        return sb.ToString();
    }
}
