namespace Turnstile.Tests;

using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Fabricates a persistent, listenerless Unix socket file for #212 tests: <c>bind(2)</c> then <c>close(2)</c>
/// with no <c>unlink</c>, leaving the pathname on disk with nothing listening — the "crash leftover" a daemon
/// must now <em>refuse</em> (fail closed) rather than silently clear, because it cannot prove the path is not a
/// live listener. .NET's <see cref="System.Net.Sockets.Socket"/> cannot stand in here: it unlinks the socket
/// file on <c>Dispose</c>, so a bound-then-closed managed socket leaves <em>no</em> file for the daemon to find
/// — the opposite of the case under test. A raw <c>bind</c>+<c>close</c> is the honest reproduction. macOS and
/// Linux differ only in the <c>sockaddr_un</c> header (macOS: a leading <c>sun_len</c> byte then a 1-byte
/// family; Linux: a 2-byte family), handled below.
///
/// <para>This uses classic <see cref="DllImportAttribute"/> rather than the product's source-generated
/// <see cref="LibraryImportAttribute"/> so the (non-AOT) test project needs no <c>AllowUnsafeBlocks</c>; the
/// interop is test-only scaffolding, never a product path.</para>
/// </summary>
internal static class RawUnixSocket
{
    private const int AfUnix = 1;
    private const int SockStream = 1;

    /// <summary>Leaves a socket file at <paramref name="socketPath"/> that no process is listening on.</summary>
    public static void CreateStalePathname(string socketPath)
    {
        byte[] pathBytes = Encoding.UTF8.GetBytes(socketPath);
        uint addrLen = (uint)(2 + pathBytes.Length + 1);
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

        Array.Copy(pathBytes, 0, addr, 2, pathBytes.Length);

        int fd = socket(AfUnix, SockStream, 0);
        if (fd < 0)
        {
            throw new IOException($"socket() failed (errno {Marshal.GetLastPInvokeError()})");
        }

        try
        {
            if (bind(fd, addr, addrLen) != 0)
            {
                throw new IOException($"bind('{socketPath}') failed (errno {Marshal.GetLastPInvokeError()})");
            }

            // It was a real listener once; the point is that close() below ends it while the file remains.
            listen(fd, 1);
        }
        finally
        {
            // No unlink: the socket file persists on disk with no listener behind it.
            close(fd);
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int socket(int domain, int type, int protocol);

    [DllImport("libc", SetLastError = true)]
    private static extern int bind(int fd, byte[] addr, uint addrLen);

    [DllImport("libc", SetLastError = true)]
    private static extern int listen(int fd, int backlog);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);
}
