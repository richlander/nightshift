namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #199: two independently opened stores (or a daemon plus a library instance) over one SQLite file
/// each ran a WriteActor that allocated revisions from a value cached at open, so both handed out N+1 and one
/// collided forever. The fix reads the durable committed revision under BEGIN IMMEDIATE — SQLite's
/// cross-connection/process writer lock — as each transaction's allocation base, so allocation is globally
/// serialized. These tests open multiple instances on one file and prove revisions stay globally unique,
/// monotonic and gapless, and every committed row persists.
/// </summary>
public sealed class ConcurrentWritersTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-conc-{Guid.NewGuid():N}.db");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static TxnOp Put(string key, string value) => new(TxnOpKind.Put, key, Bytes(value), null, false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TwoInstances_AlternatingThenConcurrentWrites_AreGloballyUniqueMonotonicGapless()
    {
        using KvStore a = KvStore.Open(_dbPath);
        using KvStore b = KvStore.Open(_dbPath);

        // Both open at the same initial revision — the exact collision precondition.
        Assert.Equal(0, a.CurrentRevision);
        Assert.Equal(0, b.CurrentRevision);

        // Alternating writes across the two instances get a contiguous 1..10 with no collision.
        var alternating = new List<long>();
        for (int i = 0; i < 10; i++)
        {
            KvStore w = (i % 2 == 0) ? a : b;
            WriteResult r = await w.CreateAsync($"/alt/{i:D2}", Bytes(i.ToString()));
            Assert.Equal(WriteStatus.Created, r.Status);
            alternating.Add(r.Revision);
        }

        Assert.Equal(Enumerable.Range(1, 10).Select(x => (long)x), alternating);

        // Now fire a concurrent burst from both instances at once; the revisions are still a unique, gapless,
        // contiguous set continuing from 11.
        var tasks = new List<Task<WriteResult>>();
        for (int i = 0; i < 20; i++)
        {
            KvStore w = (i % 2 == 0) ? a : b;
            tasks.Add(w.CreateAsync($"/conc/{i:D2}", Bytes(i.ToString())));
        }

        WriteResult[] results = await Task.WhenAll(tasks);
        long[] concurrent = [.. results.Select(r => r.Revision).OrderBy(x => x)];

        Assert.All(results, r => Assert.Equal(WriteStatus.Created, r.Status));
        Assert.Equal(20, concurrent.Distinct().Count());                       // globally unique
        Assert.Equal(Enumerable.Range(11, 20).Select(x => (long)x), concurrent); // monotonic + gapless

        // Every row persists and is visible from either instance; both report the same durable revision.
        Assert.Equal(30, a.CurrentRevision);
        Assert.Equal(30, b.CurrentRevision);
        for (int i = 0; i < 10; i++)
        {
            Assert.NotNull(a.Get($"/alt/{i:D2}"));
            Assert.NotNull(b.Get($"/alt/{i:D2}"));
        }

        for (int i = 0; i < 20; i++)
        {
            Assert.NotNull(b.Get($"/conc/{i:D2}"));
        }

        // The log itself is gapless 1..30 with no duplicate ids.
        IReadOnlyList<WatchEvent> all = a.ReadEvents("/", 0, 0);
        Assert.Equal(Enumerable.Range(1, 30).Select(x => (long)x), all.Select(e => e.Revision));
    }

    [Fact]
    public async Task StaleInstance_AfterAnotherAdvances_StartsAfterTheLatestCommitted()
    {
        // The stale-cache case the old code got wrong: open one instance, let another advance several
        // revisions, then write from the stale one. Its next transaction must allocate after the latest
        // committed value (read under the write lock), not after the revision it cached at open.
        using KvStore stale = KvStore.Open(_dbPath);
        Assert.Equal(0, stale.CurrentRevision);

        using (KvStore advancer = KvStore.Open(_dbPath))
        {
            await advancer.CreateAsync("/a", Bytes("1"));   // rev 1
            await advancer.CreateAsync("/b", Bytes("2"));   // rev 2
            await advancer.CreateAsync("/c", Bytes("3"));   // rev 3
        }

        // A multi-op transaction from the stale instance: it starts after 3 and stays contiguous (4, 5).
        TxnResult r = await stale.TxnAsync([], [Put("/x", "x"), Put("/y", "y")], []);
        Assert.True(r.Succeeded);
        Assert.Equal(5, r.Revision);                        // max allocated revision of the txn
        Assert.Equal(4, stale.Get("/x")!.ModRevision);
        Assert.Equal(5, stale.Get("/y")!.ModRevision);
        Assert.Equal(5, stale.CurrentRevision);
    }

    [Fact]
    public async Task RejectedWriteInOneInstance_ConsumesNoRevision_OtherReusesTheNext()
    {
        using KvStore a = KvStore.Open(_dbPath);
        using KvStore b = KvStore.Open(_dbPath);

        await a.CreateAsync("/spec", Bytes("frozen"), immutable: true);   // rev 1

        // A rejected write in a rolls its transaction back and consumes no revision (immutable keys refuse
        // puts).
        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => a.TxnAsync([], [Put("/spec", "x")], []));

        // The other instance's next write reuses exactly the next revision — 2, not 3.
        WriteResult next = await b.CreateAsync("/b", Bytes("2"));
        Assert.Equal(2, next.Revision);
        Assert.Equal(2, a.CurrentRevision);
        Assert.Equal(2, b.CurrentRevision);
    }

    [Fact]
    public async Task LocalStoreInstances_SafeAlongside_ForStorageIntegrity()
    {
        // The public "safe alongside" claim, at the ITurnstile surface: two LocalStore instances over one
        // file interleave writes with globally unique, gapless revisions and all rows persist.
        using LocalStore la = await LocalStore.OpenAsync(_dbPath);
        using LocalStore lb = await LocalStore.OpenAsync(_dbPath);

        var tasks = new List<Task<WriteResult>>();
        for (int i = 0; i < 12; i++)
        {
            LocalStore w = (i % 2 == 0) ? la : lb;
            tasks.Add(w.CreateAsync($"/ls/{i:D2}", Bytes(i.ToString()), ct: Ct));
        }

        WriteResult[] results = await Task.WhenAll(tasks);
        long[] revs = [.. results.Select(r => r.Revision).OrderBy(x => x)];

        Assert.Equal(Enumerable.Range(1, 12).Select(x => (long)x), revs);
        Assert.Equal(12, await la.GetRevisionAsync(Ct));
        Assert.Equal(12, await lb.GetRevisionAsync(Ct));
        for (int i = 0; i < 12; i++)
        {
            Assert.NotNull(await la.GetAsync($"/ls/{i:D2}", Ct));
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                }
            }
        }
    }
}
