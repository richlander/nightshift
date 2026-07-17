namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift next [scope]</c> — the Uber-driver gesture: request one order of work. Scans the ready set
/// under the scope's prefix, CAS-claims the first unclaimed order under this agent's lease, and prints its
/// brief. By default it blocks (watching for change) until a claim or a terminal control signal
/// (<c>DRAINING</c>/<c>HALT</c>); <c>--once</c> does a single non-blocking scan and returns
/// <c>NOWORK</c> when nothing is claimable; <c>--timeout N</c> bounds the wait and returns <c>NOWORK</c> on
/// expiry.
/// </summary>
internal static class NextCommand
{
    // Order lifecycle is 45 minutes of lease; a quiet build survives without a check.
    private const long LeaseTtlSecs = 45 * 60;
    internal static TimeSpan KeepAliveCadence { get; set; } = TimeSpan.FromMinutes(5);

    // The ready set is owned rows written by ns-plan; each value is the order base path.
    private const string ReadyRoot = "/ready/";
    private const string ControlRoot = "/control/";

    // A single durable flag flips the whole shift into drain: dispatch stops, running agents finish.
    private const string DrainingKey = "/control/draining";
    private const string HaltKey = "/control/halt";

    public static async Task<int> RunAsync(string? scope, int? timeoutSecs, bool once)
    {
        if (timeoutSecs is <= 0)
        {
            Console.Error.WriteLine("nightshift next: --timeout must be a positive integer");
            return ExitCode.Usage;
        }

        string readyPrefix = scope is null ? ReadyRoot : $"{ReadyRoot}{scope}/";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // The client owns the lease; the agent never sees it. Reuse an existing session lease if one is live.
        string leaseId = await EnsureLeaseAsync(client, ct);

        DateTime? deadline = timeoutSecs is { } secs ? DateTime.UtcNow.AddSeconds(secs) : null;
        DateTime nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);
        long fromRevision = await client.CurrentRevisionAsync(ct);

