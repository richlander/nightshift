namespace Turnstile.Tests;

using System.Collections.Concurrent;
using System.Text;
using Turnstile;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Stage-0 recipe tests: the lock/elect/queue helpers exercised against a real <see cref="LocalStore"/>
/// (library mode), proving mutual exclusion, sweep-on-open reclaim, single-leader election, and FIFO
/// exactly-once dequeue directly on the code paths the CLI runs.
/// </summary>
public class HelperTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-helper-{Guid.NewGuid():N}.db");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<LocalStore> Open() => LocalStore.OpenAsync(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string Text(byte[]? b) => b is null ? string.Empty : Encoding.UTF8.GetString(b);

    [Fact]
    public async Task Lock_IsMutuallyExclusive()
    {
        using LocalStore store = await Open();
        string leaseA = (await store.CreateLeaseAsync(60, Ct)).Id;
        string leaseB = (await store.CreateLeaseAsync(60, Ct)).Id;

        Assert.True(await Helpers.TryClaimAsync(store, "/lock/x", "a", leaseA, Ct));
        Assert.False(await Helpers.TryClaimAsync(store, "/lock/x", "b", leaseB, Ct));

        // Releasing the holder (revoke lease -> tombstone key) frees the lock for the next contender.
        await store.RevokeLeaseAsync(leaseA, Ct);
        Assert.True(await Helpers.TryClaimAsync(store, "/lock/x", "b", leaseB, Ct));
    }

    [Fact]
    public async Task Lock_SweepOnOpen_ReclaimsDeadHolder()
    {
        string leaseId;
        using (LocalStore first = await Open())
        {
            leaseId = (await first.CreateLeaseAsync(ttlSecs: 1, Ct)).Id;
            Assert.True(await Helpers.TryClaimAsync(first, "/lock/y", "dead", leaseId, Ct));
        }

        // The holder "dies" (store closed) without releasing. Past the TTL, the next open sweeps the
        // expired lease, tombstoning the leaked lock so a fresh contender can take it.
        await Task.Delay(1200, Ct);

        using LocalStore second = await Open();
        Assert.Null(await second.GetAsync("/lock/y", Ct));
        string fresh = (await second.CreateLeaseAsync(60, Ct)).Id;
        Assert.True(await Helpers.TryClaimAsync(second, "/lock/y", "alive", fresh, Ct));
    }

    [Fact]
    public async Task Elect_ExactlyOneLeaderThenFailover()
    {
        using LocalStore store = await Open();
        string[] leases =
        [
            (await store.CreateLeaseAsync(60, Ct)).Id,
            (await store.CreateLeaseAsync(60, Ct)).Id,
            (await store.CreateLeaseAsync(60, Ct)).Id,
        ];

        bool[] won = await Task.WhenAll(leases.Select((lease, i) =>
            Helpers.TryClaimAsync(store, "/leader/merge", $"c{i}", lease, Ct)));
        Assert.Equal(1, won.Count(w => w));

        int leader = Array.IndexOf(won, true);
        await store.RevokeLeaseAsync(leases[leader], Ct);

        // With the leader gone, exactly one of the remaining standbys is promoted.
        int promoted = 0;
        for (int i = 0; i < leases.Length; i++)
        {
            if (i != leader && await Helpers.TryClaimAsync(store, "/leader/merge", $"c{i}", leases[i], Ct))
            {
                promoted++;
            }
        }

        Assert.Equal(1, promoted);
    }

    [Fact]
    public async Task Queue_IsFifo_AndDrainsToEmpty()
    {
        using LocalStore store = await Open();
        foreach (string v in new[] { "a", "b", "c" })
        {
            await Helpers.EnqueueAsync(store, "/queue/build", Bytes(v), Ct);
        }

        Assert.Equal("a", Text(await Helpers.TryDequeueAsync(store, "/queue/build", Ct)));
        Assert.Equal("b", Text(await Helpers.TryDequeueAsync(store, "/queue/build", Ct)));
        Assert.Equal("c", Text(await Helpers.TryDequeueAsync(store, "/queue/build", Ct)));
        Assert.Null(await Helpers.TryDequeueAsync(store, "/queue/build", Ct));
    }

    [Fact]
    public async Task Queue_ConcurrentPop_IsExactlyOnce()
    {
        using LocalStore store = await Open();
        const int count = 20;
        for (int i = 0; i < count; i++)
        {
            await Helpers.EnqueueAsync(store, "/queue/work", Bytes($"job-{i:D2}"), Ct);
        }

        var popped = new ConcurrentBag<string>();
        await Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Run(async () =>
        {
            if (await Helpers.TryDequeueAsync(store, "/queue/work", Ct) is byte[] value)
            {
                popped.Add(Text(value));
            }
        })));

        Assert.Equal(count, popped.Count);
        Assert.Equal(count, popped.Distinct().Count());
        Assert.Null(await Helpers.TryDequeueAsync(store, "/queue/work", Ct));
    }

    public void Dispose()
    {
        foreach (string path in Directory.GetFiles(Path.GetDirectoryName(_dbPath)!, Path.GetFileName(_dbPath) + "*"))
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Best-effort cleanup of the temp db and its WAL/SHM sidecars.
            }
        }
    }
}
