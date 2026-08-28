namespace Turnstile.Tests;

using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// The single-writer revision counter must publish only committed revisions. A revision allocated by an
/// in-flight transaction is transaction-local: no cross-thread reader (Revision / <c>/status</c> / watch sync)
/// sees it, and a rolled-back transaction reuses its allocated numbers for the next commit — so a resume
/// cursor can never skip a reused, committed event (#192 round 1).
/// </summary>
public sealed class WriteActorTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-wa-{Guid.NewGuid():N}.db");
    private SqliteConnection? _conn;

    private SqliteConnection OpenConn()
    {
        var conn = new SqliteConnection($"Data Source={_dbPath}");
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS log(id INTEGER PRIMARY KEY, v TEXT);
            CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
            INSERT OR IGNORE INTO meta (k, v) VALUES ('committed_revision', '0');
            """;
        cmd.ExecuteNonQuery();
        return conn;
    }

    private static long MetaRevision(SqliteConnection c)
    {
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = 'committed_revision';";
        return long.Parse((string)cmd.ExecuteScalar()!, System.Globalization.CultureInfo.InvariantCulture);
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
    public async Task InFlightAllocatedRevision_IsNotPublished_AndRollbackReusesIt()
    {
        _conn = OpenConn();
        int committedNotifications = 0;
        using var writer = new WriteActor(_conn, startRevision: 0, onCommitted: () => Interlocked.Increment(ref committedNotifications));

        var allocated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Job A allocates a revision and stages a row, signals, then blocks until released — deterministically
        // holding the transaction in flight. On release it either commits or throws (rolls back).
        Task<long> jobA = writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();
            Insert(c, id, "A");
            allocated.SetResult();
            bool commit = release.Task.GetAwaiter().GetResult();
            if (!commit)
            {
                throw new TurnstileValidationException("rolled back");
            }

            return id;
        });

        await allocated.Task;

        // Mid-transaction: a revision (and a row) are allocated, but neither is published.
        Assert.Equal(0, writer.Revision);
        Assert.Equal(0, committedNotifications);

        // Roll the in-flight transaction back.
        release.SetResult(false);
        await Assert.ThrowsAsync<TurnstileValidationException>(() => jobA);
        Assert.Equal(0, writer.Revision);            // unadvanced after rollback
        Assert.Equal(0, committedNotifications);     // a rolled-back write notifies no watcher
        Assert.Equal(0, MetaRevision(_conn));        // the durable counter rolled back with the row

        // The next committed write reuses the revision the rolled-back transaction had allocated. A resume
        // cursor left at 0 therefore sees this event at revision 1 rather than skipping it.
        long idB = await writer.ExecuteAsync<long>((c, next) =>
        {
            long id = next();
            Insert(c, id, "B");
            return id;
        });

        Assert.Equal(1, idB);
        Assert.Equal(1, writer.Revision);            // published only now, post-commit
        Assert.Equal(1, committedNotifications);
        Assert.Equal(1, MetaRevision(_conn));        // durable counter advanced atomically with the row

        // Revision 1 is the committed 'B'; 'A' never persisted.
        using SqliteCommand check = _conn.CreateCommand();
        check.CommandText = "SELECT v FROM log WHERE id = 1;";
        Assert.Equal("B", (string)check.ExecuteScalar()!);
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
