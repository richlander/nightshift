namespace Nightshift.Commands;

using System.Text;
using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift add &lt;order.json&gt;</c> — project a committed work order into Turnstile: an immutable
/// <c>spec</c> per slice (the authorization root, carrying the order's commit SHA) and an owned
/// <c>/ready/*</c> row for every dispatchable slice. Idempotent and reboot-safe: re-running skips
/// already-seeded specs, skips slices that already have an outcome, and restores any missing ready rows.
/// This is the same operation that bootstraps an order and that re-hydrates it after a reboot.
/// </summary>
internal static class AddCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? path = FirstPositional(args);
        if (path is null)
        {
            Console.Error.WriteLine("usage: nightshift add <order.json> [--sha <commit>]");
            return 2;
        }

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"nightshift add: file not found: {path}");
            return 2;
        }

        string orderSha = Options.Value(args, "--sha") ?? GitHead(Path.GetDirectoryName(Path.GetFullPath(path))!);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using JsonDocument order = JsonDocument.Parse(await File.ReadAllTextAsync(path, ct));
        JsonElement root = order.RootElement;
        string orderId = Scalar(root, "order") ?? throw new InvalidDataException("work order is missing `order`");
        string? orderStandard = root.TryGetProperty("standard", out JsonElement os) ? os.GetString() : null;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Index ops so DAG dependencies can be resolved to their slices and checked for completion.
        var opSlices = new Dictionary<string, (string Padded, List<string> Slices)>();
        foreach (JsonElement op in Array(root, "operations"))
        {
            string opId = Scalar(op, "op") ?? throw new InvalidDataException("operation is missing `op`");
            opSlices[opId] = (Padded(opId), Array(op, "slices").Select(s => Scalar(s, "slice") ?? string.Empty).Where(s => s.Length > 0).ToList());
        }

        int specsCreated = 0, readyWritten = 0, skipped = 0;

        foreach (JsonElement op in Array(root, "operations"))
        {
            string opId = Scalar(op, "op") ?? throw new InvalidDataException("operation is missing `op`");
            string opPadded = Padded(opId);
            string? opStandard = op.TryGetProperty("standard", out JsonElement ops) ? ops.GetString() : orderStandard;

            // A dependency is satisfied only when every one of its slices has landed (state == done).
            bool depsSatisfied = true;
            foreach (JsonElement dep in Array(op, "after"))
            {
                string depId = dep.ValueKind == JsonValueKind.Number ? dep.GetRawText() : dep.GetString() ?? string.Empty;
                if (!opSlices.TryGetValue(depId, out (string Padded, List<string> Slices) depOp) || !await IsOpDoneAsync(client, orderId, depOp, ct))
                {
                    depsSatisfied = false;
                    break;
                }
            }

            foreach (JsonElement slice in Array(op, "slices"))
            {
                string sliceId = Scalar(slice, "slice") ?? throw new InvalidDataException("slice is missing `slice`");
                string sliceBase = $"/order/{orderId}/op/{opPadded}/slice/{sliceId}";

                string specJson = BuildSpec(orderId, opId, sliceId, orderSha, op, slice, opStandard);
                if (await client.CreateImmutableAsync($"{sliceBase}/spec", specJson, ct))
                {
                    specsCreated++;
                }

                // Dispatchable iff its DAG deps have landed and it has no recorded outcome yet.
                bool hasOutcome = await client.GetAsync($"{sliceBase}/state", ct) is not null;
                if (!depsSatisfied || hasOutcome)
                {
                    skipped++;
                    continue;
                }

                await client.SetAsync($"/ready/{orderId}/{opPadded}/{sliceId}", sliceBase, ct);
                readyWritten++;
            }
        }

        Console.WriteLine($"seeded order {orderId} @ {(orderSha.Length > 0 ? orderSha[..Math.Min(orderSha.Length, 12)] : "(no sha)")}: {specsCreated} spec(s) created, {readyWritten} ready, {skipped} not-yet-ready");
        return 0;
    }

    private static string Padded(string opId) => int.TryParse(opId, out int n) ? n.ToString("D4") : opId;

    /// <summary>True when every slice of the op has a terminal <c>done</c> outcome.</summary>
    private static async Task<bool> IsOpDoneAsync(TurnstileClient client, string orderId, (string Padded, List<string> Slices) op, CancellationToken ct)
    {
        if (op.Slices.Count == 0)
        {
            return false;
        }

        foreach (string sliceId in op.Slices)
        {
            KvItem? state = await client.GetAsync($"/order/{orderId}/op/{op.Padded}/slice/{sliceId}/state", ct);
            if (state is null || StatusOf(state.Text) != "done")
            {
                return false;
            }
        }

        return true;
    }

    private static string? StatusOf(string stateJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            return doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

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

    /// <summary>Reads a property as a string regardless of whether it is a JSON string or number.</summary>
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

    private static string GitHead(string dir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    return output;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git unavailable — proceed without a SHA (the caller can pass --sha instead).
        }

        return string.Empty;
    }

    private static string? FirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++;
                continue;
            }

            return args[i];
        }

        return null;
    }
}
