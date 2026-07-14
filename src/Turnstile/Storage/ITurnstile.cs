namespace Turnstile.Storage;

/// <summary>
/// The store surface shared by every helper and controller. Satisfied by <see cref="LocalStore"/>
/// (opens the SQLite file directly — library mode) or by a remote client that talks to a running
/// daemon over the socket. Callers take an <see cref="ITurnstile"/> and never know which they hold.
/// </summary>
public interface ITurnstile : IDisposable
{
    /// <summary>The highest committed revision.</summary>
    Task<long> GetRevisionAsync(CancellationToken ct = default);

    /// <summary>Returns the live state of a key, or null if it does not exist.</summary>
    Task<KeyState?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Lists live keys under a prefix in key order.</summary>
    Task<IReadOnlyList<KeyState>> RangeAsync(string prefix, int limit = 0, bool keysOnly = false, CancellationToken ct = default);

    /// <summary>Creates a key; fails if it already exists.</summary>
    Task<WriteResult> CreateAsync(string key, byte[] value, bool immutable = false, string? lease = null, CancellationToken ct = default);

    /// <summary>Updates a key, guarded by <paramref name="ifMatch"/> (mod_revision) unless <paramref name="unconditional"/>.</summary>
    Task<WriteResult> UpdateAsync(string key, byte[] value, long? ifMatch, bool unconditional = false, CancellationToken ct = default);

    /// <summary>Deletes a key, guarded by <paramref name="ifMatch"/> (mod_revision) unless <paramref name="unconditional"/>.</summary>
    Task<WriteResult> DeleteAsync(string key, long? ifMatch, bool unconditional = false, CancellationToken ct = default);

    /// <summary>Runs a transaction: evaluate <paramref name="compare"/>, then apply the success or failure branch.</summary>
    Task<TxnResult> TxnAsync(IReadOnlyList<TxnCompare> compare, IReadOnlyList<TxnOp> success, IReadOnlyList<TxnOp> failure, CancellationToken ct = default);

    /// <summary>Grants a lease with the given TTL in seconds.</summary>
    Task<LeaseInfo> CreateLeaseAsync(long ttlSecs, CancellationToken ct = default);

    /// <summary>Refreshes a lease's deadline; returns the remaining TTL in seconds, or null if the lease is gone.</summary>
    Task<long?> KeepAliveAsync(string id, CancellationToken ct = default);

    /// <summary>Revokes a lease and deletes its attached keys. Returns false if the lease did not exist.</summary>
    Task<bool> RevokeLeaseAsync(string id, CancellationToken ct = default);

    /// <summary>Returns a lease's current state and attached keys, or null if it does not exist.</summary>
    Task<LeaseView?> GetLeaseAsync(string id, CancellationToken ct = default);

    /// <summary>
    /// Streams the change log under <paramref name="prefix"/> after <paramref name="fromExclusive"/>:
    /// backlog events, a one-shot <see cref="WatchSyncMessage"/> when caught up, then live events.
    /// </summary>
    IAsyncEnumerable<WatchMessage> WatchAsync(string prefix, long fromExclusive, CancellationToken ct = default);
}
