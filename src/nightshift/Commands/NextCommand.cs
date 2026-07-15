namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift next [scope]</c> — the Uber-driver gesture: request one order of work. Scans the ready set
/// under the scope's prefix, CAS-claims the first unclaimed order under this agent's lease, and prints its
/// brief. Blocks (watching for change) until an order is claimable or the timeout expires.
/// </summary>
internal static class NextCommand
{
    // Order lifecycle is 45 minutes of lease; a quiet build survives without a check.
    private const long LeaseTtlSecs = 45 * 60;

    // The ready set is owned rows written by ns-plan; each value is the order base path.
    private const string ReadyRoot = "/ready/";

    // A single durable flag flips the whole shift into drain: dispatch stops, running agents finish.
    private const string DrainingKey = "/control/draining";

    public static async Task<int> RunAsync(string? scope, int timeoutSecs)
    {
        string readyPrefix = scope is null ? ReadyRoot : $"{ReadyRoot}{scope}/";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        if (await client.GetAsync(DrainingKey, ct) is not null)
        {
            Console.WriteLine("DRAINING");
            return ExitCode.Draining;
        }

        // The client owns the lease; the agent never sees it. Reuse an existing session lease if one is live.
        string leaseId = await EnsureLeaseAsync(client, ct);

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSecs);
        long fromRevision = await client.CurrentRevisionAsync(ct);

        try
        {
            while (true)
            {
                if (await TryClaimOneAsync(client, readyPrefix, leaseId, ct) is { } packet)
                {
                    Session.Save(new SessionState(leaseId, packet.Fence, packet.ClaimKey, packet.OrderBase, packet.ReadyKey));

                    // Mint the branch↔order association at claim time: the branch is the durable recovery
                    // anchor and the future merge→land bridge's join key. Recorded before any work starts.
                    if (OrderRef.FromBase(packet.OrderBase) is { } order)
                    {
                        await client.SetAsync($"{packet.OrderBase}/branch", order.Branch, ct);
                    }

                    packet.Spec.PrintWork(Console.Out, packet.OrderBase, packet.Fence);
                    return ExitCode.Ok;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    Console.WriteLine("NOWORK");
                    return ExitCode.NoWork;
                }

                // Block on change rather than spin: a new ready row or a freed claim wakes us to re-scan.
                fromRevision = await WaitForChangeAsync(client, fromRevision, remaining, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("INTERRUPTED");
            return ExitCode.Interrupted;
        }
    }

    private static async Task<WorkPacket?> TryClaimOneAsync(TurnstileClient client, string readyPrefix, string leaseId, CancellationToken ct)
    {
        // Ready rows are returned in key order — that order is the scheduling priority.
        foreach (KvItem ready in await client.RangeAsync(readyPrefix, ct))
        {
            string orderBase = ready.Text.Trim();
            if (orderBase.Length == 0)
            {
                continue;
            }

            string claimKey = $"{orderBase}/claim";
            if (await client.GetAsync(claimKey, ct) is not null)
            {
                continue; // already claimed
            }

            ClaimResult claim = await client.TryClaimAsync(claimKey, leaseId, Session.Identity, ct);
            if (!claim.Won)
            {
                continue; // lost the race to a peer
            }

            KvItem? spec = await client.GetAsync($"{orderBase}/spec", ct);
            OrderView parsed = spec is null ? OrderView.Empty : OrderView.Parse(spec.Text);
            return new WorkPacket(orderBase, claimKey, ready.Key, claim.Revision, parsed);
        }

        return null;
    }

    private static async Task<string> EnsureLeaseAsync(TurnstileClient client, CancellationToken ct)
    {
        SessionState? existing = Session.Load();
        if (existing is not null && await client.KeepAliveAsync(existing.LeaseId, ct))
        {
            return existing.LeaseId;
        }

        return await client.CreateLeaseAsync(LeaseTtlSecs, ct);
    }

    /// <summary>Waits for any change at/after <paramref name="fromRevision"/> or the deadline; returns the new revision floor.</summary>
    private static async Task<long> WaitForChangeAsync(TurnstileClient client, long fromRevision, TimeSpan budget, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            await foreach (WatchSignal signal in client.WatchAsync("/", fromRevision, timeout.Token))
            {
                return signal.Revision; // one change is enough to trigger a re-scan
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // budget elapsed — caller re-checks the deadline and reports NOWORK
        }

        return fromRevision;
    }

    private sealed record WorkPacket(string OrderBase, string ClaimKey, string ReadyKey, long Fence, OrderView Spec);
}
