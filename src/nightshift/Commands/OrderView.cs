namespace Nightshift.Commands;

using System.Text.Json;

/// <summary>
/// An order's brief as the agent sees it, plus the WORK-packet renderer shared by <c>next</c> (on claim)
/// and <c>show</c> (re-read the current claim after a context reset). Same format both times, so an agent
/// that compacted can recover its task verbatim without re-claiming.
/// </summary>
internal sealed record OrderView(
    string[] Paths,
    string[] Supersedes,
    string[] Related,
    string[] Antipatterns,
    string? Standard,
    string? Issue,
    string? Title,
    string? OrderSha,
    string? Brief)
{
    public static OrderView Empty { get; } = new([], [], [], [], null, null, null, null, null);

    public static OrderView Parse(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            return new OrderView(
                StringArray(root, "paths"),
                StringArray(root, "supersedes"),
                StringArray(root, "related"),
                StringArray(root, "antipatterns"),
                Str(root, "standard"),
                Str(root, "issue"),
                Str(root, "title"),
                Str(root, "order_sha"),
                Str(root, "brief"));
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>Prints the WORK packet: a <c>WORK &lt;base&gt;</c> header, the present fields, then the fence.</summary>
    public void PrintWork(TextWriter output, string orderBase, long fence)
    {
        output.WriteLine($"WORK {orderBase}");
        if (OrderRef.FromBase(orderBase) is { } order)
        {
            Line(output, "branch", order.Branch);
        }

        Line(output, "title", Title);
        Line(output, "issue", Issue);
        Line(output, "paths", Paths);
        Line(output, "supersedes", Supersedes);
        Line(output, "standard", Standard);
        Line(output, "related", Related);
        Line(output, "antipatterns", Antipatterns);
        Line(output, "brief", Brief);
        Line(output, "order_sha", OrderSha);
        output.WriteLine($"fence: {fence}");
    }

    private static void Line(TextWriter output, string label, string? value)
    {
        if (value is { Length: > 0 })
        {
            output.WriteLine($"{label}: {value}");
        }
    }

    private static void Line(TextWriter output, string label, string[] values)
    {
        if (values.Length > 0)
        {
            output.WriteLine($"{label}: {string.Join(", ", values)}");
        }
    }

    private static string[] StringArray(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? string.Empty : e.GetRawText())
                .Where(s => s.Length > 0)
                .ToArray()
            : [];

    private static string? Str(JsonElement root, string name)
        => root.TryGetProperty(name, out JsonElement v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Number => v.GetRawText(),
                _ => null,
            }
            : null;
}
