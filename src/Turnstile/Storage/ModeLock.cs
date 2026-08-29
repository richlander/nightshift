namespace Turnstile.Storage;

using System.Runtime.InteropServices;

/// <summary>
/// The cross-process lock that enforces Turnstile's database-ownership contract (#202): a database is either
/// owned <em>exclusively by one daemon</em> or shared by any number of direct (library-mode)
/// <see cref="LocalStore"/> processes — never both at once. That exclusivity is what makes a daemon's watch
/// genuinely live: with no direct store able to open the file behind its back, every commit flows through the
/// daemon's own <see cref="KvStore"/> and pulses the signal its watchers park on. Without this lock, a direct
/// write to the same file would commit and advance the revision while pulsing only its own process-local
/// signal, leaving a daemon watcher parked on a change it can never see.
///
/// <para>It is a BSD <c>flock(2)</c> advisory lock on a sidecar file next to the database
/// (<c>&lt;db&gt;-modelock</c>): a <em>shared</em> lock for a <see cref="LocalStore"/> (many compatible, so
/// multiple direct writers coexist — #199) and an <em>exclusive</em> lock for a daemon (incompatible with any
/// shared holder, so it excludes every direct store). The lock lives on an open file description, so the OS
/// releases it automatically when the process exits or crashes — a dead owner never wedges the database. That
/// property only holds if no <em>other</em> process keeps the description open, so the lock fd is opened
/// <c>O_CLOEXEC</c>: a child this process spawns never inherits it and so can never keep a dead owner's flock
/// alive. Both acquisitions are non-blocking: a conflict fails immediately with
/// <see cref="TurnstileDatabaseInUseException"/> rather than parking, which is how the contract surfaces as a
/// visible error instead of a silent hang. Only a real ownership conflict (<c>EWOULDBLOCK</c>) maps to that
/// exception; any other <c>flock</c>/<c>open</c> failure surfaces as an <see cref="IOException"/> with its
/// errno, never dressed up as a conflict.</para>
///
/// <para>Turnstile already requires Unix-domain sockets, so a Unix-only <c>flock</c> is in-scope. The P/Invoke
/// is source-generated (<see cref="LibraryImport"/>), so it stays NativeAOT- and trim-safe with no added
/// dependency. Host-local tampering with the sidecar file is out of scope: this is coordination correctness,
/// not security containment.</para>
/// </summary>
internal sealed partial class ModeLock : IDisposable
{
    // flock(2) operations. BSD-derived and identical on Linux and macOS.
    private const int LockSh = 1;
    private const int LockEx = 2;
    private const int LockNb = 4;

    // open(2) flag. O_RDWR is 0x0002 on both Linux and macOS. The sidecar is opened without O_CREAT — whose
    // value differs between the two platforms, and which as a variadic third `mode` argument is passed
    // incorrectly through a fixed-arity P/Invoke on arm64 — and created instead through fixed-arity creat(2)
    // only when it is genuinely absent (see EnsureOpen), so no variadic call is ever made.
    private const int ORdwr = 2;

    // open(2) O_CLOEXEC: set close-on-exec atomically at open time so the retained lock fd is never inherited
    // by a child this process spawns. Without it a child would keep the flock's open file description alive
    // after the owner exits/crashes, contradicting the OS-release property and potentially wedging a later
    // daemon start. Set at open() rather than with a follow-up fcntl(F_SETFD, FD_CLOEXEC) because FD_CLOEXEC is
    // a *variadic* fcntl argument, and a fixed-arity P/Invoke mispasses it on arm64 macOS (where variadic args
    // travel on the stack) — the same ABI trap that keeps O_CREAT off the open() path. The flag value differs
    // by platform: 0x0008_0000 on Linux, 0x0100_0000 on macOS.
    private static readonly int OCloexec = OperatingSystem.IsMacOS() ? 0x0100_0000 : 0x0008_0000;

    // fcntl(2) F_GETFD command and the FD_CLOEXEC bit it returns. Both are 1 on Linux and macOS. F_GETFD takes
    // no meaningful variadic argument, so — unlike F_SETFD — it is safe to read back through a fixed-arity
    // P/Invoke on every ABI; it is used only to verify O_CLOEXEC actually took.
    private const int FGetfd = 1;
    private const int FdCloexec = 1;

    // Sidecar creation mode: 0644. creat(2) is fixed-arity, so this passes correctly on every ABI (unlike a
    // variadic open + O_CREAT); the owner keeps read+write so a later O_RDWR open never trips on permissions.
    private const int CreateMode = 0x1A4;

    // errno. EINTR is 4 on both Linux and macOS; a non-blocking flock can still be interrupted by a signal,
    // so that one case is retried rather than mistaken for a conflict.
    private const int Eintr = 4;

    // errno. ENOENT is 2 on both Linux and macOS: the *only* open(2) failure that may be answered by creating
    // the sidecar. Any other open error is a real fault and must surface, not be masked by a creat() attempt.
    private const int Enoent = 2;

    // errno. EWOULDBLOCK (== EAGAIN) is the only flock(LOCK_NB) failure that means "a conflicting holder owns
    // the database". It is 11 on Linux and 35 on macOS. Every other errno is a genuine system failure.
    private static readonly int EWouldBlock = OperatingSystem.IsMacOS() ? 35 : 11;

    private int _fd;

    private ModeLock(int fd) => _fd = fd;

    /// <summary>The sidecar path whose lock guards <paramref name="dbPath"/>. Absolute, so the lock is stable
    /// regardless of the caller's working directory.</summary>
    public static string SidecarPath(string dbPath) => Path.GetFullPath(dbPath) + "-modelock";

