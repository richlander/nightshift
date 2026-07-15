namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift release --status &lt;s&gt; [--reason ...]</c> — hand the order back. Records the outcome to the
/// order's <c>state</c> key, frees the claim (by revoking the agent's lease), and — for terminal outcomes —
/// removes the order from the ready set so it is not re-dispatched. <c>declined</c> returns it to the pool.
/// </summary>
internal static class ReleaseCommand
{
    private static readonly string[] ValidStatuses = ["done", "blocked", "declined", "escalated", "refused"];

    public static async Task<int> RunAsync(string? status, string? reason)
    {
        if (status is null || Array.IndexOf(ValidStatuses, status) < 0)
        {
            Console.Error.WriteLine($"nightshift release: --status must be one of {string.Join('|', ValidStatuses)}");
            return ExitCode.Usage;
        }

        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift release: no active claim (nothing to release)");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Record the agent's outcome. Note `done` here means "submitted, awaiting merge" — it does NOT
        // open dependents. Only `land` (the merge signal) advances the DAG. See LandCommand.
        await OrderState.WriteAsync(client, session.OrderBase, status, reason, Session.Identity, ct);

        // The live `plan` controller owns /ready/* and will reconcile it. We proactively drop this order's
        // ready row (except for `declined`, which returns to the pool) to close the re-dispatch gap before
        // the controller wakes — a blind delete, harmless if the controller already handled it.
        if (status != "declined")
        {
            await client.DeleteAsync(session.ReadyKey, ct);
        }

        // Revoking the lease deletes the lease-attached claim — the slice is now free of this agent.
        await client.RevokeLeaseAsync(session.LeaseId, ct);
        Session.Clear();

        Console.WriteLine($"RELEASED {status}");
        return ExitCode.Ok;
    }
}
