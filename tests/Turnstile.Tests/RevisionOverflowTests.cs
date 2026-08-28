namespace Turnstile.Tests;

using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #198: revision allocation must never wrap <see cref="long"/>. When the durable
/// <c>committed_revision</c> sits at or one below <see cref="long.MaxValue"/>, a mutation that needs another
/// revision must fail closed — before any overflowed row or counter move becomes visible — so the whole
/// SQLite transaction rolls back, the committed revision does not move, and no change pulse fires. Reads and
/// no-op transactions stay valid at exhaustion, and <c>long.MaxValue</c> itself is allocatable exactly once.
///
/// The first group drives the allocator directly through <see cref="WriteActor"/> with an
/// <c>onCommitted</c> counter — a direct notification seam, not a wall-clock pulse probe. The second group
/// seeds a real <see cref="KvStore"/> in the legitimate compacted-history state (a committed revision above
/// <c>MAX(kv.id)</c>, which <see cref="Schema"/> accepts) and exercises the public write/read API.
/// </summary>
public sealed class RevisionOverflowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-overflow-{Guid.NewGuid():N}.db");
    private SqliteConnection? _conn;

    private const long Max = long.MaxValue;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // ---- WriteActor-level harness (direct allocator + notification counter) ----

    private SqliteConnection OpenConnSeeded(long committed)
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA busy_timeout=5000;
            CREATE TABLE IF NOT EXISTS log(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
            INSERT OR REPLACE INTO meta (k, v) VALUES ('committed_revision', $c);
            """;
        cmd.Parameters.AddWithValue("$c", committed.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
        return conn;
    }

    private long MetaRevision()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = 'committed_revision';";
        return long.Parse((string)cmd.ExecuteScalar()!, CultureInfo.InvariantCulture);
    }

    private long LogRowCount()
    {
        using var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM log;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static void Insert(SqliteConnection c, long id, string v)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO log(id, v) VALUES ($id, $v);";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$v", v);
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task AtMaxValue_WriteThatAllocates_FailsClosed_NoRowNoRevisionNoPulse()
    {
        _conn = OpenConnSeeded(Max);
        int notifications = 0;
        using var writer = new WriteActor(_conn, onCommitted: () => Interlocked.Increment(ref notifications));

        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();          // committed is already MaxValue: this must throw before staging a row
            Insert(c, id, "over");
            return id;
        }));

        Assert.Equal(Max, MetaRevision());   // counter did not move
        Assert.Equal(0, LogRowCount());      // no row was staged/committed
        Assert.Equal(0, notifications);      // a failed write pulses no watcher
    }

    [Fact]
    public async Task AtMaxValue_ReadOnlyJob_Commits_AndWriterStaysResponsive()
    {
        _conn = OpenConnSeeded(Max);
        int notifications = 0;
        using var writer = new WriteActor(_conn, onCommitted: () => Interlocked.Increment(ref notifications));

        // A no-op/read-only job allocates nothing, so it commits fine even at exhaustion — and does not pulse.
        long rows = await writer.ExecuteAsync<long>((c, _) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM log;";
            return (long)cmd.ExecuteScalar()!;
        });
        Assert.Equal(0, rows);
        Assert.Equal(Max, MetaRevision());
        Assert.Equal(0, notifications);

        // A write that needs a revision still fails closed.
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();
            Insert(c, id, "x");
            return id;
        }));

        // The writer thread survived the failure: a later read-only job is still serviced.
        long rowsAgain = await writer.ExecuteAsync<long>((c, _) =>
        {
            using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM log;";
            return (long)cmd.ExecuteScalar()!;
        });
        Assert.Equal(0, rowsAgain);
        Assert.Equal(0, notifications);
    }

    [Fact]
    public async Task AtMaxValueMinusOne_TwoRevisionTxn_RollsBackWhole_ThenOneCommitsMax_ThenExhausted()
    {
        _conn = OpenConnSeeded(Max - 1);
        int notifications = 0;
        using var writer = new WriteActor(_conn, onCommitted: () => Interlocked.Increment(ref notifications));

        // A single job that stages MaxValue and then requests one more revision must roll back whole.
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => writer.ExecuteAsync<long>((c, next) =>
        {
            long first = next();       // MaxValue-1 -> MaxValue, staged
            Insert(c, first, "A");
            long second = next();      // -> overflow: throw, rolling back the staged row and counter
            Insert(c, second, "B");
            return second;
        }));

        Assert.Equal(Max - 1, MetaRevision());   // whole rollback: counter unmoved
        Assert.Equal(0, LogRowCount());          // neither row survived
        Assert.Equal(0, notifications);

        // A later single-revision transaction may still consume MaxValue exactly.
        long committed = await writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();
            Insert(c, id, "C");
            return id;
        });
        Assert.Equal(Max, committed);
        Assert.Equal(Max, MetaRevision());
        Assert.Equal(1, LogRowCount());
        Assert.Equal(1, notifications);

        // After that, every revision-requiring write fails deterministically.
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();
            Insert(c, id, "D");
            return id;
        }));
        Assert.Equal(Max, MetaRevision());
        Assert.Equal(1, LogRowCount());
        Assert.Equal(1, notifications);          // the failed write did not pulse
    }

    // ---- KvStore-level (real Schema, compacted-history seed) ----

    /// <summary>
    /// Opens a real <see cref="KvStore"/> whose durable committed revision is <paramref name="committed"/>
    /// with an empty <c>kv</c> log — the legitimate post-compaction state where the counter sits above
    /// <c>MAX(kv.id)</c>. The value is written through the real schema and then reopened, so
    /// <see cref="Schema"/> reconciliation validates it rather than resetting it; nothing bypasses the schema
    /// invariants.
    /// </summary>
    private KvStore OpenStoreSeeded(long committed)
    {
        using (KvStore init = KvStore.Open(_dbPath))
        {
            // Creates the real schema with committed_revision = 0.
        }

        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE meta SET v = $v WHERE k = 'committed_revision';";
            cmd.Parameters.AddWithValue("$v", committed.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
        KvStore store = KvStore.Open(_dbPath);   // Schema.Ensure accepts committed >= MAX(kv.id)
        Assert.Equal(committed, store.CurrentRevision);
        return store;
    }

    [Fact]
    public async Task CommittedAtMax_CreateFails_NoPulse_ReadAndNoOpTxnStillWork()
    {
        using KvStore store = OpenStoreSeeded(Max);

        Task changed = store.WaitForChangeAsync();
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.CreateAsync("/k", Bytes("v")));

        Assert.Equal(Max, store.CurrentRevision);          // unmoved
        Assert.Null(store.Get("/k"));                      // no row became visible
        Assert.False(changed.IsCompleted);                 // no pulse fired for the failed write

        // A no-op transaction (only Gets) allocates nothing and reports the durable committed revision.
        TxnResult noop = await store.TxnAsync([], [new TxnOp(TxnOpKind.Get, "/k", null, null, false)], []);
        Assert.True(noop.Succeeded);
        Assert.Equal(Max, noop.Revision);

        // A plain read still works, and the store keeps reporting the same revision.
        Assert.Null(store.Get("/anything"));
        Assert.Equal(Max, store.CurrentRevision);
    }

    [Fact]
    public async Task CommittedAtMax_LeaseCreate_StillSucceeds_AllocatingNoRevision()
    {
        using KvStore store = OpenStoreSeeded(Max);

        // Lease creation allocates no log revision, so it commits even at revision exhaustion.
        LeaseInfo lease = await store.CreateLeaseAsync(60);
        Assert.NotNull(lease.Id);
        Assert.Equal(Max, store.CurrentRevision);
    }

    [Fact]
    public async Task CommittedAtMaxMinusOne_TwoPutTxn_RollsBackWhole_ThenSinglePutCommitsMax_ThenFails()
    {
        using KvStore store = OpenStoreSeeded(Max - 1);

        // A transaction with two puts needs two revisions: the second overflows, so the whole txn rolls back.
        Task changed = store.WaitForChangeAsync();
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.TxnAsync(
            [],
            [
                new TxnOp(TxnOpKind.Put, "/a", Bytes("1"), null, false),
                new TxnOp(TxnOpKind.Put, "/b", Bytes("2"), null, false),
            ],
            []));

        Assert.Equal(Max - 1, store.CurrentRevision);      // whole rollback
        Assert.Null(store.Get("/a"));
        Assert.Null(store.Get("/b"));
        Assert.False(changed.IsCompleted);                 // no pulse

        // A single-revision write may still consume MaxValue exactly.
        WriteResult created = await store.CreateAsync("/c", Bytes("3"));
        Assert.Equal(Max, created.Revision);
        Assert.Equal(Max, store.CurrentRevision);
        Assert.Equal("3", Encoding.UTF8.GetString(store.Get("/c")!.Value!));

        // Every subsequent revision-requiring write now fails deterministically.
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.CreateAsync("/d", Bytes("4")));
        Assert.Equal(Max, store.CurrentRevision);
        Assert.Null(store.Get("/d"));
    }

    public void Dispose()
    {
        _conn?.Dispose();
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
