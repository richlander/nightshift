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
/// releases it automatically when the process exits or crashes — a dead owner never wedges the database. Both
/// acquisitions are non-blocking: a conflict fails immediately with <see cref="TurnstileDatabaseInUseException"/>
/// rather than parking, which is how the contract surfaces as a visible error instead of a silent hang.</para>
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

    // Sidecar creation mode: 0644. creat(2) is fixed-arity, so this passes correctly on every ABI (unlike a
    // variadic open + O_CREAT); the owner keeps read+write so a later O_RDWR open never trips on permissions.
    private const int CreateMode = 0x1A4;

    // errno. EINTR is 4 on both Linux and macOS; a non-blocking flock can still be interrupted by a signal,
    // so that one case is retried rather than mistaken for a conflict.
    private const int Eintr = 4;

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

        int fd = EnsureOpen(path);
        if (fd < 0)
        {
            throw new IOException(
                $"turnstile: cannot open mode-lock sidecar '{path}' (errno {Marshal.GetLastPInvokeError()})");
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

            // With LOCK_NB the only expected non-EINTR failure is EWOULDBLOCK/EAGAIN — a conflicting holder.
            // The fd was just opened, so a genuine EBADF/EINVAL here is not reachable in practice; treat any
            // remaining failure as the ownership conflict rather than leak an open descriptor.
            close(fd);
            throw new TurnstileDatabaseInUseException(conflictMessage);
        }
    }

    // Opens the sidecar for locking, creating it if absent, without ever touching a managed FileStream. That
    // matters: on Unix .NET's FileStream takes its own flock to honour FileShare, so opening the sidecar
    // through it would spuriously collide with a real owner's lock (a daemon's LOCK_EX) and throw a raw
    // IOException before this code could report the clean, typed conflict. So the fd comes straight from
    // open(2): try the existing file first — open never checks the advisory lock, so this succeeds even while
    // an owner holds it — and only creat(2) a genuinely missing file. Racing creators just re-truncate a fresh
    // zero-byte inode; the flock above, not the open, is what actually arbitrates ownership.
    private static int EnsureOpen(string path)
    {
        string? dir = Path.GetDirectoryName(path);
        if (dir is { Length: > 0 })
        {
            Directory.CreateDirectory(dir);
        }

        int fd = open(path, ORdwr);
        if (fd >= 0)
        {
            return fd;
        }

        int created = creat(path, CreateMode);
        if (created >= 0)
        {
            close(created);
        }

        return open(path, ORdwr);
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
    private static partial int close(int fd);
}
