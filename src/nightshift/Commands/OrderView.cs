namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

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
    /// <summary>The non-terminal status that turns a claim into a rework continuation of its existing branch.</summary>
    internal const string ChangesRequested = "changes-requested";

    /// <summary>
    /// Set to <c>rework</c> when the claimed order is at <see cref="ChangesRequested"/>: it tells the worker
    /// the <c>branch</c> already exists on origin with prior work — fetch and CONTINUE it, do not cut a fresh
    /// one off main. Absent (null) on a normal first claim. Carried outside the spec-shaped primary
    /// constructor because it is drawn from <c>{base}/state</c>, not the immutable spec.
    /// </summary>
    public string? Mode { get; init; }

    /// <summary>The review findings for a rework (from <c>{base}/rework</c>) — the brief the re-claiming worker acts on.</summary>
    public string? Findings { get; init; }

    public static OrderView Empty { get; } = new([], [], [], [], null, null, null, null, null);

    /// <summary>
    /// Loads an order's WORK view from Turnstile: the immutable <c>{base}/spec</c>, plus — when the order is
    /// at <see cref="ChangesRequested"/> — the rework <see cref="Mode"/> and <see cref="Findings"/> read from
    /// <c>{base}/state</c> and <c>{base}/rework</c>. Shared by <c>next</c> (on claim) and <c>show</c> (recover
    /// after a reset) so both render the identical packet, rework directive included.
    /// </summary>
    public static async Task<OrderView> LoadAsync(TurnstileClient client, string orderBase, CancellationToken ct)
    {
        KvItem? spec = await client.GetAsync($"{orderBase}/spec", ct);
        OrderView view = spec is null ? Empty : Parse(spec.Text);

        if (await StatusOfAsync(client, orderBase, ct) == ChangesRequested)
        {
            KvItem? rework = await client.GetAsync($"{orderBase}/rework", ct);
            view = view with { Mode = "rework", Findings = rework?.Text };
        }

        return view;
    }

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

        Line(output, "mode", Mode);
        Line(output, "title", Title);
        Line(output, "issue", Issue);
        Line(output, "paths", Paths);
        Line(output, "supersedes", Supersedes);
        Line(output, "standard", Standard);
        Line(output, "related", Related);
        Line(output, "antipatterns", Antipatterns);
        Line(output, "brief", Brief);
        Line(output, "findings", Findings);
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

    /// <summary>Reads the <c>status</c> field from an order's <c>{base}/state</c> key; null when unset or unparseable.</summary>
    private static async Task<string?> StatusOfAsync(TurnstileClient client, string orderBase, CancellationToken ct)
    {
        KvItem? state = await client.GetAsync($"{orderBase}/state", ct);
        if (state is null)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(state.Text);
            return doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
