namespace Nightshift.Commands;

using System.Text;
using System.Text.Json;

/// <summary>
/// An order: <b>one landable PR</b> — the atomic claim unit and land unit. Bound to at most one issue.
/// Carries its Turnstile key base, its order-to-order dependencies, and the immutable spec to seed.
/// </summary>
internal sealed record Order(string Id, string Base, string[] After, string SpecJson);

/// <summary>
/// The in-memory projection of a plan (<c>orders.json</c>) — pure, no I/O. A plan is the set of orders
/// (landable PRs) for a feature/campaign, spanning 1..N issues, with an order-to-order dependency DAG.
/// It turns the authored plan into the keys Turnstile needs: an immutable <c>spec</c> per order plus the
/// structure to derive the ready set. Both the one-shot <c>add</c> and the live <c>plan</c> controller
/// build ready state from this model.
/// </summary>
internal sealed class Plan
{
    public string PlanId { get; }

    public IReadOnlyList<Order> Orders { get; }

    private Plan(string planId, IReadOnlyList<Order> orders)
    {
        PlanId = planId;
        Orders = orders;
    }

    public string ReadyKey(Order order) => $"/ready/{PlanId}/{order.Id}";

    public static Plan Parse(string json, string planSha)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string planId = Scalar(root, "plan") ?? throw new InvalidDataException("plan is missing `plan`");
        string? planStandard = root.TryGetProperty("standard", out JsonElement ps) ? ps.GetString() : null;

        var orders = new List<Order>();
        foreach (JsonElement orderElem in Array(root, "orders"))
        {
            string orderId = Scalar(orderElem, "order") ?? throw new InvalidDataException("order is missing `order`");
            string orderBase = $"/plan/{planId}/order/{orderId}";
            string[] after = Array(orderElem, "after")
                .Select(d => d.ValueKind == JsonValueKind.Number ? d.GetRawText() : d.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToArray();
            string specJson = BuildSpec(planId, orderId, planSha, orderElem, planStandard);
            orders.Add(new Order(orderId, orderBase, after, specJson));
        }

        return new Plan(planId, orders);
    }

    private static string BuildSpec(string planId, string orderId, string planSha, JsonElement order, string? planStandard)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("plan", planId);
            w.WriteString("order", orderId);
            if (planSha.Length > 0)
            {
                w.WriteString("order_sha", planSha);
            }

            CopyScalar(w, order, "issue");
            CopyScalar(w, order, "title");

            string? standard = order.TryGetProperty("standard", out JsonElement os) ? os.GetString() : planStandard;
            if (standard is { Length: > 0 })
            {
                w.WriteString("standard", standard);
            }

            CopyStringArray(w, order, "paths");
            CopyStringArray(w, order, "supersedes");
            CopyStringArray(w, order, "after");
            CopyStringArray(w, order, "related");
            CopyStringArray(w, order, "antipatterns");
            CopyScalar(w, order, "brief");
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void CopyScalar(Utf8JsonWriter w, JsonElement parent, string name)
    {
        if (Scalar(parent, name) is { Length: > 0 } s)
        {
            w.WriteString(name, s);
        }
    }

    private static void CopyStringArray(Utf8JsonWriter w, JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0)
        {
            return;
        }

        w.WriteStartArray(name);
        foreach (JsonElement e in arr.EnumerateArray())
        {
            w.WriteStringValue(e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText());
        }

        w.WriteEndArray();
    }

    private static string? Scalar(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement v))
        {
            return null;
        }

        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
    }

    private static IEnumerable<JsonElement> Array(JsonElement parent, string name)
        => parent.TryGetProperty(name, out JsonElement arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray()
            : [];
}
