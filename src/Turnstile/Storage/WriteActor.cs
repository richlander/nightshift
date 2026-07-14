namespace Turnstile.Storage;

using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;

/// <summary>
/// The single-writer actor. All mutations funnel through one connection on one dedicated thread,
/// so writes are serialized without scattering BEGIN IMMEDIATE discipline across the codebase.
/// Revisions are allocated here — strictly monotonic, gapless, never reused — and rolled back
/// in memory if the transaction fails, so a rolled-back write never consumes a revision.
/// </summary>
internal sealed class WriteActor : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly BlockingCollection<Job> _queue = new(new ConcurrentQueue<Job>());
    private readonly Thread _thread;
    private long _revision;

    public WriteActor(SqliteConnection conn, long startRevision)
    {
        _conn = conn;
        _revision = startRevision;
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
            long snapshot = Interlocked.Read(ref _revision);
            using SqliteTransaction tx = _conn.BeginTransaction(deferred: false);
            try
            {
                long AllocateRevision() => Interlocked.Increment(ref _revision);
                object? result = job.Work(_conn, AllocateRevision);
                tx.Commit();
                job.Tcs.SetResult(result);
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

                Interlocked.Exchange(ref _revision, snapshot);
                job.Tcs.SetException(ex);
            }
        }
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
