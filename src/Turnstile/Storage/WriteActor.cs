namespace Turnstile.Storage;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// The single-writer actor for one <see cref="KvStore"/> instance: all of this instance's mutations funnel
/// through one connection on one dedicated thread, so they serialize without scattering BEGIN IMMEDIATE
/// discipline across the codebase.
///
/// Revisions are NOT allocated from any cached in-memory value. Each job opens BEGIN IMMEDIATE — SQLite's
/// cross-connection, cross-process writer lock — and reads the durable <c>meta.committed_revision</c> under
/// that lock as its allocation base. That makes allocation globally serialized: two independently opened
/// instances (or daemons) over one database file can never both hand out N+1, because only one holds the
/// write lock at a time and each reads the true latest committed revision before allocating. Allocation is
/// transaction-local and the counter advances in the same transaction as the rows it counts, so a rollback
/// consumes nothing and a multi-row transaction stays contiguous. The local change signal still pulses after
/// commit — that notifies this instance's watchers only; cross-instance notification is out of scope here.
/// </summary>
internal sealed class WriteActor : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    private readonly Thread _thread;
    private readonly Action? _onCommitted;

    public WriteActor(SqliteConnection conn, Action? onCommitted = null)
    {
        _conn = conn;
        _onCommitted = onCommitted;
        _thread = new Thread(Loop) { IsBackground = true, Name = "turnstile-writer" };
        _thread.Start();
    }

    /// <summary>
    /// Runs <paramref name="work"/> inside a serialized write transaction. The delegate receives the
    /// write connection and a revision allocator; it must perform its reads and inserts on that
    /// connection. The transaction commits on return and rolls back on throw.
    /// </summary>
    public Task<T> ExecuteAsync<T>(Func<SqliteConnection, Func<long>, T> work)
    {
        var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _queue.Add(new Job((c, next) => work(c, next), tcs));
        return Await(tcs.Task);

        static async Task<T> Await(Task<object?> task) => (T)(await task.ConfigureAwait(false))!;
    }

    private void Loop()
    {
        foreach (Job job in _queue.GetConsumingEnumerable())
        {
            // BEGIN IMMEDIATE is inside the try: on contention SQLite waits up to busy_timeout and then throws
            // SQLITE_BUSY, which must surface to the caller as a bounded, visible failure — not crash the
            // writer thread nor retry forever nor fall through to success-shaped output.
            SqliteTransaction? tx = null;
            try
            {
                tx = _conn.BeginTransaction(deferred: false);

                // The allocation base is the durable committed revision read UNDER the write lock, never a
                // cached startup value. Under BEGIN IMMEDIATE this is the latest committed revision across all
                // connections and processes, so allocation is globally unique and gapless.
                long committed = CommittedRevision.Read(_conn);
                long allocated = committed;
                long AllocateRevision() => ++allocated;

                object? result = job.Work(_conn, AllocateRevision);

                // Persist the counter in the SAME transaction as the rows it counts, so they commit and roll
                // back together. A no-op/read-only transaction allocates nothing and leaves the counter alone.
                if (allocated > committed)
                {
                    PersistRevision(tx, allocated);
                }

                tx.Commit();

                // Post-commit ordering: hand the result back, then pulse this instance's watchers, only when
                // the log actually advanced.
                job.Tcs.SetResult(result);
                if (allocated > committed)
                {
                    _onCommitted?.Invoke();
                }
            }
            catch (Exception ex)
            {
                try
                {
                    tx?.Rollback();
                }
                catch
                {
                    // A failed rollback leaves the connection unusable, but the process owns a single
                    // writer; surfacing the original error to the caller is the useful signal.
                }

                job.Tcs.SetException(ex);
            }
            finally
            {
                tx?.Dispose();
            }
        }
    }

    private void PersistRevision(SqliteTransaction tx, long revision)
    {
        using SqliteCommand cmd = _conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "UPDATE meta SET v = $v WHERE k = $k;";
        cmd.Parameters.AddWithValue("$v", revision.ToString(CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("$k", Schema.CommittedRevisionKey);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        _queue.CompleteAdding();
        if (_thread.IsAlive)
        {
            _thread.Join();
        }

        _conn.Dispose();
    }

    private readonly record struct Job(
        Func<SqliteConnection, Func<long>, object?> Work,
        TaskCompletionSource<object?> Tcs);
}
