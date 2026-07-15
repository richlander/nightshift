namespace Nightshift.Commands;

using System.Text.Json;
using System.Text.Json.Serialization;
using Markout;
using Nightshift.Output;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift where</c> — the coordinator's board. Ranges <c>/plan/</c> and, for every order that has
/// been claimed or reported, prints one row: <c>&lt;order-base&gt;  &lt;status&gt;  &lt;branch&gt;</c>. An order
/// surfaces the moment its <c>/branch</c> key is minted (at claim); its status is drawn from <c>/state</c>
/// when a worker has released it, or <c>claimed</c> while still in hand. Read-only.
/// </summary>
internal static class WhereCommand
{
    private const string PlanRoot = "/plan/";
    private const string StateSuffix = "/state";
    private const string BranchSuffix = "/branch";

    public static async Task<int> RunAsync(OutputFormat output)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        IReadOnlyList<KvItem> items = await client.RangeAsync(PlanRoot, ct);

        List<WhereRow> rows = BuildRows(items);
        if (rows.Count == 0)
        {
            RenderEmpty(output, Console.Out);
            return ExitCode.Ok;
        }

        RenderRows(rows, output, Console.Out);
        return ExitCode.Ok;
    }

    internal static List<WhereRow> BuildRows(IEnumerable<KvItem> items)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (KvItem item in items)
        {
            if (BaseOf(item.Key, StateSuffix) is { } stateBase)
            {
                statuses[stateBase] = ParseStatus(item.Text);
            }
            else if (BaseOf(item.Key, BranchSuffix) is { } branchBase)
            {
                branches[branchBase] = item.Text.Trim();
            }
        }

        var bases = new SortedSet<string>(statuses.Keys, StringComparer.Ordinal);
        bases.UnionWith(branches.Keys);
        return bases
            .Select(orderBase => new WhereRow
            {
                OrderBase = orderBase,
                Status = statuses.TryGetValue(orderBase, out string? status) ? status : "claimed",
                Branch = branches.TryGetValue(orderBase, out string? branch) ? branch : string.Empty,
            })
            .ToList();
    }

    internal static void RenderRows(IReadOnlyList<WhereRow> rows, OutputFormat output, TextWriter writer)
        => OutputFormatter.Render(
            new WhereView { Rows = rows.ToList() },
            rows.ToList(),
            writer,
            output,
            WhereViewContext.Default,
            WhereJsonContext.Default.ListWhereRow);

    internal static void RenderEmpty(OutputFormat output, TextWriter writer)
    {
        switch (output)
        {
            case OutputFormat.Json:
                RenderRows([], OutputFormat.Json, writer);
                break;
            case OutputFormat.Jsonl:
                break;
            case OutputFormat.Plaintext:
            case OutputFormat.Table:
            case OutputFormat.Markdown:
            case OutputFormat.Tsv:
                writer.WriteLine("(no orders)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(output), output, null);
        }
    }

    /// <summary>Returns the order base for a <c>{base}{suffix}</c> key, or null when the key does not end in the suffix.</summary>
    internal static string? BaseOf(string key, string suffix)
        => key.EndsWith(suffix, StringComparison.Ordinal) ? key[..^suffix.Length] : null;

    /// <summary>Extracts <c>status</c> from an order's state JSON; returns <c>?</c> when absent or unparseable.</summary>
    internal static string ParseStatus(string stateJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out JsonElement status)
                && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() ?? "?";
            }
        }
        catch (JsonException)
        {
            // Not our shape — fall through to the sentinel.
        }

        return "?";
    }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class WhereView
{
    [MarkoutSection(Headless = true)]
    public required List<WhereRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record WhereRow
{
    [MarkoutPropertyName("Order base")]
    public required string OrderBase { get; init; }
    public required string Status { get; init; }
    public required string Branch { get; init; }
}

[MarkoutContext(typeof(WhereView))]
internal partial class WhereViewContext : MarkoutSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<WhereRow>))]
internal partial class WhereJsonContext : JsonSerializerContext
{
}
