namespace Nightshift.Commands;

using System.Text;
using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift release --status &lt;s&gt; [--reason ...]</c> — hand the slice back. Records the outcome to the
/// slice's <c>state</c> key, frees the claim (by revoking the agent's lease), and — for terminal outcomes —
/// removes the slice from the ready set so it is not re-dispatched. <c>declined</c> returns it to the pool.
/// </summary>
internal static class ReleaseCommand
{
    private static readonly string[] ValidStatuses = ["done", "blocked", "declined", "escalated", "refused"];

    public static async Task<int> RunAsync(string[] args)
    {
        string? status = Options.Value(args, "--status");
        if (status is null || Array.IndexOf(ValidStatuses, status) < 0)
        {
            Console.Error.WriteLine($"nightshift release: --status must be one of {string.Join('|', ValidStatuses)}");
            return 2;
        }

        string? reason = Options.Value(args, "--reason");

        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift release: no active claim (nothing to release)");
            return 3;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Record the outcome (owned key, blind write) so the shift report and controllers can read it.
        await client.SetAsync($"{session.SliceBase}/state", BuildState(status, reason), ct);

        // Terminal outcomes leave the ready set; `declined` returns the slice to the pool for another agent.
        if (status != "declined")
        {
            await client.DeleteAsync(session.ReadyKey, ct);
        }

        // Revoking the lease deletes the lease-attached claim — the slice is now free of this agent.
        await client.RevokeLeaseAsync(session.LeaseId, ct);
        Session.Clear();

        Console.WriteLine($"RELEASED {status}");
        return 0;
    }

    private static string BuildState(string status, string? reason)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("status", status);
            if (reason is { Length: > 0 })
            {
                writer.WriteString("reason", reason);
            }

            writer.WriteString("by", Session.Identity);
            writer.WriteString("at", DateTime.UtcNow.ToString("o"));
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
