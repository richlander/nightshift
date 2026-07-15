namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift leave</c> — clock out. Returns any in-flight order to the pool (revoking the claim lease
/// frees the claim; with no terminal state written, the reconciler re-offers it) and drops the roster
/// entry. Idempotent: leaving with nothing in hand is a clean no-op.
/// </summary>
internal static class LeaveCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // A held order goes back to the pool: revoking the claim lease deletes the lease-attached claim,
        // and no state is written, so the order is eligible again on the next reconcile.
        SessionState? session = Session.Load();
        if (session is not null)
        {
            await client.RevokeLeaseAsync(session.LeaseId, ct);
            Session.Clear();
        }

        // Drop the roster entry by revoking its shift lease.
        PresenceState? presence = Presence.Load();
        if (presence is not null)
        {
            await client.RevokeLeaseAsync(presence.LeaseId, ct);
            Presence.Clear();
        }

        Console.WriteLine("LEFT");
        return ExitCode.Ok;
    }
}
