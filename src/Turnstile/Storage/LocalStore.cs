namespace Turnstile.Storage;

/// <summary>
/// Library-mode <see cref="ITurnstile"/>: opens the SQLite file directly and adapts <see cref="KvStore"/>.
/// This is the degraded-but-useful path — no daemon, no always-on sweeper. Correctness of lease expiry
/// is therefore <em>eventual</em>: <see cref="OpenAsync"/> sweeps once on open so a leaked lock from a
/// dead process is reclaimed the next time any process touches the store.
///
/// WAL plus globally serialized revision allocation (each writer reads the durable committed revision under
/// BEGIN IMMEDIATE, never a cached value) makes this safe alongside a running daemon or another instance
/// against the same file <em>for storage integrity</em>: revisions stay globally unique, monotonic and
/// gapless, and every committed row persists (#199). It does <em>not</em> make watch notification
/// cross-process: each instance's change signal is in-memory, so a watcher here could never be woken by a
/// commit another instance made. Rather than expose a watch that can silently park on a process-local signal,
/// <see cref="WatchAsync"/> rejects up front with a <see cref="TurnstileWatchUnavailableException"/> —
/// watch liveness is daemon-only (#202). Finite reads/writes/txns/leases and storage integrity remain fully
/// useful here; only the live watch requires the daemon.
/// </summary>
public sealed class LocalStore : ITurnstile
{
    private readonly KvStore _kv;

    private LocalStore(KvStore kv) => _kv = kv;

    /// <summary>Opens the store at <paramref name="dbPath"/> and sweeps expired leases once (sweep-on-open).</summary>
    public static async Task<LocalStore> OpenAsync(string dbPath)
    {
        KvStore kv = KvStore.Open(dbPath);
        // Sweep-on-open: library mode has no always-on sweeper, so reclaim any leases that expired while
        // no process was attached. This emits the delete events a lazy read would silently swallow.
        await kv.SweepExpiredAsync().ConfigureAwait(false);
        return new LocalStore(kv);
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

    public void Dispose() => _kv.Dispose();
}
