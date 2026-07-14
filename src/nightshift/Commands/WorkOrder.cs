namespace Nightshift.Commands;

using System.Text;
using System.Text.Json;

/// <summary>A slice: the claim unit. Carries its Turnstile key base and the immutable spec to seed.</summary>
internal sealed record OrderSlice(string Id, string Base, string SpecJson);

/// <summary>An operation: a group of slices with DAG dependencies on other ops (by op id).</summary>
internal sealed record OrderOp(string Id, string Padded, string[] After, IReadOnlyList<OrderSlice> Slices);

/// <summary>
/// The in-memory projection of a work order (JSON) — pure, no I/O. Turns the authored DAG into the
/// keys Turnstile needs: an immutable <c>spec</c> per slice and enough structure to derive the ready set.
/// Both the one-shot <c>add</c> and the live <c>plan</c> controller build ready state from this model.
/// </summary>
internal sealed class WorkOrder
{
    public string OrderId { get; }

    public IReadOnlyList<OrderOp> Ops { get; }

    private WorkOrder(string orderId, IReadOnlyList<OrderOp> ops)
    {
        OrderId = orderId;
        Ops = ops;
    }

    public string ReadyKey(OrderOp op, OrderSlice slice) => $"/ready/{OrderId}/{op.Padded}/{slice.Id}";

    public static WorkOrder Parse(string json, string orderSha)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        string orderId = Scalar(root, "order") ?? throw new InvalidDataException("work order is missing `order`");
        string? orderStandard = root.TryGetProperty("standard", out JsonElement os) ? os.GetString() : null;

        var ops = new List<OrderOp>();
        foreach (JsonElement opElem in Array(root, "operations"))
        {
            string opId = Scalar(opElem, "op") ?? throw new InvalidDataException("operation is missing `op`");
            string padded = Padded(opId);
            string[] after = Array(opElem, "after")
                .Select(d => d.ValueKind == JsonValueKind.Number ? d.GetRawText() : d.GetString() ?? string.Empty)
                .Where(s => s.Length > 0)
                .ToArray();
            string? opStandard = opElem.TryGetProperty("standard", out JsonElement ops2) ? ops2.GetString() : orderStandard;

            var slices = new List<OrderSlice>();
            foreach (JsonElement sliceElem in Array(opElem, "slices"))
            {
                string sliceId = Scalar(sliceElem, "slice") ?? throw new InvalidDataException("slice is missing `slice`");
                string sliceBase = $"/order/{orderId}/op/{padded}/slice/{sliceId}";
                string specJson = BuildSpec(orderId, opId, sliceId, orderSha, opElem, sliceElem, opStandard);
                slices.Add(new OrderSlice(sliceId, sliceBase, specJson));
            }

            ops.Add(new OrderOp(opId, padded, after, slices));
        }

        return new WorkOrder(orderId, ops);
    }

    private static string Padded(string opId) => int.TryParse(opId, out int n) ? n.ToString("D4") : opId;

    private static string BuildSpec(string orderId, string opId, string sliceId, string orderSha, JsonElement op, JsonElement slice, string? opStandard)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("order", orderId);
            w.WriteString("op", opId);
            w.WriteString("slice", sliceId);
            if (orderSha.Length > 0)
            {
                w.WriteString("order_sha", orderSha);
            }

            CopyScalar(w, op, "issue");
            CopyScalar(w, op, "title");

            string? standard = slice.TryGetProperty("standard", out JsonElement ss) ? ss.GetString() : opStandard;
            if (standard is { Length: > 0 })
            {
                w.WriteString("standard", standard);
            }

            CopyStringArray(w, slice, "paths");
            CopyStringArray(w, slice, "supersedes");
            CopyStringArray(w, op, "after");
            CopyStringArray(w, slice, "related");
            CopyStringArray(w, slice, "antipatterns");
            CopyScalar(w, slice, "brief");
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