        try
        {
            while (true)
            {
                switch (await ReadTerminalSignalAsync(client, ct))
                {
                    case TerminalSignal.Halt:
                        Console.WriteLine("HALT");
                        return ExitCode.Halt;
                    case TerminalSignal.Draining:
                        Console.WriteLine("DRAINING");
                        return ExitCode.Draining;
                }

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

                if (once)
                {
                    Console.WriteLine("NOWORK");
                    return ExitCode.NoWork;
                }

                DateTime now = DateTime.UtcNow;
                if (deadline is { } d && now >= d)
                {
                    Console.WriteLine("NOWORK");
                    return ExitCode.NoWork;
                }

                TimeSpan budget = ComputeWaitBudget(now, deadline, nextKeepAliveAt);
                WaitResult wait = await WaitForChangeAsync(client, readyPrefix, fromRevision, budget, ct);
                fromRevision = wait.Revision;

                if (wait.Source != WakeSource.Timeout || DateTime.UtcNow >= nextKeepAliveAt)
                {
                    leaseId = await KeepClaimLeaseAliveAsync(client, leaseId, ct);
                    await KeepPresenceAliveAsync(client, ct);
                    nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("INTERRUPTED");
            return ExitCode.Interrupted;
        }
    }

    internal static TimeSpan ComputeWaitBudget(DateTime now, DateTime? deadline, DateTime keepAliveAt)
    {
        DateTime wakeAt = deadline is { } d && d < keepAliveAt ? d : keepAliveAt;
        TimeSpan budget = wakeAt - now;
        return budget > TimeSpan.Zero ? budget : TimeSpan.Zero;
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

            OrderView parsed = await OrderView.LoadAsync(client, orderBase, ct);
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

    private static async Task<string> KeepClaimLeaseAliveAsync(TurnstileClient client, string leaseId, CancellationToken ct)
        => await client.KeepAliveAsync(leaseId, ct) ? leaseId : await client.CreateLeaseAsync(LeaseTtlSecs, ct);

    private static async Task KeepPresenceAliveAsync(TurnstileClient client, CancellationToken ct)
    {
        PresenceState? presence = Presence.Load();
        if (presence is not null)
        {
            await client.KeepAliveAsync(presence.LeaseId, ct);
        }
    }

    private static async Task<TerminalSignal> ReadTerminalSignalAsync(TurnstileClient client, CancellationToken ct)
    {
        if (await client.GetAsync(HaltKey, ct) is not null)
        {
            return TerminalSignal.Halt;
        }

        if (await client.GetAsync(DrainingKey, ct) is not null)
        {
            return TerminalSignal.Draining;
        }

        return TerminalSignal.None;
    }

    /// <summary>
    /// Waits for either ready-set or control-plane change at/after <paramref name="fromRevision"/>, or for
    /// the budget to elapse; returns the new revision floor and wake reason.
    /// </summary>
    private static async Task<WaitResult> WaitForChangeAsync(TurnstileClient client, string readyPrefix, long fromRevision, TimeSpan budget, CancellationToken ct)
        => await WaitForScopedChangeCoreAsync(
            (from, token) => client.WatchAsync(readyPrefix, from, token),
            (from, token) => client.WatchAsync(ControlRoot, from, token),
            client.CurrentRevisionAsync,
            fromRevision,
            budget,
            ct);

    internal static async Task<WaitResult> WaitForScopedChangeCoreAsync(
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watchReady,
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watchControl,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        TimeSpan budget,
        CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task<WatchOutcome> readyTask = WaitForPrefixChangeCoreAsync(watchReady, currentRevision, fromRevision, linked.Token);
        Task<WatchOutcome> controlTask = WaitForPrefixChangeCoreAsync(watchControl, currentRevision, fromRevision, linked.Token);
        Task timeoutTask = budget == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
            : Task.Delay(budget, linked.Token);

        Task winner = await Task.WhenAny(readyTask, controlTask, timeoutTask);
        if (winner == timeoutTask)
        {
            linked.Cancel();
            await ObserveCanceledWatchersAsync(readyTask, controlTask, ct);
            return new WaitResult(fromRevision, WakeSource.Timeout);
        }

        WatchOutcome outcome = await (winner == readyTask ? readyTask : controlTask);
        linked.Cancel();
        await ObserveCanceledWatchersAsync(readyTask, controlTask, ct);
        return new WaitResult(outcome.Revision, winner == readyTask ? WakeSource.Ready : WakeSource.Control);
    }

    private static async Task ObserveCanceledWatchersAsync(Task<WatchOutcome> readyTask, Task<WatchOutcome> controlTask, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(readyTask, controlTask);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The losing watcher is canceled after one wake source wins — expected.
        }
    }

    private static async Task<WatchOutcome> WaitForPrefixChangeCoreAsync(
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watch,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        CancellationToken ct)
    {
        try
        {
            await foreach (WatchSignal signal in watch(fromRevision, ct))
            {
                return new WatchOutcome(signal.Revision);
            }
        }
        catch (WatchCompactedException)
        {
            return new WatchOutcome(await currentRevision(ct));
        }

        return new WatchOutcome(fromRevision);
    }

    internal static async Task<long> WaitForChangeCoreAsync(
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watch,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        TimeSpan budget,
        CancellationToken ct)
    {
        if (budget == Timeout.InfiniteTimeSpan)
        {
            return (await WaitForPrefixChangeCoreAsync(watch, currentRevision, fromRevision, ct)).Revision;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(budget);
        try
        {
            return (await WaitForPrefixChangeCoreAsync(watch, currentRevision, fromRevision, timeout.Token)).Revision;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return fromRevision;
        }
    }

    private sealed record WorkPacket(string OrderBase, string ClaimKey, string ReadyKey, long Fence, OrderView Spec);

    private sealed record WatchOutcome(long Revision);

    internal sealed record WaitResult(long Revision, WakeSource Source);

    internal enum WakeSource
    {
        Timeout,
        Ready,
        Control,
    }

    private enum TerminalSignal
    {
        None,
        Draining,
        Halt,
    }
}
