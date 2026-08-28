namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// The durable committed revision (the <c>meta</c> singleton) is the external source of truth for
/// <see cref="KvStore.CurrentRevision"/>. It is advanced in the same transaction as the rows it counts, so a
/// reader — status, range, or the watch one-shot sync — can never report a revision below a committed row it
/// can already see. It is backfilled from <c>MAX(kv.id)</c> for databases that predate the key, and it
/// survives restart (#192 round 2).
/// </summary>
public sealed class RevisionPersistenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-rev-{Guid.NewGuid():N}.db");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Open_OnADatabaseWithoutTheMetaKey_BackfillsFromMaxKvId()
    {
        // Establish some history, then simulate a legacy database: drop the committed-revision meta row so the
        // next open must backfill it from MAX(kv.id) rather than resetting to zero.
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));
            await store.CreateAsync("/b", Bytes("2"));
            await store.CreateAsync("/c", Bytes("3"));
            Assert.Equal(3, store.CurrentRevision);
        }

        SqliteConnection.ClearAllPools();
        using (var raw = new SqliteConnection($"Data Source={_dbPath}"))
        {
            raw.Open();
            using SqliteCommand cmd = raw.CreateCommand();
            cmd.CommandText = "DELETE FROM meta WHERE k = 'committed_revision';";
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        using (KvStore reopened = KvStore.Open(_dbPath))
        {
            Assert.Equal(3, reopened.CurrentRevision);                 // backfilled from MAX(kv.id), not reset
            WriteResult next = await reopened.CreateAsync("/d", Bytes("4"));
            Assert.Equal(4, next.Revision);                            // and monotonic from there
            Assert.Equal(4, reopened.CurrentRevision);
        }
    }

    [Fact]
    public async Task Reopen_PreservesCommittedRevision_AndStaysMonotonic()
    {
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));
            await store.CreateAsync("/b", Bytes("2"));
            Assert.Equal(2, store.CurrentRevision);
        }

        SqliteConnection.ClearAllPools();
        using (KvStore reopened = KvStore.Open(_dbPath))
        {
            Assert.Equal(2, reopened.CurrentRevision);                 // persisted across restart
            Assert.Equal(3, (await reopened.CreateAsync("/c", Bytes("3"))).Revision);
        }
    }

    [Fact]
    public async Task ConcurrentWritesAndReads_CurrentRevisionNeverLagsAVisibleEvent()
    {
        // The watch invariant: the one-shot sync must be >= every event it could have just emitted. The watch
        // reads events, then reads CurrentRevision for the sync — so, mirroring that order, a reader must never
        // see a committed event whose revision exceeds the CurrentRevision read just after. With the counter
        // read from the in-memory field this failed in the commit→publish window; reading the durable meta
        // counter (committed with the rows) closes it.
        using KvStore store = KvStore.Open(_dbPath);
        CancellationToken ct = TestContext.Current.CancellationToken;
        const int writes = 300;

        Task writer = Task.Run(async () =>
        {
            for (int i = 0; i < writes; i++)
            {
                await store.CreateAsync($"/k/{i:D4}", Bytes("v"), lease: null);
            }
        }, ct);

        long maxObservedEvent = 0;
        while (!writer.IsCompleted)
        {
            IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: 0, limit: 0);
            long maxEvent = events.Count > 0 ? events[^1].Revision : 0;
            long sync = store.CurrentRevision;
            Assert.True(sync >= maxEvent, $"sync {sync} lagged a visible event {maxEvent}");
            maxObservedEvent = Math.Max(maxObservedEvent, maxEvent);
        }

        await writer;

        // Final consistency: the counter matches the log's last event exactly, and every event was countable.
        Assert.Equal(writes, store.CurrentRevision);
        IReadOnlyList<WatchEvent> all = store.ReadEvents("/", fromExclusive: 0, limit: 0);
        Assert.Equal(writes, all.Count);
        Assert.Equal(store.CurrentRevision, all[^1].Revision);
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
