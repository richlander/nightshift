namespace Nightshift.Tests;

using System.Text.Json;
using Nightshift.Commands;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The <c>rework</c> coordinator verb: it turns a submitted-but-unlanded order back into the non-terminal
/// <c>changes-requested</c> status, records the review findings, and — critically — preserves the branch and
/// claim so the re-claiming worker continues the SAME branch. It rejects orders that cannot be reworked.
/// </summary>
public class ReworkCommandTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public ReworkCommandTests(TurnstileFixture fixture) => _fixture = fixture;

    private static string NewBase() => $"/plan/rwt{Guid.NewGuid():N}/order/op1";

    [Fact]
    public async Task Rework_DoneOrder_WritesChangesRequested_KeepsBranchAndClaim()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{ \"title\": \"T\" }", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);
        await client.SetAsync($"{orderBase}/branch", "nightshift/x/op1", ct);
        await client.SetAsync($"{orderBase}/claim", "worker-abc", ct);

        int code = await ReworkCommand.RunAsync(client, orderBase, "please fix the retry loop", null, "operator", ct);

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("changes-requested", await StatusAsync(client, orderBase, ct));
        Assert.Equal("please fix the retry loop", await ReasonAsync(client, orderBase, ct));

        KvItem? rework = await client.GetAsync($"{orderBase}/rework", ct);
        Assert.NotNull(rework);
        Assert.Equal("please fix the retry loop", rework!.Text);

        // The continuation anchors survive untouched.
        Assert.Equal("nightshift/x/op1", (await client.GetAsync($"{orderBase}/branch", ct))?.Text.Trim());
        Assert.Equal("worker-abc", (await client.GetAsync($"{orderBase}/claim", ct))?.Text.Trim());
    }

    [Fact]
    public async Task Rework_DefaultReason_WhenNoneGiven()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{}", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);

        int code = await ReworkCommand.RunAsync(client, orderBase, null, null, "operator", ct);

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("changes requested", await ReasonAsync(client, orderBase, ct));
        Assert.Equal("changes requested", (await client.GetAsync($"{orderBase}/rework", ct))?.Text);
    }

    [Fact]
    public async Task Rework_ReasonFile_BecomesFullFindings()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{}", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);

        string findings = "Line 1: race in the sweeper.\nLine 2: add a regression test.\n";
        string path = Path.Combine(AppContext.BaseDirectory, $"rework-{Guid.NewGuid():N}.txt");
        await File.WriteAllTextAsync(path, findings, ct);
        try
        {
            int code = await ReworkCommand.RunAsync(client, orderBase, "short", path, "operator", ct);

            Assert.Equal(ExitCode.Ok, code);
            // The short --reason lands in state.reason; the file's FULL text lands in {base}/rework.
            Assert.Equal("short", await ReasonAsync(client, orderBase, ct));
            Assert.Equal(findings, (await client.GetAsync($"{orderBase}/rework", ct))?.Text);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Rework_AlreadyChangesRequested_RefreshesReasonAndFindings()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{}", ct);
        await OrderState.WriteAsync(client, orderBase, "done", null, "worker", ct);
        await ReworkCommand.RunAsync(client, orderBase, "first pass", null, "operator", ct);

        int code = await ReworkCommand.RunAsync(client, orderBase, "second pass", null, "operator", ct);

        Assert.Equal(ExitCode.Ok, code);
        Assert.Equal("changes-requested", await StatusAsync(client, orderBase, ct));
        Assert.Equal("second pass", await ReasonAsync(client, orderBase, ct));
        Assert.Equal("second pass", (await client.GetAsync($"{orderBase}/rework", ct))?.Text);
    }

    [Fact]
    public async Task Rework_RejectsLandedOrder()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        await client.CreateImmutableAsync($"{orderBase}/spec", "{}", ct);
        await OrderState.WriteAsync(client, orderBase, "landed", null, "operator", ct);

        int code = await ReworkCommand.RunAsync(client, orderBase, "too late", null, "operator", ct);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Equal("landed", await StatusAsync(client, orderBase, ct)); // unchanged
        Assert.Null(await client.GetAsync($"{orderBase}/rework", ct));
    }

    [Fact]
    public async Task Rework_RejectsOrderWithoutSpec()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        int code = await ReworkCommand.RunAsync(client, orderBase, "no spec", null, "operator", ct);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Null(await client.GetAsync($"{orderBase}/state", ct));
        Assert.Null(await client.GetAsync($"{orderBase}/rework", ct));
    }

    [Fact]
    public async Task Rework_RejectsOrderNotYetDone()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string orderBase = NewBase();

        // Spec exists but the order was never submitted (no state) — nothing to send back.
        await client.CreateImmutableAsync($"{orderBase}/spec", "{}", ct);

        int code = await ReworkCommand.RunAsync(client, orderBase, "premature", null, "operator", ct);

        Assert.Equal(ExitCode.Usage, code);
        Assert.Null(await client.GetAsync($"{orderBase}/state", ct));
        Assert.Null(await client.GetAsync($"{orderBase}/rework", ct));
    }

    [Fact]
    public async Task Rework_RejectsMalformedBase()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;

        int code = await ReworkCommand.RunAsync(client, "not-a-base", "x", null, "operator", ct);

        Assert.Equal(ExitCode.Usage, code);
    }

    private static async Task<string?> StatusAsync(TurnstileClient client, string orderBase, CancellationToken ct)
        => await FieldAsync(client, orderBase, "status", ct);

    private static async Task<string?> ReasonAsync(TurnstileClient client, string orderBase, CancellationToken ct)
        => await FieldAsync(client, orderBase, "reason", ct);

    private static async Task<string?> FieldAsync(TurnstileClient client, string orderBase, string field, CancellationToken ct)
    {
        KvItem? state = await client.GetAsync($"{orderBase}/state", ct);
        if (state is null)
        {
            return null;
        }

        using JsonDocument doc = JsonDocument.Parse(state.Text);
        return doc.RootElement.TryGetProperty(field, out JsonElement v) ? v.GetString() : null;
    }
}
