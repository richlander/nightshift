namespace Turnstile.Storage;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Socket-<em>endpoint</em> ownership for a daemon (#212). <see cref="ModeLock"/> protects the database a daemon
/// owns, but not the Unix socket path it binds. Those are independent: a second daemon started on the same
/// socket but a <em>different</em> database takes a different <see cref="ModeLock"/> (no conflict), and — under
/// the old startup — would unconditionally unlink the first daemon's socket and bind its own. Unix lets a
/// listening socket be unlinked without disturbing existing connections, so the two daemons would then split
/// coordination state: old connections stay on the first daemon and its database, new clients reach the second.
///
/// <para>This closes that with two independent guards, both applied <em>before</em> any socket is deleted or
/// bound:</para>
/// <list type="number">
///   <item><b>An exclusive <see cref="FileLock"/> on the socket's canonical identity</b> (sidecar
///   <c>&lt;socket&gt;-socklock</c>). Two daemons that would bind the same endpoint — even through different
///   path spellings, because the identity is canonicalized (<see cref="CanonicalPath"/>) — contend on this one
///   lock, so the second fails fast with <see cref="TurnstileSocketInUseException"/> and never reaches the
///   delete. It reuses the very same close-on-exec, OS-release, non-blocking flock machinery the database lock
///   uses; only the sidecar name and the typed exception differ.</item>
///   <item><b>A liveness probe before any delete.</b> The lock alone cannot protect an <em>older</em> daemon
///   (built before this lock existed) or any foreign listener that holds no socklock — it would acquire the
///   free lock and then delete a live endpoint. So once the lock is held, if the socket path still exists, a
///   single connect probes it: a successful connect means a listener is live (Turnstile or not) and startup
///   <em>refuses</em> rather than unlink it; only a probe that proves the pathname stale
///   (<c>ENOENT</c>/<c>ECONNREFUSED</c>-class — nothing is listening) is deleted. Any other probe outcome
///   (<c>EACCES</c>, a non-socket at the path, ...) surfaces as an <see cref="IOException"/>, never silently
///   treated as stale. No polling: one connect attempt, then a decision.</item>
/// </list>
///
/// <para>The daemon acquires this <em>before</em> the database <see cref="ModeLock"/>, in one documented
/// non-blocking order; both locks are non-blocking, so no acquisition can deadlock, and both are released on
/// any startup failure and after shutdown. Malicious host-local tampering (a hostile hardlink, a path swapped
/// after resolution) is out of scope, exactly as for <see cref="FileLock"/>/<see cref="CanonicalPath"/>: this
/// follows #202's supported-symlink contract, not a security boundary.</para>
/// </summary>
internal sealed partial class SocketLock : IDisposable
{
    // socket(2)/connect(2) constants for the liveness probe. AF_UNIX and SOCK_STREAM are both 1 on Linux and
    // macOS. The probe speaks the same transport a client would, so "a listener answers" is exactly what a
    // client would find — the honest test of liveness.
    private const int AfUnix = 1;
    private const int SockStream = 1;

    // errno for the probe. EINTR (4) and ENOENT (2) are identical on Linux and macOS; ECONNREFUSED differs
    // (111 on Linux, 61 on macOS). ENOENT ("nothing at the path") and ECONNREFUSED ("a socket file with no
    // listener") are the two stale outcomes that authorize deleting the pathname; every other errno is a real
    // fault that must surface rather than be mistaken for stale.
    private const int Eintr = 4;
    private const int Enoent = 2;
    private static readonly int EConnRefused = OperatingSystem.IsMacOS() ? 61 : 111;

    // sockaddr_un layout differs by platform: macOS has a leading 1-byte sun_len then a 1-byte sun_family;
    // Linux has a 2-byte sun_family and no sun_len. On both, AF_UNIX is 1 and the pathname follows at offset 2,
    // NUL-terminated. macOS caps sun_path at 104 bytes, Linux at 108.
    private const int SunPathOffset = 2;
    private static readonly int SunPathMax = OperatingSystem.IsMacOS() ? 104 : 108;

    private readonly FileLock _lock;

    private SocketLock(FileLock fileLock) => _lock = fileLock;

    /// <summary>The sidecar path whose lock guards the socket endpoint named by <paramref name="canonicalSocketPath"/>.
    /// The argument must already be the socket's canonical filesystem identity from
    /// <see cref="CanonicalPath.Resolve"/>, so two path spellings that reach the same endpoint take the one
    /// sidecar rather than two locks beside different names for the same socket.</summary>
    public static string SidecarPath(string canonicalSocketPath) => canonicalSocketPath + "-socklock";

    /// <summary>
    /// Takes exclusive ownership of the socket endpoint named by <paramref name="canonicalSocketPath"/>, then
    /// makes <paramref name="socketPath"/> safe to bind: a live endpoint there is refused (never unlinked) and
    /// only a proven-stale pathname is deleted. Fails with <see cref="TurnstileSocketInUseException"/> if
    /// another daemon already owns the endpoint or a live listener answers; other probe faults surface as
    /// <see cref="IOException"/>. On any failure no lock is retained and no live socket is deleted.
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
            ClearStalePathOrRefuse(socketPath);
            return new SocketLock(held);
        }
        catch
        {
            // Release the just-acquired lock on any failure (a live-endpoint refusal, or a probe fault) so a
            // failed start leaves nothing held.
            held.Dispose();
            throw;
        }
    }

    // Makes socketPath bindable now that the endpoint lock is held. If nothing is at the path there is nothing
    // to clear. Otherwise probe once: a live listener is refused (unlinking it would strand its connections —
    // the #212 bug), a stale pathname is deleted so the daemon can bind, and any other outcome surfaces.
    private static void ClearStalePathOrRefuse(string socketPath)
    {
        if (!File.Exists(socketPath))
        {
            return;
        }

        switch (Probe(socketPath))
        {
            case ProbeResult.Live:
                throw new TurnstileSocketInUseException(
                    $"cannot start the daemon: a live listener already answers on the socket '{socketPath}'. "
                    + "Refusing to replace it — an existing daemon (or another process) is serving there. Stop "
                    + "it or serve on a different --socket (#212).");

            case ProbeResult.Stale:
                // The pathname is a leftover with no listener (a crash left the file, or the path vanished).
                // Deleting it is what lets a daemon restart on the same socket after an unclean exit.
                File.Delete(socketPath);
                break;
        }
    }

    private enum ProbeResult
    {
        Live,
        Stale,
    }

    // Connects once to socketPath over AF_UNIX/SOCK_STREAM and classifies the outcome. A successful connect is
    // Live (a listener queued us — no accept() by the peer is needed, so this cannot hang on a live-but-busy
    // server). ENOENT/ECONNREFUSED is Stale (nothing is listening). Any other errno — EACCES, ENOTSOCK (a
    // non-socket file sits at the path), ... — is a real fault the caller must see, so it throws rather than
    // guess "stale" and unlink something it does not understand. EINTR is retried, matching FileLock.
    private static ProbeResult Probe(string socketPath)
    {
        byte[] addr = BuildSockaddrUn(socketPath, out uint addrLen);

        int fd = socket(AfUnix, SockStream, 0);
        if (fd < 0)
        {
            throw new IOException(
                $"turnstile: cannot create a probe socket for '{socketPath}' (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            while (true)
            {
                if (connect(fd, addr, addrLen) == 0)
                {
                    return ProbeResult.Live;
                }

                int err = Marshal.GetLastPInvokeError();
                if (err == Eintr)
                {
                    continue;
                }

                if (err == Enoent || err == EConnRefused)
                {
                    return ProbeResult.Stale;
                }

                throw new IOException(
                    $"turnstile: cannot probe the socket '{socketPath}' before binding (errno {err}); refusing to "
                    + "treat an unclassified endpoint as stale");
            }
        }
        finally
        {
            close(fd);
        }
    }

    // Packs a pathname AF_UNIX sockaddr_un for connect(2), honouring the per-platform header layout (macOS:
    // sun_len + 1-byte family; Linux: 2-byte family). The address length passed to connect covers the header,
    // the path bytes, and the terminating NUL. A path too long for the platform's sun_path is a real error —
    // it could never be bound either — so it surfaces rather than being silently truncated into a different
    // endpoint.
    private static byte[] BuildSockaddrUn(string socketPath, out uint addrLen)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(socketPath);
        if (pathBytes.Length + 1 > SunPathMax)
        {
            throw new IOException(
                $"turnstile: socket path '{socketPath}' is too long for a Unix socket address "
                + $"({pathBytes.Length} bytes; max {SunPathMax - 1})");
        }

        addrLen = (uint)(SunPathOffset + pathBytes.Length + 1);
        byte[] addr = new byte[addrLen];
        if (OperatingSystem.IsMacOS())
        {
            addr[0] = (byte)addrLen;
            addr[1] = AfUnix;
        }
        else
        {
            addr[0] = AfUnix;
            addr[1] = 0;
        }

        Array.Copy(pathBytes, 0, addr, SunPathOffset, pathBytes.Length);
        return addr;
    }

    /// <summary>Releases socket-endpoint ownership (the OS drops the flock when the descriptor closes).
    /// Idempotent. Does not unlink the bound socket file — Kestrel owns that.</summary>
    public void Dispose() => _lock.Dispose();

    [LibraryImport("libc", SetLastError = true)]
    private static partial int socket(int domain, int type, int protocol);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int connect(int fd, byte[] addr, uint addrLen);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);
}
