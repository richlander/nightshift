namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift check</c> — the heartbeat and directive read. Renews the lease (the forcing function that
/// keeps the claim alive), verifies the claim still belongs to this agent at its fence, and surfaces any
/// standing directive. Responses: OK | HALT | FENCE_STALE | QUERY.
/// </summary>
internal static class CheckCommand
{
    // A global stop flag any operator can set; every checking agent sees it and halts.
    private const string HaltKey = "/control/halt";

    public static async Task<int> RunAsync(string[] args)
    {
        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift check: no active claim (run `nightshift next` first)");
            return 3;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Renew first: if the lease is gone the claim has already been swept — the agent must stop.
        bool alive = await client.KeepAliveAsync(session.LeaseId, ct);
        if (!alive)
        {
            Session.Clear();
            Console.WriteLine("FENCE_STALE");
            return 0;
        }

        // The claim is lease-attached, but verify it is still ours at the fence we were issued.
        KvItem? claim = await client.GetAsync(session.ClaimKey, ct);
        if (claim is null || claim.ModRevision != session.Fence || claim.Text.Trim() != Session.Identity)
        {
            Session.Clear();
            Console.WriteLine("FENCE_STALE");
            return 0;
        }

        if (await client.GetAsync(HaltKey, ct) is not null)
        {
            Console.WriteLine("HALT");
            return 0;
        }

        KvItem? directive = await client.GetAsync($"{session.OrderBase}/directive", ct);
        if (directive is not null && directive.Text.Trim() is { Length: > 0 } text)
        {
            Console.WriteLine("QUERY");
            Console.WriteLine(text);
            return 0;
        }

        Console.WriteLine("OK");
        return 0;
    }
}
