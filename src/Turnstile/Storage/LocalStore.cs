namespace Turnstile.Storage;

/// <summary>
/// Library-mode <see cref="ITurnstile"/>: opens the SQLite file directly and adapts <see cref="KvStore"/>.
/// This is the degraded-but-useful path — no daemon, no always-on sweeper. Correctness of lease expiry
/// is therefore <em>eventual</em>: <see cref="OpenAsync"/> sweeps once on open so a leaked lock from a
/// dead process is reclaimed the next time any process touches the store. WAL makes this safe alongside
/// a running daemon against the same file.
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

    public IAsyncEnumerable<WatchMessage> WatchAsync(string prefix, long fromExclusive, CancellationToken ct = default)
        => _kv.WatchAsync(prefix, fromExclusive, ct);

    public void Dispose() => _kv.Dispose();
}
