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
    public async Task Open_OnAStaleButValidMetaBelowMaxKvId_RepairsUpward()
    {
        // The round-3 blocker: a syntactically valid committed_revision that has fallen below MAX(kv.id) must
        // be repaired upward on open, or CurrentRevision reports below a visible row and the next write reuses
        // an existing id and fails. Rows 1..3 with a stale meta of 1 must reopen at 3, and the next write gets 4.
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));
            await store.CreateAsync("/b", Bytes("2"));
            await store.CreateAsync("/c", Bytes("3"));
        }

        SetMeta("1");

        SqliteConnection.ClearAllPools();
        using (KvStore reopened = KvStore.Open(_dbPath))
        {
            Assert.Equal(3, reopened.CurrentRevision);                 // repaired up to MAX(kv.id)
            WriteResult next = await reopened.CreateAsync("/d", Bytes("4"));
            Assert.Equal(4, next.Revision);                            // no reused id, no collision
            Assert.Equal(4, reopened.CurrentRevision);
        }
    }

    [Fact]
    public async Task Open_OnAMetaAboveMaxKvId_PreservesIt()
    {
        // A committed revision above the surviving MAX(kv.id) is legitimate after compaction/history retention
        // removes rows, so it is preserved rather than dragged down.
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));
        }

        SetMeta("10");

        SqliteConnection.ClearAllPools();
        using (KvStore reopened = KvStore.Open(_dbPath))
        {
            Assert.Equal(10, reopened.CurrentRevision);                // preserved (> MAX(kv.id))
            Assert.Equal(11, (await reopened.CreateAsync("/b", Bytes("2"))).Revision);
        }
    }

    [Fact]
    public async Task ReadOnlyTxn_OnAPreservedAboveMaxMeta_ReportsTheDurableRevisionNotMaxKvId()
    {
        // The final-round blocker: a no-write txn returned MAX(kv.id), which lags a preserved committed
        // revision. With rows leaving MAX(kv.id)=1 but committed_revision=10, a read-only txn performed
        // before the next write must report 10, and the next write then lands at 11.
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));
        }

        SetMeta("10");

        SqliteConnection.ClearAllPools();
        using (KvStore reopened = KvStore.Open(_dbPath))
        {
            TxnResult readOnly = await reopened.TxnAsync([], [new TxnOp(TxnOpKind.Get, "/a", null, null, false)], []);
            Assert.True(readOnly.Succeeded);
            Assert.Equal(10, readOnly.Revision);                       // durable revision, not MAX(kv.id)=1
            Assert.Equal(11, (await reopened.CreateAsync("/b", Bytes("2"))).Revision);
        }
    }

    [Fact]
    public async Task ReadOnlyTxn_OnAFreshStore_ReportsRevisionZero()
    {
        using KvStore store = KvStore.Open(_dbPath);
        TxnResult noop = await store.TxnAsync([], [], []);             // no compares, no ops
        Assert.True(noop.Succeeded);
        Assert.Equal(0, noop.Revision);
        Assert.Equal(0, store.CurrentRevision);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("-5")]
    [InlineData("3.5")]
    public async Task Open_OnAMalformedMeta_FailsVisibly(string corrupt)
    {
        using (KvStore store = KvStore.Open(_dbPath))
        {
            await store.CreateAsync("/a", Bytes("1"));   // one row so the database is otherwise valid
        }

        SetMeta(corrupt);

        SqliteConnection.ClearAllPools();
        Assert.Throws<InvalidOperationException>(() => KvStore.Open(_dbPath));
    }

    private void SetMeta(string value)
    {
        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection($"Data Source={_dbPath}");
        raw.Open();
        using SqliteCommand cmd = raw.CreateCommand();
        cmd.CommandText = "UPDATE meta SET v = $v WHERE k = 'committed_revision';";
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
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
