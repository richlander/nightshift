namespace Turnstile.Storage;

using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// The single-writer actor. All mutations funnel through one connection on one dedicated thread,
/// so writes are serialized without scattering BEGIN IMMEDIATE discipline across the codebase.
/// Revisions are allocated transaction-locally — strictly monotonic, gapless across commits, never
/// reused by a committed row — and the public counter is advanced only after the transaction commits.
/// A rolled-back write therefore never publishes a revision (nor a phantom one mid-flight), and its
/// allocated numbers are reused by the next transaction.
/// </summary>
internal sealed class WriteActor : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    private readonly Thread _thread;
    private readonly Action? _onCommitted;
    private long _revision;

    public WriteActor(SqliteConnection conn, long startRevision, Action? onCommitted = null)
    {
        _conn = conn;
        _revision = startRevision;
        _onCommitted = onCommitted;
        _thread = new Thread(Loop) { IsBackground = true, Name = "turnstile-writer" };
        _thread.Start();
    }

    /// <summary>The highest revision committed so far. Safe to read from any thread.</summary>
    public long Revision => Interlocked.Read(ref _revision);

    /// <summary>
    /// Runs <paramref name="work"/> inside a serialized write transaction. The delegate receives the
    /// write connection and a revision allocator; it must perform its reads and inserts on that
    /// connection. The transaction commits on return and rolls back (restoring the revision) on throw.
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
            // The revision counter is allocated transaction-locally and published only on commit. The public
            // _revision field (read cross-thread via Revision, /status, and watch sync) therefore never shows
            // a number belonging to an in-flight transaction, and a rolled-back write neither advances it nor
            // needs to restore it — so a reused revision cannot be skipped by a resume cursor that observed a
            // phantom. The single writer thread means `allocated` needs no synchronization.
            long committed = Interlocked.Read(ref _revision);
            long allocated = committed;
            using SqliteTransaction tx = _conn.BeginTransaction(deferred: false);
            try
            {
                long AllocateRevision() => ++allocated;
                object? result = job.Work(_conn, AllocateRevision);

                // Persist the committed revision in the SAME transaction as the rows it counts, so the durable
                // meta counter and the kv rows commit and roll back together — a reader can never see one
                // without the other. A no-op transaction (nothing allocated) leaves the counter untouched.
                if (allocated > committed)
                {
                    PersistRevision(tx, allocated);
                }

                tx.Commit();

                // The in-memory field is now only the writer-private allocation cursor (the external truth is
                // the meta counter). Advance it and notify watchers only after the commit succeeds.
                if (allocated > committed)
                {
                    Interlocked.Exchange(ref _revision, allocated);
                    job.Tcs.SetResult(result);
                    _onCommitted?.Invoke();
                }
                else
                {
                    job.Tcs.SetResult(result);
                }
            }
            catch (Exception ex)
            {
                try
                {
                    tx.Rollback();
                }
                catch
                {
                    // A failed rollback leaves the connection unusable, but the process owns a single
                    // writer; surfacing the original error to the caller is the useful signal.
                }

                // Nothing was published, so _revision is already correct; the allocated numbers are discarded
                // and reused by the next transaction.
                job.Tcs.SetException(ex);
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
