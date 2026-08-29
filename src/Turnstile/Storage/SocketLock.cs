namespace Turnstile.Storage;

/// <summary>
/// Socket-<em>endpoint</em> ownership for a daemon (#212). <see cref="ModeLock"/> protects the database a daemon
/// owns, but not the Unix socket path it binds. Those are independent: a second daemon started on the same
/// socket but a <em>different</em> database takes a different <see cref="ModeLock"/> (no conflict), and — under
/// the old startup — would unconditionally unlink the first daemon's socket and bind its own. Unix lets a
/// listening socket be unlinked without disturbing existing connections, so the two daemons would then split
/// coordination state: old connections stay on the first daemon and its database, new clients reach the second.
///
/// <para>This closes that with two mechanisms, both applied <em>before</em> any socket is bound and neither of
/// which ever deletes a socket path:</para>
/// <list type="number">
///   <item><b>An exclusive <see cref="FileLock"/> on the socket's canonical identity</b> (sidecar
///   <c>&lt;socket&gt;-socklock</c>). Two compliant daemons that would bind the same endpoint — even through
///   different path spellings, because the identity is canonicalized (<see cref="CanonicalPath"/>) — contend on
///   this one lock, so the second fails fast with <see cref="TurnstileSocketInUseException"/>. This both
///   serializes daemon startup and closes the absent-path race (two daemons starting on a fresh socket that
///   both see no file). It reuses the very same close-on-exec, OS-release, non-blocking flock machinery the
///   database lock uses; only the sidecar name and the typed exception differ.</item>
///   <item><b>A fail-closed existence check.</b> The lock alone cannot protect an <em>older</em> daemon (built
///   before this lock existed) or any foreign listener that holds no socklock — it would acquire the free lock
///   over a live endpoint. A daemon <em>cannot</em> prove a socket path is stale without cooperation from
///   whatever bound it: on macOS a saturated but live listener can refuse a connect, so no connect-only probe
///   can tell a crash leftover apart from a busy live server. So once the lock is held, if <em>any</em>
///   filesystem entry exists at the requested or canonical endpoint — a live listener, a stale socket, a
///   regular file, or a symlink — startup <em>refuses</em> with <see cref="TurnstileSocketInUseException"/> and
///   never unlinks it. Safety wins over crash-restart convenience: Turnstile will not silently delete a path
///   that might be live.</item>
/// </list>
///
/// <para><b>What still restarts cleanly, and what does not.</b> A graceful shutdown removes the daemon's own
/// socket (Kestrel unlinks it on unbind), so an ordinary restart finds nothing at the path and binds. An
/// <em>unclean</em> crash leaves the socket file behind; because staleness cannot be proven, that becomes an
/// explicit operator-cleanup condition — verify no process is listening, remove the path, and retry. There is
/// deliberately no <c>--force</c>, no automatic retry, and no polling. If an external process races to bind
/// after the existence check, that surfaces as a visible Kestrel bind failure and still deletes nothing;
/// compliant daemons cannot race because the lock serializes them.</para>
///
/// <para>The daemon acquires this <em>before</em> the database <see cref="ModeLock"/>, in one documented
/// non-blocking order; both locks are non-blocking, so no acquisition can deadlock, and both are released on
/// any startup failure and after shutdown. This is a thin, socket-specific face over <see cref="FileLock"/>,
/// exactly analogous to <see cref="ModeLock"/>: it adds only the sidecar name, the typed exception, and the
/// existence refusal. Malicious host-local tampering (a hostile hardlink, a path swapped after resolution) is
/// out of scope, exactly as for <see cref="FileLock"/>/<see cref="CanonicalPath"/>: this follows #202's
/// supported-symlink contract, not a security boundary.</para>
/// </summary>
internal sealed class SocketLock : IDisposable
{
    private readonly FileLock _lock;

    private SocketLock(FileLock fileLock) => _lock = fileLock;

    /// <summary>The sidecar path whose lock guards the socket endpoint named by <paramref name="canonicalSocketPath"/>.
    /// The argument must already be the socket's canonical filesystem identity from
    /// <see cref="CanonicalPath.Resolve"/>, so two path spellings that reach the same endpoint take the one
    /// sidecar rather than two locks beside different names for the same socket.</summary>
    public static string SidecarPath(string canonicalSocketPath) => canonicalSocketPath + "-socklock";

