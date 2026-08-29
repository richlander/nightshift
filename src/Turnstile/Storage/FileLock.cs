namespace Turnstile.Storage;

using System.Runtime.InteropServices;

/// <summary>
/// The NativeAOT-safe cross-process lock primitive Turnstile builds its ownership contracts on: a BSD
/// <c>flock(2)</c> advisory lock on a sidecar file, taken non-blocking, held on an open file description, and
/// released automatically by the OS when the process exits or crashes. It is deliberately policy-free — it
/// knows nothing about databases or sockets. Two subsystems layer their own meaning on top of it:
/// <see cref="ModeLock"/> keys it on a database's canonical path to arbitrate the daemon/direct ownership
/// contract (#202), and <see cref="SocketLock"/> keys it on a socket's canonical path to arbitrate socket-
/// endpoint ownership (#212). Extracting the syscall machinery here means both take the very same, once-tested
/// close-on-exec and errno-classification behaviour rather than each duplicating the platform P/Invoke.
///
/// <para>A <em>shared</em> lock is compatible with other shared holders and incompatible with an exclusive one;
/// an <em>exclusive</em> lock is incompatible with any other holder. The lock lives on an open file
/// description, so the OS drops it when the owning process exits — a dead owner never wedges the resource. That
/// property only holds if no <em>other</em> process keeps the description open, so the lock fd is opened
/// <c>O_CLOEXEC</c>: a child this process spawns never inherits it and so can never keep a dead owner's flock
/// alive. Acquisition is non-blocking: a conflict returns <c>null</c> immediately (the caller maps that to its
/// own typed <see cref="TurnstileUnavailableException"/>) rather than parking, which is how an ownership
/// contract surfaces as a visible error instead of a silent hang. Only a real ownership conflict
/// (<c>EWOULDBLOCK</c>) is the <c>null</c> return; any other <c>flock</c>/<c>open</c> failure surfaces as an
/// <see cref="IOException"/> carrying its errno, never dressed up as a conflict.</para>
///
/// <para>Turnstile already requires Unix-domain sockets, so a Unix-only <c>flock</c> is in-scope. The P/Invoke
/// is source-generated (<see cref="LibraryImport"/>), so it stays NativeAOT- and trim-safe with no added
/// dependency. Host-local tampering with the sidecar file is out of scope: this is coordination correctness,
/// not security containment.</para>
///
/// <para>The sidecar name is derived by the caller from a resource's <em>canonical</em> path, so a symlink
/// alias of one resource takes the very sidecar its canonical path names. This primitive does <em>not</em>
/// recanonicalize; passing it a non-canonical path is a caller error.</para>
/// </summary>
internal sealed partial class FileLock : IDisposable
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
    // acquire. Set at open() rather than with a follow-up fcntl(F_SETFD, FD_CLOEXEC) because FD_CLOEXEC is
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
    // the resource". It is 11 on Linux and 35 on macOS. Every other errno is a genuine system failure.
    private static readonly int EWouldBlock = OperatingSystem.IsMacOS() ? 35 : 11;

    private int _fd;

    private FileLock(int fd) => _fd = fd;

    /// <summary>
    /// Takes the lock on <paramref name="sidecarPath"/> — shared when <paramref name="exclusive"/> is false,
    /// exclusive when true — non-blocking. Returns the held lock on success, or <c>null</c> when a conflicting
    /// holder already owns it (the sole <c>EWOULDBLOCK</c> case). Any other failure — a sidecar that cannot be
    /// opened for a reason other than "absent", or a lock fd the kernel failed to make close-on-exec — throws
    /// an <see cref="IOException"/> carrying its errno rather than being flattened into a false "conflict".
    /// </summary>
    public static FileLock? TryAcquire(string sidecarPath, bool exclusive)
    {
        int fd = EnsureOpen(sidecarPath, out int openErrno);
        if (fd < 0)
        {
            throw new IOException(
                $"turnstile: cannot open lock sidecar '{sidecarPath}' (errno {openErrno})");
        }

        int op = (exclusive ? LockEx : LockSh) | LockNb;
        while (true)
        {
            if (flock(fd, op) == 0)
            {
                return new FileLock(fd);
            }

            int err = Marshal.GetLastPInvokeError();
            if (err == Eintr)
            {
                continue;
            }

            // The fd is closed before returning/throwing so no descriptor leaks on the failure path. Only
            // EWOULDBLOCK/EAGAIN — the sole errno flock(LOCK_NB) raises for an incompatible holder — is the
            // ownership conflict a caller maps to its typed exception. Every other errno (EBADF, EINVAL,
            // ENOLCK, EOPNOTSUPP/ENOTSUP, EIO, ...) is a genuine system failure; reporting it as a conflict
            // would tell a caller "someone else owns this" when the lock is simply broken, so it surfaces as an
            // IOException carrying the errno instead.
            close(fd);
            if (err == EWouldBlock)
            {
                return null;
            }

            throw new IOException(
                $"turnstile: cannot acquire lock on sidecar '{sidecarPath}' via flock (errno {err})");
        }
    }

    // Opens the sidecar for locking, creating it if absent, without ever touching a managed FileStream. That
    // matters: on Unix .NET's FileStream takes its own flock to honour FileShare, so opening the sidecar
    // through it would spuriously collide with a real owner's lock (an exclusive holder) and throw a raw
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
                $"turnstile: lock sidecar '{path}' opened without close-on-exec; refusing an inheritable lock fd");
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
