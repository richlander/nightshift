namespace Nightshift.Commands;

using System.Globalization;
using System.Text.Json.Serialization;
using Markout;
using Nightshift.Output;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift show</c> — reprint the current claim's WORK packet. State lives in Turnstile and the
/// worktree, never in the model, so an agent that compacted or resumed can recover exactly what it is
/// working on WITHOUT claiming again. Read-only: no lease renewal, no state change. The default plaintext
/// view is the WORK packet verbatim (the recovery contract); <c>--output</c> exposes the same order as
/// structured [field, value] rows for machine/dashboard consumers.
/// </summary>
internal static class ShowCommand
{
    public static async Task<int> RunAsync(OutputFormat output)
    {
        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift show: no active claim (run `nightshift next` first)");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        OrderView view = await OrderView.LoadAsync(client, session.OrderBase, ct);
        Render(view, session.OrderBase, session.Fence, output, Console.Out);
        return ExitCode.Ok;
    }

    internal static void Render(OrderView view, string orderBase, long fence, OutputFormat output, TextWriter writer)
    {
        if (output == OutputFormat.Plaintext)
        {
            // Plaintext IS the WORK packet: agents recover their task by reparsing this, so it must stay
            // byte-identical to `next` (WORK <base> ... fence). Never route it through a table formatter.
            view.PrintWork(writer, orderBase, fence);
            return;
        }

        List<OrderField> fields = BuildFields(view, orderBase, fence);
        OutputFormatter.Render(
            new OrderPacketView { Fields = fields },
            fields,
            writer,
            output,
            OrderPacketViewContext.Default,
            ShowJsonContext.Default.ListOrderField);
    }

    /// <summary>Decomposes the WORK packet into [field, value] rows in the same order <c>PrintWork</c> emits.</summary>
    internal static List<OrderField> BuildFields(OrderView view, string orderBase, long fence)
    {
        var fields = new List<OrderField> { new() { Field = "base", Value = orderBase } };
        if (OrderRef.FromBase(orderBase) is { } order)
        {
            fields.Add(new OrderField { Field = "branch", Value = order.Branch });
        }

        AddText(fields, "mode", view.Mode);
        AddText(fields, "title", view.Title);
        AddText(fields, "issue", view.Issue);
        AddList(fields, "paths", view.Paths);
        AddList(fields, "supersedes", view.Supersedes);
        AddText(fields, "standard", view.Standard);
        AddList(fields, "related", view.Related);
        AddList(fields, "antipatterns", view.Antipatterns);
        AddText(fields, "brief", view.Brief);
        AddText(fields, "findings", view.Findings);
        AddText(fields, "order_sha", view.OrderSha);
        fields.Add(new OrderField { Field = "fence", Value = fence.ToString(CultureInfo.InvariantCulture) });
        return fields;
    }

    private static void AddText(List<OrderField> fields, string name, string? value)
    {
        if (value is { Length: > 0 })
        {
            fields.Add(new OrderField { Field = name, Value = value });
        }
    }

    private static void AddList(List<OrderField> fields, string name, string[] values)
    {
        if (values.Length > 0)
        {
            fields.Add(new OrderField { Field = name, Value = string.Join(", ", values) });
        }
    }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class OrderPacketView
{
    [MarkoutSection(Headless = true)]
    public required List<OrderField> Fields { get; init; }
}

[MarkoutSerializable]
internal sealed record OrderField
{
    public required string Field { get; init; }
    public required string Value { get; init; }
}

[MarkoutContext(typeof(OrderPacketView))]
internal partial class OrderPacketViewContext : MarkoutSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<OrderField>))]
internal partial class ShowJsonContext : JsonSerializerContext
{
}
