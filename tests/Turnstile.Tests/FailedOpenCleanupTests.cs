namespace Turnstile.Tests;

using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202 follow-up: the ownership contract makes <see cref="LocalStore.OpenAsync"/>'s failure path
/// load-bearing. Once <see cref="KvStore.Open"/> succeeds it has already started a <c>WriteActor</c> (a
/// background thread plus a write connection), so if the sweep-on-open step then throws, disposing only the
/// <c>ModeLock</c> would leak that writer while still — via the nested finally — freeing the lock. This test
/// forces the sweep to fail <em>after</em> KvStore creation and proves the open leaves nothing behind: the
/// shared mode lock is released (a daemon-grade exclusive lock acquires immediately) and, because the whole
/// KvStore is disposed on the way out, its writer is drained too.
/// </summary>
public sealed class FailedOpenCleanupTests : IDisposable
{
    private readonly List<string> _dbs = [];

    /// <summary>
    /// A database whose <c>lease</c> table is missing the <c>expires_at</c> column is a deterministic seam:
    /// <see cref="KvStore.Open"/> succeeds (its schema step is <c>CREATE TABLE IF NOT EXISTS</c>, so it leaves
    /// the pre-existing malformed table untouched and creates the rest), but the very next step —
    /// <see cref="KvStore.SweepExpiredAsync"/>, whose only query filters on <c>expires_at</c> — throws
    /// "no such column". Nothing else on the open path reads that column, so the failure is provably located
    /// after KvStore creation, exercising exactly the writer-owning cleanup branch. No production fault
    /// injection is introduced.
    /// </summary>
    [Fact]
    public async Task SweepFailsAfterKvStoreCreated_OpenDisposesWriterAndReleasesLock()
    {
        string db = NewDb();
        PreCreateWithMalformedLeaseTable(db);

        // Capture the KvStore this open creates so we can prove its writer was disposed, keyed by canonical
        // path so a parallel test's open cannot clobber the capture.
        string canonical = DatabasePath.Canonicalize(db);
        KvStore? created = null;
        LocalStore.KvStoreOpenObserversForTests[canonical] = kv => created = kv;
        try
        {
            // The open must surface the sweep failure. That it is specifically the missing-column error proves
            // we got past KvStore.Open (which spun up the writer) and failed inside SweepExpiredAsync.
            SqliteException ex = await Assert.ThrowsAsync<SqliteException>(() => LocalStore.OpenAsync(db));
            Assert.Contains("expires_at", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            LocalStore.KvStoreOpenObserversForTests.TryRemove(canonical, out _);
        }

        // Direct evidence the writer did not leak: the KvStore created during the failed open had its writer
        // (background thread + connection) disposed on the way out. This is the assertion that fails if the
        // catch releases only the ModeLock. It is deterministic — Dispose joins the writer thread — so there is
        // no timing or thread-count check here.
        Assert.NotNull(created);
        Assert.True(created!.IsWriterDisposedForTests, "failed open must dispose the writer it created, not just the lock");

        // The externally visible half: the failed open released the shared mode lock, so a daemon-grade
        // EXCLUSIVE lock — which a leaked shared lock would refuse with TurnstileDatabaseInUseException — is
        // available immediately, with no retry or delay.
        using (ModeLock exclusive = ModeLock.AcquireExclusive(canonical))
        {
            Assert.NotNull(exclusive);
        }

        // The writer half: KvStore.Dispose (which drains the WriteActor thread and closes its connection) ran on
        // the failure path, so the file has no lingering writer connection. Clearing the pool and reopening a
        // fresh, valid store on a sibling database with no interference is the proportional, deterministic
        // evidence available without adding production introspection surface or asserting on thread counts.
        SqliteConnection.ClearAllPools();
        string healthy = NewDb();
        using LocalStore reopened = await LocalStore.OpenAsync(healthy);
        Assert.True(await reopened.GetRevisionAsync(TestContext.Current.CancellationToken) >= 0);
    }

    private static void PreCreateWithMalformedLeaseTable(string db)
    {
        string cs = new SqliteConnectionStringBuilder
        {
            DataSource = db,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ConnectionString;

        using SqliteConnection conn = new(cs);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        // Deliberately omit expires_at so the CREATE TABLE IF NOT EXISTS in Schema.Ensure is a no-op and the
        // sweep's `WHERE expires_at <= ?` cannot bind.
        cmd.CommandText = "CREATE TABLE lease (id TEXT PRIMARY KEY, ttl_secs INTEGER NOT NULL);";
        cmd.ExecuteNonQuery();
    }

    private string NewDb()
    {
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-failopen-{Guid.NewGuid():N}.db");
        _dbs.Add(db);
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string db in _dbs)
        {
            foreach (string path in new[] { db, db + "-wal", db + "-shm", db + "-modelock" })
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // Unique per run; a locked handle is harmless.
                    }
                }
            }
        }
    }
}
