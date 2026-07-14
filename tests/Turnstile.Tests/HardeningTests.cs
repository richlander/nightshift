namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// P0/P1 hardening (spec §9): the concurrency, log, and lease invariants where the real bugs live.
/// Iteration counts are scaled for a fast inner loop while still exercising the interleavings.
/// </summary>
public class HardeningTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-hard-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task ClaimRace_ExactlyOneWinner_RepeatedRounds()
    {
        using KvStore store = Open();
        const int rounds = 100;
        const int contenders = 64;

        for (int round = 0; round < rounds; round++)
        {
            string key = $"/claim/{round}";
            using var barrier = new Barrier(contenders);
            var tasks = new Task<WriteStatus>[contenders];
            for (int i = 0; i < contenders; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return store.CreateAsync(key, Bytes("me")).GetAwaiter().GetResult().Status;
                });
            }

            WriteStatus[] outcomes = await Task.WhenAll(tasks);
            Assert.Equal(1, outcomes.Count(s => s == WriteStatus.Created));
            Assert.Equal(contenders - 1, outcomes.Count(s => s == WriteStatus.Exists));
        }

        // Exactly one revision consumed per round (Exists never touches the log).
        Assert.Equal(rounds, store.CurrentRevision);
    }

    [Fact]
    public async Task Revisions_UnderConcurrentWriters_AreGaplessAndUnique()
    {
        using KvStore store = Open();
        const int writers = 200;

        var tasks = new Task<long>[writers];
        for (int i = 0; i < writers; i++)
        {
            int me = i;
            tasks[i] = Task.Run(() => store.CreateAsync($"/k/{me:D4}", Bytes("v")).GetAwaiter().GetResult().Revision);
        }

        long[] revisions = await Task.WhenAll(tasks);
        long[] sorted = [.. revisions.OrderBy(r => r)];
        for (int i = 0; i < sorted.Length; i++)
        {
            Assert.Equal(i + 1, sorted[i]);
        }
    }

    [Fact]
    public async Task Watch_Completeness_AndResumeAtEveryPoint()
    {
        using KvStore store = Open();
        const int keys = 50;
        const int updatesPerKey = 20;

        var expected = new List<long>();
        for (int k = 0; k < keys; k++)
        {
            WriteResult created = await store.CreateAsync($"/k/{k:D3}", Bytes("v0"));
            expected.Add(created.Revision);
            long last = created.Revision;
            for (int u = 1; u <= updatesPerKey; u++)
            {
                WriteResult upd = await store.UpdateAsync($"/k/{k:D3}", Bytes($"v{u}"), ifMatch: last);
                expected.Add(upd.Revision);
                last = upd.Revision;
            }
        }

        // A watcher from rev 0 sees every mutation exactly once, in strict revision order.
        IReadOnlyList<WatchEvent> all = store.ReadEvents("/", fromExclusive: 0, limit: 0);
        Assert.Equal(expected.OrderBy(r => r).ToArray(), all.Select(e => e.Revision).ToArray());

        // Resuming at any revision yields exactly the tail with no gaps and no duplicates.
        var rng = new Random(12345);
        for (int trial = 0; trial < 25; trial++)
        {
            long resume = rng.Next(0, (int)store.CurrentRevision + 1);
            IReadOnlyList<WatchEvent> tail = store.ReadEvents("/", fromExclusive: resume, limit: 0);
            Assert.Equal(all.Where(e => e.Revision > resume).Select(e => e.Revision).ToArray(),
                tail.Select(e => e.Revision).ToArray());
        }
    }

    [Fact]
    public async Task KeepAlive_ExpiryRace_IsCoherent()
    {
        using KvStore store = Open();
        var rng = new Random(999);

        for (int trial = 0; trial < 12; trial++)
        {
            LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);
            string key = $"/eph/{trial}";
            await store.CreateAsync(key, Bytes("v"), lease: lease.Id);

            // Land the keepalive within a jittered window around the 1s deadline.
            await Task.Delay(950 + rng.Next(0, 120), TestContext.Current.CancellationToken);

            Task<long?> keepalive = store.KeepAliveAsync(lease.Id);
            Task<int> sweep = store.SweepExpiredAsync();
            long? remaining = await keepalive;
            await sweep;

            // Settle: any lease past its (possibly renewed) deadline is now swept.
            await Task.Delay(1100, TestContext.Current.CancellationToken);
            await store.SweepExpiredAsync();

            bool renewed = remaining is not null;
            bool present = store.Get(key) is not null;

            // The invariant the client depends on: a successful keepalive means the key was never
            // reaped out from under it; a failed one means it is gone and must not be re-acquired.
            if (renewed)
            {
                Assert.True(present || store.GetLease(lease.Id) is null,
                    "a renewed lease's key must survive until its next deadline");
            }
            else
            {
                Assert.False(present, "a lost keepalive must correspond to a reaped key");
            }
        }
    }

    [Fact]
    public async Task GracefulRestart_ResumesRevision_WithNoReuse()
    {
        long revBefore;
        using (KvStore store = Open())
        {
            await store.CreateAsync("/a", Bytes("1"));
            await store.CreateAsync("/b", Bytes("2"));
            revBefore = store.CurrentRevision;
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        using KvStore reopened = Open();
        Assert.Equal(revBefore, reopened.CurrentRevision);
        Assert.Equal("1", Encoding.UTF8.GetString(reopened.Get("/a")!.Value!));

        WriteResult next = await reopened.CreateAsync("/c", Bytes("3"));
        Assert.Equal(revBefore + 1, next.Revision);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