    /// <summary>
    /// Takes exclusive ownership of the socket endpoint named by <paramref name="canonicalSocketPath"/>, then
    /// verifies <paramref name="socketPath"/> is free to bind. Fails with <see cref="TurnstileSocketInUseException"/>
    /// if another daemon already owns the endpoint, or if <em>any</em> filesystem entry already exists at the
    /// requested or canonical path — Turnstile never unlinks an existing socket path, because it cannot prove
    /// the path is not a live listener. On any failure no lock is retained and no socket path is deleted.
    /// </summary>
    public static SocketLock Acquire(string socketPath, string canonicalSocketPath)
    {
        // A null return is the sole ownership-conflict (EWOULDBLOCK) case — another daemon holds this endpoint;
        // every other lock failure surfaces from FileLock as an IOException carrying its errno.
        FileLock? held = FileLock.TryAcquire(SidecarPath(canonicalSocketPath), exclusive: true);
        if (held is null)
        {
            throw new TurnstileSocketInUseException(
                $"cannot start the daemon: another Turnstile daemon already owns the socket endpoint "
                + $"'{socketPath}'. A second daemon on the same socket would split coordination state; stop the "
                + "other daemon or serve on a different --socket (#212).");
        }

        try
        {
            RefuseIfEndpointExists(socketPath, canonicalSocketPath);
            return new SocketLock(held);
        }
        catch
        {
            // Release the just-acquired lock on the existence refusal so a failed start leaves nothing held.
            held.Dispose();
            throw;
        }
    }

    // With the endpoint lock held, refuse to bind if anything at all already occupies the path. Turnstile
    // cannot prove such an entry is a crash leftover rather than a live listener — a connect-only probe cannot
    // distinguish a stale pathname from a saturated, uncooperative live server — so it never unlinks it. A
    // graceful shutdown removes the daemon's own socket, so a leftover here means an unclean exit or a foreign
    // process, and clearing it is the operator's explicit decision, not the daemon's.
    private static void RefuseIfEndpointExists(string socketPath, string canonicalSocketPath)
    {
        if (!PathExists(socketPath) && !PathExists(canonicalSocketPath))
        {
            return;
        }

        throw new TurnstileSocketInUseException(
            $"cannot start the daemon: a filesystem entry already exists at the socket path '{socketPath}'. "
            + "Turnstile refuses to unlink it because it cannot prove the path is not a live listener — a "
            + "graceful daemon shutdown removes its own socket, so a leftover means an unclean exit or another "
            + "process. Verify no daemon or other process is listening there, then remove the stale path "
            + "explicitly and retry, or serve on a different --socket (#212).");
    }

    // Reports whether any filesystem entry occupies <paramref name="path"/> — a regular file, a directory, a
    // Unix socket, or a symlink, *including a dangling one whose target is absent*. Path.Exists reports each of
    // those as present (it does not follow the final link, so a dangling symlink still reads as present); the
    // LinkTarget probe is a belt-and-braces confirmation of a dangling symlink on any platform whose Path.Exists
    // might miss it — a genuinely absent path yields a null LinkTarget with no error.
    //
    // The dangling-symlink *leaf* case is in fact already refused upstream: the daemon canonicalizes the socket
    // path through CanonicalPath.Resolve before ever calling Acquire, and that resolution rejects a dangling
    // final symlink (and any leaf whose parent will not realpath) with a visible IOException. That same
    // resolution also guarantees the parent directory exists and resolves here, which confines Path.Exists's
    // "false on many errors" behaviour to the leaf — absent versus present — so a dedicated lstat probe would be
    // a disproportionate seam for no additional safety.
    //
    // What is *not* acceptable is reading an inability to inspect the pathname as "absent". A false return from
    // this helper means "bind is free"; an IOException or UnauthorizedAccessException while probing the link is
    // neither absent nor free-to-bind, and swallowing it to false would turn "cannot tell" into "go ahead and
    // bind" — the exact fail-open the socket lock exists to prevent. So an inspection failure refuses closed,
    // surfaced as the same typed, operator-visible signal as an occupied path, preserving the underlying cause.
    private static bool PathExists(string path)
    {
        if (Path.Exists(path))
        {
            return true;
        }

        try
        {
            return new FileInfo(path).LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new TurnstileSocketInUseException(
                $"cannot start the daemon: the socket path '{path}' could not be inspected ({ex.Message}), so "
                + "Turnstile cannot prove it is free to bind. It refuses to continue rather than risk binding "
                + "over a live endpoint. Resolve the access error, or serve on a different --socket (#212).");
        }
    }

    /// <summary>Releases socket-endpoint ownership (the OS drops the flock when the descriptor closes).
    /// Idempotent. Does not unlink the bound socket file — Kestrel owns that.</summary>
    public void Dispose() => _lock.Dispose();
}
