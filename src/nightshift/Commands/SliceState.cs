namespace Nightshift.Commands;

using System.Text;
using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>Writes a slice's outcome to its owned <c>{base}/state</c> key as small JSON.</summary>
internal static class SliceState
{
    public static Task WriteAsync(TurnstileClient client, string sliceBase, string status, string? reason, string by, CancellationToken ct)
        => client.SetAsync($"{sliceBase}/state", Build(status, reason, by), ct);

    private static string Build(string status, string? reason, string by)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("status", status);
            if (reason is { Length: > 0 })
            {
                w.WriteString("reason", reason);
            }

            w.WriteString("by", by);
            w.WriteString("at", DateTime.UtcNow.ToString("o"));
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
