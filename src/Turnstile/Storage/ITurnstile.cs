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
    /// <remarks>
    /// Watch <em>liveness</em> is daemon-only. A <see cref="LocalStore"/> (library mode) has only a
    /// process-local change signal and cannot be woken by another process's commit to the same file, so it
    /// rejects this call up front with a <see cref="TurnstileWatchUnavailableException"/> rather than
    /// yielding a stream that could park forever on events it will never see (#202). Only the daemon
    /// transport delivers a live watch.
    /// </remarks>
    IAsyncEnumerable<WatchMessage> WatchAsync(string prefix, long fromExclusive, CancellationToken ct = default);
}

/// <summary>
/// The requested capability cannot be served by the transport the caller reached, and retrying it there
/// cannot succeed — it is a narrowed contract, not a transient error. Two conditions raise it, both rooted in
/// the daemon-only watch contract (#202): a database-ownership conflict (a running daemon exclusively owns the
/// file, or a direct library-mode store already holds it when a daemon tries to start — see
/// <see cref="TurnstileDatabaseInUseException"/>), and the eager watch rejection on a direct store (see
/// <see cref="TurnstileWatchUnavailableException"/>). Product surfaces map this to the established non-success
/// convention — a first-line <c>turnstile:</c> message and exit 1 — never a stack trace, because the condition
/// is expected.
/// </summary>
public class TurnstileUnavailableException(string message) : Exception(message);

/// <summary>
/// Thrown when a watch is requested on a transport that cannot deliver live cross-process notification —
/// the direct-SQLite <see cref="LocalStore"/> (library mode). Each store instance owns an in-memory,
/// per-process change signal, so a watcher there is never woken by another process's commit to the same
/// file (#202). Watch liveness is therefore daemon-only: a caller that needs a live watch must connect
/// through a running Turnstile daemon (<c>turnstile serve</c>). This is a narrowed contract, not a
/// transient failure — retrying against the same direct store cannot succeed.
/// </summary>
public sealed class TurnstileWatchUnavailableException(string message) : TurnstileUnavailableException(message);

/// <summary>
/// Thrown when the cross-process mode lock refuses a store open because the database's ownership contract
/// (#202) would be violated. Two directions raise it: a <see cref="LocalStore"/> tried to open a database a
/// running daemon exclusively owns (callers must use the daemon's socket instead), or a daemon tried to start
/// on a database a direct library-mode store already holds (the daemon needs exclusive ownership so its watch
/// stays the only writer a watcher can miss). Multiple direct stores against one database — with no daemon —
/// stay allowed (#199); this only fires across the direct/daemon boundary.
/// </summary>
public sealed class TurnstileDatabaseInUseException(string message) : TurnstileUnavailableException(message);

/// <summary>
/// Thrown when a daemon cannot take ownership of its socket <em>endpoint</em> (#212). Database ownership
/// (<see cref="TurnstileDatabaseInUseException"/>) does not cover this: a second daemon on the same socket but
/// a <em>different</em> database takes a different mode lock and would otherwise unlink the first daemon's live
/// socket and bind its own, leaving existing connections on the first daemon while new clients reach the
/// second — a split coordination state. Two conditions raise it, both before any socket is deleted or bound: a
/// socket-ownership conflict (another daemon already holds the <see cref="SocketLock"/> for the same canonical
/// socket identity), and a live-endpoint refusal (a listener — Turnstile or not — answers a connect on the
/// socket path, so it is not stale and must never be unlinked). A genuinely stale pathname is not this: it is
/// deleted and startup proceeds. Product surfaces map this to the established non-success convention — a
/// first-line <c>turnstile:</c> message and exit 1 — never a stack trace, because the condition is expected.
/// </summary>
public sealed class TurnstileSocketInUseException(string message) : TurnstileUnavailableException(message);
