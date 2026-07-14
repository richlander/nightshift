namespace Turnstile;

using System.Diagnostics;
using System.Text;
using Turnstile.Server;
using Turnstile.Storage;

/// <summary>
/// Stage-0 coordination recipes built on <see cref="ITurnstile"/>: a mutex (<c>lock</c>), leader
/// election with standby failover (<c>elect</c>), and a FIFO work queue (<c>queue</c>). Each is a thin
/// composition of lease + txn + watch — the proof that "coordination is a store plus recipes." They
/// auto-connect via <see cref="TurnstileConnection"/>, so they work daemonless (library mode) or against
/// a running daemon, unchanged.
/// </summary>
internal static class Helpers
{
    private const int ExitHeld = 4;
    private const int ExitLostLock = 5;
    private const int ExitEmpty = 4;

    /// <summary>A mutex: acquire <c>key</c>, run the wrapped command while holding it, release on exit.</summary>
    public static Task<int> LockAsync(string[] args) => ExclusiveAsync(args, alwaysWait: false);

    /// <summary>Leader election: stand by until elected, run the wrapped command as leader, fail over on death.</summary>
    public static Task<int> ElectAsync(string[] args) => ExclusiveAsync(args, alwaysWait: true);

    private static async Task<int> ExclusiveAsync(string[] args, bool alwaysWait)
    {
        (string[] head, string[] cmd) = SplitAtDoubleDash(args);
        if (Positional(head) is not string key || !key.StartsWith('/'))
        {
            Console.Error.WriteLine("turnstile: a key beginning with '/' is required");
            return 2;
        }

        long ttl = ParseTtl(Cli.OptionValue(head, "--ttl") ?? "300");
        bool noWait = !alwaysWait && Cli.HasFlag(head, "--no-wait");
        long timeout = long.TryParse(Cli.OptionValue(head, "--timeout"), out long t) ? t : 0;
        string holder = Identity();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        ITurnstile store = await TurnstileConnection.ConnectAsync(Cli.OptionValue(head, "--socket"), ct: ct);
        string leaseId;
        try
        {
            leaseId = (await store.CreateLeaseAsync(ttl, ct)).Id;
        }
        catch (OperationCanceledException)
        {
            store.Dispose();
            return 130;
        }

        try
        {
            bool acquired = await AcquireAsync(store, key, holder, leaseId, noWait, timeout, ct);
            if (!acquired)
            {
                if (ct.IsCancellationRequested)
                {
                    return 130;
                }

                Console.Error.WriteLine($"turnstile: '{key}' is held");
                return ExitHeld;
            }

            return await HoldAndRunAsync(store, leaseId, ttl, cmd, ct);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
        finally
        {
            // Releasing revokes the lease, which tombstones the lock key (a delete event that wakes any
            // standby). Use an uncancellable token so release still runs on Ctrl-C.
            try
            {
                await store.RevokeLeaseAsync(leaseId, CancellationToken.None);
            }
            catch
            {
                // Best-effort release; the sweeper reclaims the lease on TTL expiry regardless.
            }

            store.Dispose();
        }
    }

    // Claim the key with a create-if-absent txn (create_revision == 0). On contention, wait for the
    // holder's key to be deleted (its lease expired or was released), then re-contend.
    private static async Task<bool> AcquireAsync(ITurnstile store, string key, string holder, string leaseId, bool noWait, long timeoutSecs, CancellationToken ct)
    {
        DateTimeOffset deadline = timeoutSecs > 0 ? DateTimeOffset.UtcNow.AddSeconds(timeoutSecs) : DateTimeOffset.MaxValue;

        while (!ct.IsCancellationRequested)
        {
            long since = await store.GetRevisionAsync(ct);
            if (await TryClaimAsync(store, key, holder, leaseId, ct))
            {
                return true;
            }

            if (noWait)
            {
                return false;
            }

            if (!await WaitForReleaseAsync(store, key, since, deadline, ct))
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// Atomically claims <paramref name="key"/> for <paramref name="holder"/> under
    /// <paramref name="leaseId"/> iff it does not already exist (create_revision == 0). Returns false if
    /// another holder got there first — the compare/put is the whole mutex.
    /// </summary>
    internal static async Task<bool> TryClaimAsync(ITurnstile store, string key, string holder, string leaseId, CancellationToken ct = default)
    {
        TxnResult txn = await store.TxnAsync(
            [new TxnCompare(key, TxnTarget.CreateRevision, TxnCompareOp.Equal, 0, null, null)],
            [new TxnOp(TxnOpKind.Put, key, Encoding.UTF8.GetBytes(holder), leaseId, false)],
            [],
            ct);
        return txn.Succeeded;
    }

    // Watch the key from `since` until it is deleted (freed) or the deadline passes.
    private static async Task<bool> WaitForReleaseAsync(ITurnstile store, string key, long since, DateTimeOffset deadline, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (deadline != DateTimeOffset.MaxValue)
        {
            TimeSpan remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            timeoutCts.CancelAfter(remaining);
        }

        try
        {
            await foreach (WatchMessage msg in store.WatchAsync(key, since, timeoutCts.Token))
            {
                if (msg is WatchEventMessage e && e.Event.Key == key && e.Event.Deleted)
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // timed out waiting for release
        }

        return false;
    }

    // Hold the lock (keepalive in the background) while the wrapped command runs. With no command, hold
    // leadership and block until interrupted — the "be the leader" standby mode.
    private static async Task<int> HoldAndRunAsync(ITurnstile store, string leaseId, long ttl, string[] cmd, CancellationToken ct)
    {
        using var lostCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task keepalive = KeepAliveLoopAsync(store, leaseId, ttl, lostCts);

        try
        {
            if (cmd.Length == 0)
            {
                Console.Error.WriteLine("turnstile: acquired; holding until interrupted");
                try
                {
                    await Task.Delay(Timeout.Infinite, lostCts.Token);
                }
                catch (OperationCanceledException)
                {
                }

                return lostCts.IsCancellationRequested && !ct.IsCancellationRequested ? ExitLostLock : 0;
            }

            return await RunChildAsync(cmd, lostCts.Token, ct);
        }
        finally
        {
            lostCts.Cancel();
            try
            {
                await keepalive;
            }
            catch
            {
                // The keepalive loop ending is expected on release.
            }
        }
    }

    // Refresh the lease every ttl/3 seconds. If a keepalive loses the race with the sweeper (the lease is
    // gone), we have lost the lock: cancel so the child is stopped rather than running unprotected.
    private static async Task KeepAliveLoopAsync(ITurnstile store, string leaseId, long ttl, CancellationTokenSource lostCts)
    {
        TimeSpan interval = TimeSpan.FromSeconds(Math.Max(ttl / 3.0, 1));
        try
        {
            while (!lostCts.IsCancellationRequested)
            {
                await Task.Delay(interval, lostCts.Token);
                if (await store.KeepAliveAsync(leaseId, lostCts.Token) is null)
                {
                    Console.Error.WriteLine("turnstile: lost lock (lease expired); stopping");
                    lostCts.Cancel();
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<int> RunChildAsync(string[] cmd, CancellationToken lostOrCt, CancellationToken userCt)
    {
        var psi = new ProcessStartInfo(cmd[0]) { UseShellExecute = false };
        for (int i = 1; i < cmd.Length; i++)
        {
            psi.ArgumentList.Add(cmd[i]);
        }

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"turnstile: cannot run '{cmd[0]}': {ex.Message}");
            return 127;
        }

        try
        {
            await process.WaitForExitAsync(lostOrCt);
            return process.ExitCode;
        }
        catch (OperationCanceledException)
        {
            // Interrupted or lost the lock: stop the child so nothing runs unprotected.
            TryKill(process);
            return userCt.IsCancellationRequested ? 130 : ExitLostLock;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The process may have exited between the check and the kill.
        }
    }

    /// <summary>A FIFO work queue: <c>queue push &lt;q&gt;</c> enqueues; <c>queue pop &lt;q&gt;</c> claims the oldest item exactly once.</summary>
    public static async Task<int> QueueAsync(string[] args)
    {
        string? sub = Positional(args);
        string? queue = PositionalAt(args, 1);
        if (sub is not ("push" or "pop") || queue is null || !queue.StartsWith('/'))
        {
            Console.Error.WriteLine("turnstile: usage: turnstile queue <push|pop> /queue [--value X] [--wait]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using ITurnstile store = await TurnstileConnection.ConnectAsync(Cli.OptionValue(args, "--socket"), ct: ct);
        try
        {
            return sub == "push"
                ? await QueuePushAsync(store, queue, args, ct)
                : await QueuePopAsync(store, queue, Cli.HasFlag(args, "--wait"), ct);
        }
        catch (OperationCanceledException)
        {
            return 130;
        }
    }

    // Enqueue: bump a per-queue sequence counter and write the item under it, atomically, so lexical key
    // order is FIFO order. Retry on counter contention.
    private static async Task<int> QueuePushAsync(ITurnstile store, string queue, string[] args, CancellationToken ct)
    {
        byte[] value = await ReadValueAsync(args, ct);
        string itemKey = await EnqueueAsync(store, queue, value, ct);
        Console.WriteLine(itemKey);
        return 0;
    }

    /// <summary>
    /// Appends <paramref name="value"/> to the queue and returns its item key. A per-queue counter is
    /// bumped and the item written in one txn, so lexical key order equals enqueue order (FIFO). Retries
    /// until the counter compare wins.
    /// </summary>
    internal static async Task<string> EnqueueAsync(ITurnstile store, string queue, byte[] value, CancellationToken ct = default)
    {
        string counter = $"{queue}/seq";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            KeyState? seq = await store.GetAsync(counter, ct);
            long next = 1 + (seq?.Value is byte[] b && long.TryParse(Encoding.UTF8.GetString(b), out long cur) ? cur : 0);
            string itemKey = $"{queue}/item/{next:D20}";

            TxnCompare compare = seq is null
                ? new TxnCompare(counter, TxnTarget.CreateRevision, TxnCompareOp.Equal, 0, null, null)
                : new TxnCompare(counter, TxnTarget.ModRevision, TxnCompareOp.Equal, seq.ModRevision, null, null);

            TxnResult txn = await store.TxnAsync(
                [compare],
                [
                    new TxnOp(TxnOpKind.Put, counter, Encoding.UTF8.GetBytes(next.ToString()), null, false),
                    new TxnOp(TxnOpKind.Put, itemKey, value, null, false),
                ],
                [],
                ct);

            if (txn.Succeeded)
            {
                return itemKey;
            }
        }
    }

    // Dequeue loop: pop the oldest item exactly once; when the queue is empty, either return or (with
    // --wait) block on a watch until something is pushed, then retry.
    private static async Task<int> QueuePopAsync(ITurnstile store, string queue, bool wait, CancellationToken ct)
    {
        string items = $"{queue}/item/";

        while (!ct.IsCancellationRequested)
        {
            long since = await store.GetRevisionAsync(ct);
            if (await TryDequeueAsync(store, queue, ct) is byte[] value)
            {
                using Stream stdout = Console.OpenStandardOutput();
                await stdout.WriteAsync(value, ct);
                return 0;
            }

            if (!wait)
            {
                return ExitEmpty;
            }

            await WaitForPushAsync(store, items, since, ct);
        }

        return 130;
    }

    /// <summary>
    /// Claims and removes the oldest item, returning its value, or null if the queue is empty. Scans
    /// oldest-first and atomically deletes the first item still present (delete-under-compare), so exactly
    /// one popper wins any given item; contended candidates are skipped and the scan repeats.
    /// </summary>
    internal static async Task<byte[]?> TryDequeueAsync(ITurnstile store, string queue, CancellationToken ct = default)
    {
        string items = $"{queue}/item/";

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            IReadOnlyList<KeyState> batch = await store.RangeAsync(items, limit: 64, keysOnly: false, ct);
            if (batch.Count == 0)
            {
                return null;
            }

            foreach (KeyState item in batch)
            {
                TxnResult txn = await store.TxnAsync(
                    [new TxnCompare(item.Key, TxnTarget.ModRevision, TxnCompareOp.Equal, item.ModRevision, null, null)],
                    [new TxnOp(TxnOpKind.Delete, item.Key, null, null, false)],
                    [],
                    ct);

                if (txn.Succeeded)
                {
                    return item.Value ?? [];
                }
            }
        }
    }

    private static async Task WaitForPushAsync(ITurnstile store, string items, long since, CancellationToken ct)
    {
        await foreach (WatchMessage msg in store.WatchAsync(items, since, ct))
        {
            if (msg is WatchEventMessage e && !e.Event.Deleted)
            {
                return;
            }
        }
    }

    private static async Task<byte[]> ReadValueAsync(string[] args, CancellationToken ct)
    {
        if (Cli.OptionValue(args, "--value") is string value)
        {
            return Encoding.UTF8.GetBytes(value);
        }

        using Stream stdin = Console.OpenStandardInput();
        using var ms = new MemoryStream();
        await stdin.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static string Identity() => $"{Environment.MachineName}/{Environment.ProcessId}";

    // Everything before "--" is options; everything after is the wrapped command.
    private static (string[] Head, string[] Command) SplitAtDoubleDash(string[] args)
    {
        int i = Array.IndexOf(args, "--");
        return i < 0 ? (args, []) : (args[..i], args[(i + 1)..]);
    }

    // Parse a TTL like "30", "45s", "30m", "2h" into seconds.
    private static long ParseTtl(string ttl)
    {
        if (ttl.Length == 0)
        {
            return 300;
        }

        char suffix = ttl[^1];
        long multiplier = suffix switch { 's' => 1, 'm' => 60, 'h' => 3600, _ => 1 };
        string number = char.IsDigit(suffix) ? ttl : ttl[..^1];
        return long.TryParse(number, out long n) && n > 0 ? n * multiplier : 300;
    }

    private static string? Positional(string[] args) => PositionalAt(args, 0);

    private static string? PositionalAt(string[] args, int index)
    {
        int seen = 0;
        foreach (string arg in args)
        {
            if (arg == "--")
            {
                break;
            }

            if (arg.StartsWith('-'))
            {
                continue;
            }

            if (seen++ == index)
            {
                return arg;
            }
        }

        return null;
    }
}
