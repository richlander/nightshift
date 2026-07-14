namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift next [scope]</c> — the Uber-driver gesture: request one slice of work. Scans the ready set
/// under the scope's prefix, CAS-claims the first unclaimed slice under this agent's lease, and prints its
/// brief. Blocks (watching for change) until a slice is claimable or the timeout expires.
/// </summary>
internal static class NextCommand
{
    // Slice lifecycle is 45 minutes of lease; a quiet build survives without a check.
    private const long LeaseTtlSecs = 45 * 60;

    // The ready set is owned rows written by ns-plan; each value is the slice base path.
    private const string ReadyRoot = "/ready/";

    // A single durable flag flips the whole shift into drain: dispatch stops, running agents finish.
    private const string DrainingKey = "/control/draining";

    public static async Task<int> RunAsync(string[] args)
    {
        string? scope = FirstPositional(args);
        int timeoutSecs = ParseInt(Options.Value(args, "--timeout"), 60);

        string readyPrefix = scope is null ? ReadyRoot : $"{ReadyRoot}{scope}/";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        if (await client.GetAsync(DrainingKey, ct) is not null)
        {
            Console.WriteLine("DRAINING");
            return 0;
        }

        // The client owns the lease; the agent never sees it. Reuse an existing session lease if one is live.
        string leaseId = await EnsureLeaseAsync(client, ct);

        DateTime deadline = DateTime.UtcNow.AddSeconds(timeoutSecs);
        long fromRevision = await client.CurrentRevisionAsync(ct);

        while (true)
        {
            if (await TryClaimOneAsync(client, readyPrefix, leaseId, ct) is { } packet)
            {
                Session.Save(new SessionState(leaseId, packet.Fence, packet.ClaimKey, packet.SliceBase, packet.ReadyKey));
                Print(packet);
                return 0;
            }

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                Console.WriteLine("NOWORK");
                return 0;
            }

            // Block on change rather than spin: a new ready row or a freed claim wakes us to re-scan.
            fromRevision = await WaitForChangeAsync(client, fromRevision, remaining, ct);
        }
    }

    private static async Task<WorkPacket?> TryClaimOneAsync(TurnstileClient client, string readyPrefix, string leaseId, CancellationToken ct)
    {
        // Ready rows are returned in key order — that order is the scheduling priority.
        foreach (KvItem ready in await client.RangeAsync(readyPrefix, ct))
        {
            string sliceBase = ready.Text.Trim();
            if (sliceBase.Length == 0)
            {
                continue;
            }

            string claimKey = $"{sliceBase}/claim";
            if (await client.GetAsync(claimKey, ct) is not null)
            {
                continue; // already claimed
            }

            ClaimResult claim = await client.TryClaimAsync(claimKey, leaseId, Session.Identity, ct);
            if (!claim.Won)
            {
                continue; // lost the race to a peer
            }

            KvItem? spec = await client.GetAsync($"{sliceBase}/spec", ct);
            SliceSpec parsed = spec is null ? SliceSpec.Empty : SliceSpec.Parse(spec.Text);
            return new WorkPacket(sliceBase, claimKey, ready.Key, claim.Revision, parsed);
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

    private static void Print(WorkPacket packet)
    {
        Console.WriteLine($"WORK {packet.SliceBase}");
        if (packet.Spec.Paths.Length > 0)
        {
            Console.WriteLine($"paths: {string.Join(", ", packet.Spec.Paths)}");
        }

        if (packet.Spec.Standard is { Length: > 0 } standard)
        {
            Console.WriteLine($"standard: {standard}");
        }

        if (packet.Spec.Brief is { Length: > 0 } brief)
        {
            Console.WriteLine($"brief: {brief}");
        }

        Console.WriteLine($"fence: {packet.Fence}");
    }

    private static string? FirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++; // skip an option's value
                continue;
            }

            return args[i];
        }

        return null;
    }

    private static int ParseInt(string? value, int fallback) => int.TryParse(value, out int v) ? v : fallback;

    private sealed record WorkPacket(string SliceBase, string ClaimKey, string ReadyKey, long Fence, SliceSpec Spec);

    private sealed record SliceSpec(string[] Paths, string? Standard, string? Brief)
    {
        public static SliceSpec Empty { get; } = new([], null, null);

        public static SliceSpec Parse(string json)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                string[] paths = root.TryGetProperty("paths", out JsonElement p) && p.ValueKind == JsonValueKind.Array
                    ? p.EnumerateArray().Select(e => e.GetString() ?? string.Empty).Where(s => s.Length > 0).ToArray()
                    : [];
                string? standard = root.TryGetProperty("standard", out JsonElement s) ? s.GetString() : null;
                string? brief = root.TryGetProperty("brief", out JsonElement b) ? b.GetString() : null;
                return new SliceSpec(paths, standard, brief);
            }
            catch (JsonException)
            {
                return Empty;
            }
        }
    }
}

/// <summary>Minimal option reader shared by Nightshift commands.</summary>
internal static class Options
{
    public static string? Value(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