    /// <summary>
    /// Takes the shared lock for a direct <see cref="LocalStore"/>. Succeeds alongside other direct stores;
    /// fails with <see cref="TurnstileDatabaseInUseException"/> if a daemon exclusively owns the database.
    /// </summary>
    public static ModeLock AcquireShared(string dbPath) => Acquire(
        dbPath,
        LockSh,
        $"database '{dbPath}' is owned by a running Turnstile daemon; connect through its socket rather than "
        + "opening the file directly (start or reach 'turnstile serve'). Direct library-mode access is refused "
        + "while a daemon owns the database so watch liveness holds (#202).");

    /// <summary>
    /// Takes the exclusive lock for a daemon. Fails with <see cref="TurnstileDatabaseInUseException"/> if any
    /// direct <see cref="LocalStore"/> currently holds the database.
    /// </summary>
    public static ModeLock AcquireExclusive(string dbPath) => Acquire(
        dbPath,
        LockEx,
        $"cannot start the daemon: database '{dbPath}' is already in use — either another daemon already owns "
        + "it, or one or more direct (library-mode) Turnstile clients hold it. A daemon needs exclusive "
        + "ownership so its watch stays live; stop the other owner and retry (#202).");

    private static ModeLock Acquire(string dbPath, int mode, string conflictMessage)
    {
        string path = SidecarPath(dbPath);

        int fd = EnsureOpen(path, out int openErrno);
        if (fd < 0)
        {
            throw new IOException(
                $"turnstile: cannot open mode-lock sidecar '{path}' (errno {openErrno})");
        }

        int op = mode | LockNb;
        while (true)
        {
            if (flock(fd, op) == 0)
            {
                return new ModeLock(fd);
            }

            int err = Marshal.GetLastPInvokeError();
            if (err == Eintr)
            {
                continue;
            }

            // The fd is closed before either throw so no descriptor leaks on the failure path. Only
            // EWOULDBLOCK/EAGAIN — the sole errno flock(LOCK_NB) raises for an incompatible holder — is the
            // ownership conflict the contract exists to report. Every other errno (EBADF, EINVAL, ENOLCK,
            // EOPNOTSUPP/ENOTSUP, EIO, ...) is a genuine system failure; reporting it as a conflict would tell
            // a caller "someone else owns the database" when the lock is simply broken, so it surfaces as an
            // IOException carrying the errno instead.
            close(fd);
            if (err == EWouldBlock)
            {
                throw new TurnstileDatabaseInUseException(conflictMessage);
            }

            throw new IOException(
                $"turnstile: cannot acquire mode-lock on sidecar '{path}' via flock (errno {err})");
        }
    }

    // Opens the sidecar for locking, creating it if absent, without ever touching a managed FileStream. That
    // matters: on Unix .NET's FileStream takes its own flock to honour FileShare, so opening the sidecar
    // through it would spuriously collide with a real owner's lock (a daemon's LOCK_EX) and throw a raw
    // IOException before this code could report the clean, typed conflict. So the fd comes straight from
    // open(2): try the existing file first — open never checks the advisory lock, so this succeeds even while
    // an owner holds it — and only creat(2) a genuinely missing file. Racing creators just re-truncate a fresh
    // zero-byte inode; the flock above, not the open, is what actually arbitrates ownership.
    //
    // On failure it returns -1 and reports the errno through <paramref name="errno"/>, captured immediately
    // after the failing syscall so no interposed managed work can clobber it. Only ENOENT is answered by
    // creat(2); every other open error (EACCES on a read-only directory, EISDIR, EROFS, ENAMETOOLONG, ...) is
    // returned as-is rather than being masked by a creat() attempt that would fail confusingly or, worse,
    // succeed and hide the original fault.
    private static int EnsureOpen(string path, out int errno)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        int fd = OpenLockFd(path);
        if (fd >= 0)
        {
            errno = 0;
            return fd;
        }

        errno = Marshal.GetLastPInvokeError();
        if (errno != Enoent)
        {
            return -1;
        }

        int created = creat(path, CreateMode);
        if (created >= 0)
        {
            close(created);
        }

        fd = OpenLockFd(path);
        if (fd >= 0)
        {
            errno = 0;
            return fd;
        }

        errno = Marshal.GetLastPInvokeError();
        return -1;
    }

    // Opens the sidecar O_CLOEXEC and returns the retained lock fd, or -1 (with the marshaller's last error set
    // for the caller) if open(2) fails. On success it verifies the kernel actually set close-on-exec: the
    // retained lock fd must never be inheritable, or a spawned child that outlives this process would keep the
    // flock alive and break the OS-release property. O_CLOEXEC does this atomically at open time; the F_GETFD
    // read is defence-in-depth against a wrong platform constant, and because it is the retained-fd invariant
    // being violated (not a caller-recoverable open error) a failure throws rather than returning -1.
    private static int OpenLockFd(string path)
    {
        int fd = open(path, ORdwr | OCloexec);
        if (fd < 0)
        {
            return -1;
        }

        int flags = fcntl(fd, FGetfd, 0);
        if (flags < 0 || (flags & FdCloexec) == 0)
        {
            close(fd);
            throw new IOException(
                $"turnstile: mode-lock sidecar '{path}' opened without close-on-exec; refusing an inheritable lock fd");
        }

        return fd;
    }

    /// <summary>Releases the lock by closing the descriptor (the OS drops the flock with it). Idempotent.</summary>
    public void Dispose()
    {
        int fd = Interlocked.Exchange(ref _fd, -1);
        if (fd >= 0)
        {
            close(fd);
        }
    }

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int creat(string path, int mode);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int flock(int fd, int operation);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int fcntl(int fd, int command, int arg);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
