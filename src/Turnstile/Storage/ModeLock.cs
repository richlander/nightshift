namespace Turnstile.Storage;

/// <summary>
/// The database-ownership contract (#202): a database is either owned <em>exclusively by one daemon</em> or
/// shared by any number of direct (library-mode) <see cref="LocalStore"/> processes — never both at once. That
/// exclusivity is what makes a daemon's watch genuinely live: with no direct store able to open the file behind
/// its back, every commit flows through the daemon's own <see cref="KvStore"/> and pulses the signal its
/// watchers park on. Without it, a direct write to the same file would commit and advance the revision while
/// pulsing only its own process-local signal, leaving a daemon watcher parked on a change it can never see.
///
/// <para>It is a thin, database-specific face over the shared <see cref="FileLock"/> primitive: a
/// <em>shared</em> flock for a <see cref="LocalStore"/> (many compatible, so multiple direct writers coexist —
/// #199) and an <em>exclusive</em> flock for a daemon (incompatible with any shared holder, so it excludes
/// every direct store). All of the syscall machinery — the non-blocking acquire, the <c>O_CLOEXEC</c> lock fd,
/// the automatic OS release on process exit, and the errno classification that keeps a real system failure from
/// masquerading as a conflict — lives in <see cref="FileLock"/> and is shared with <see cref="SocketLock"/>.
/// This type adds only the two things that are database-specific: the sidecar name (<c>&lt;db&gt;-modelock</c>)
/// and the typed <see cref="TurnstileDatabaseInUseException"/> a conflict raises.</para>
///
/// <para>The sidecar name is derived from the database's <em>canonical</em> path, which the caller resolves once
/// through <see cref="DatabasePath.Canonicalize"/> and hands to both this lock and <see cref="KvStore.Open"/>.
/// That is what makes the lock name the same file SQLite opens even when the database is reached through an
/// ordinary symlink alias (a symlinked file, or a file under a symlinked directory): without canonicalization
/// two aliases of one inode would take two different sidecars and a direct writer could commit behind a daemon's
/// live watch. This lock does <em>not</em> recanonicalize; passing it a non-canonical path is a caller error.</para>
/// </summary>
internal sealed class ModeLock : IDisposable
{
    private readonly FileLock _lock;

    private ModeLock(FileLock fileLock) => _lock = fileLock;

    /// <summary>The sidecar path whose lock guards <paramref name="canonicalDbPath"/>. The argument must already
    /// be the database's canonical filesystem identity from <see cref="DatabasePath.Canonicalize"/> — the sidecar
    /// name is derived from it verbatim rather than recanonicalized lexically here, so a symlink alias of a file
    /// takes the very sidecar its canonical path names (not a second lock beside a different name for the same
    /// inode). Resolving once in the caller and passing the same string to both this lock and
    /// <see cref="KvStore.Open"/> also keeps a single open from resolving the path twice and racing a swap.</summary>
    public static string SidecarPath(string canonicalDbPath) => canonicalDbPath + "-modelock";

    /// <summary>
    /// Takes the shared lock for a direct <see cref="LocalStore"/>. Succeeds alongside other direct stores;
    /// fails with <see cref="TurnstileDatabaseInUseException"/> if a daemon exclusively owns the database.
    /// </summary>
    public static ModeLock AcquireShared(string dbPath) => Acquire(
        dbPath,
        exclusive: false,
        $"database '{dbPath}' is owned by a running Turnstile daemon; connect through its socket rather than "
        + "opening the file directly (start or reach 'turnstile serve'). Direct library-mode access is refused "
        + "while a daemon owns the database so watch liveness holds (#202).");

    /// <summary>
    /// Takes the exclusive lock for a daemon. Fails with <see cref="TurnstileDatabaseInUseException"/> if any
    /// direct <see cref="LocalStore"/> currently holds the database.
    /// </summary>
    public static ModeLock AcquireExclusive(string dbPath) => Acquire(
        dbPath,
        exclusive: true,
        $"cannot start the daemon: database '{dbPath}' is already in use — either another daemon already owns "
        + "it, or one or more direct (library-mode) Turnstile clients hold it. A daemon needs exclusive "
        + "ownership so its watch stays live; stop the other owner and retry (#202).");

    private static ModeLock Acquire(string dbPath, bool exclusive, string conflictMessage)
    {
        // A null return is the sole ownership-conflict (EWOULDBLOCK) case, which becomes the typed database
        // exception; every other failure surfaces from FileLock as an IOException carrying its errno.
        FileLock? held = FileLock.TryAcquire(SidecarPath(dbPath), exclusive);
        if (held is null)
        {
            throw new TurnstileDatabaseInUseException(conflictMessage);
        }

        return new ModeLock(held);
    }

    /// <summary>Releases the lock (the OS drops the flock when the descriptor closes). Idempotent.</summary>
    public void Dispose() => _lock.Dispose();
}
