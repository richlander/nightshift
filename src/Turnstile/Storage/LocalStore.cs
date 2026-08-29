namespace Turnstile.Storage;

/// <summary>
/// Library-mode <see cref="ITurnstile"/>: opens the SQLite file directly and adapts <see cref="KvStore"/>.
/// This is the degraded-but-useful path — no daemon, no always-on sweeper. Correctness of lease expiry
/// is therefore <em>eventual</em>: <see cref="OpenAsync"/> sweeps once on open so a leaked lock from a
/// dead process is reclaimed the next time any process touches the store.
///
/// <para>Direct mode is for when <em>no daemon owns the database</em>. <see cref="OpenAsync"/> takes a shared
/// <see cref="ModeLock"/> first, so any number of direct stores can share one file — WAL plus globally
/// serialized revision allocation (each writer reads the durable committed revision under BEGIN IMMEDIATE,
/// never a cached value) keeps revisions globally unique, monotonic and gapless, and every committed row
/// persists (#199) — but a database a daemon holds exclusively refuses the open with a
/// <see cref="TurnstileDatabaseInUseException"/>. That boundary is what keeps a daemon's watch honest: because
/// no direct store can open the file while the daemon owns it, no commit ever bypasses the daemon's change
/// signal.</para>
///
/// <para>Watch liveness never survives the direct path regardless: each instance's change signal is
/// in-memory, so a watcher here could never be woken by another instance's commit. Rather than expose a watch
/// that can silently park on a process-local signal, <see cref="WatchAsync"/> rejects up front with a
/// <see cref="TurnstileWatchUnavailableException"/> — watch liveness is daemon-only (#202). Finite
/// reads/writes/txns/leases remain fully useful here; only the live watch requires the daemon.</para>
/// </summary>
public sealed class LocalStore : ITurnstile
{
    private readonly KvStore _kv;
    private readonly ModeLock _modeLock;

    private LocalStore(KvStore kv, ModeLock modeLock)
    {
        _kv = kv;
        _modeLock = modeLock;
    }

    /// <summary>Opens the store at <paramref name="dbPath"/> and sweeps expired leases once (sweep-on-open).</summary>
    /// <exception cref="TurnstileDatabaseInUseException">
    /// If a daemon exclusively owns the database (#202). Direct library-mode access is refused while a daemon
    /// is running, so callers reach it through the daemon's socket instead of opening the file behind its back.
    /// </exception>
    public static async Task<LocalStore> OpenAsync(string dbPath)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        // Take the shared mode lock before touching SQLite: if a daemon owns this database it fails here,
        // before any connection is opened, so a direct write can never slip in behind a daemon's live watch.
        // Multiple direct stores share the lock, so daemonless multi-writer use is unaffected (#199).
        ModeLock modeLock = ModeLock.AcquireShared(dbPath);
        try
        {
            KvStore kv = KvStore.Open(dbPath);
            // Sweep-on-open: library mode has no always-on sweeper, so reclaim any leases that expired while
            // no process was attached. This emits the delete events a lazy read would silently swallow.
            await kv.SweepExpiredAsync().ConfigureAwait(false);
            return new LocalStore(kv, modeLock);
        }
        catch
        {
            // A failed open must not leak the lock, or the database would stay marked in-use with no store
            // behind it — releasing it here lets a daemon (or a retry) acquire immediately.
            modeLock.Dispose();
            throw;
        }
    }

    public Task<long> GetRevisionAsync(CancellationToken ct = default)
        => Task.FromResult(_kv.CurrentRevision);

    public Task<KeyState?> GetAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_kv.Get(key));

    public Task<IReadOnlyList<KeyState>> RangeAsync(string prefix, int limit = 0, bool keysOnly = false, CancellationToken ct = default)
        => Task.FromResult(_kv.Range(prefix, limit, keysOnly));

    public Task<WriteResult> CreateAsync(string key, byte[] value, bool immutable = false, string? lease = null, CancellationToken ct = default)
        => _kv.CreateAsync(key, value, immutable, lease);

    public Task<WriteResult> UpdateAsync(string key, byte[] value, long? ifMatch, bool unconditional = false, CancellationToken ct = default)
        => _kv.UpdateAsync(key, value, ifMatch, unconditional);

    public Task<WriteResult> DeleteAsync(string key, long? ifMatch, bool unconditional = false, CancellationToken ct = default)
        => _kv.DeleteAsync(key, ifMatch, unconditional);

    public Task<TxnResult> TxnAsync(IReadOnlyList<TxnCompare> compare, IReadOnlyList<TxnOp> success, IReadOnlyList<TxnOp> failure, CancellationToken ct = default)
        => _kv.TxnAsync(compare, success, failure);

    public Task<LeaseInfo> CreateLeaseAsync(long ttlSecs, CancellationToken ct = default)
        => _kv.CreateLeaseAsync(ttlSecs);

    public Task<long?> KeepAliveAsync(string id, CancellationToken ct = default)
        => _kv.KeepAliveAsync(id);

    public Task<bool> RevokeLeaseAsync(string id, CancellationToken ct = default)
        => _kv.RevokeLeaseAsync(id);

    public Task<LeaseView?> GetLeaseAsync(string id, CancellationToken ct = default)
        => Task.FromResult(_kv.GetLease(id));

    /// <summary>
    /// Rejects the watch: a <see cref="LocalStore"/> can only ever be woken by its own process's commits, so a
    /// live watch here would silently park on events another writer made. The failure is raised <em>eagerly</em>
    /// — synchronously, when this method is invoked, before any element is yielded or any wait is entered — so a
    /// caller cannot end up parked on a deferred async iterator. The unsupported transport always wins: the
    /// exception is thrown regardless of <paramref name="ct"/>, so the contract is deterministic rather than
    /// racing the token's state. Watch liveness is daemon-only (#202).
    /// </summary>
    /// <exception cref="TurnstileWatchUnavailableException">Always, because library mode has no cross-process signal.</exception>
    public IAsyncEnumerable<WatchMessage> WatchAsync(string prefix, long fromExclusive, CancellationToken ct = default)
        => throw new TurnstileWatchUnavailableException(
            "watch liveness requires a running Turnstile daemon; the direct SQLite store (library mode) "
            + "cannot observe another process's commits (start 'turnstile serve')");

    public void Dispose()
    {
        _kv.Dispose();
        _modeLock.Dispose();
    }
}
