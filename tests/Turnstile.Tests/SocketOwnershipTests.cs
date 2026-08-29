namespace Turnstile.Tests;

using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #212: database ownership (#202) does not protect the <em>socket endpoint</em>. A daemon takes its
/// <see cref="ModeLock"/> on the canonical database path; a second daemon on the same socket but a
/// <em>different</em> database takes a different mode lock (no conflict) and, under the old startup, would
/// unconditionally unlink the first daemon's live socket and bind its own. Unix lets a listening socket be
/// unlinked without disturbing existing connections, so the two daemons then split coordination state — old
/// clients stay on the first daemon and its database, new clients reach the second.
///
/// <para>The fix is an independent <see cref="SocketLock"/> — an exclusive <see cref="FileLock"/> on the
/// socket's canonical identity, acquired <em>before</em> any delete or bind — plus a liveness probe that
/// refuses to unlink a socket a listener still answers on. These prove the contract at the real process/socket
/// boundary:</para>
/// <list type="bullet">
///   <item>a second product daemon on the same socket but a different database is refused, the first daemon's
///   socket survives, and both an existing and a fresh client still reach the first daemon and its data — the
///   claim the old unconditional-delete startup could not make (it would replace the socket and serve forever,
///   so the timeout guard turns that regression into a prompt failure);</item>
///   <item>a live <em>foreign</em> listener holding no socket lock is refused and left in place — the guard for
///   the liveness probe, distinct from the lock;</item>
///   <item>a genuinely stale socket pathname (bound then closed with no unlink) is cleared and the daemon
///   serves, preserving crash-restart behaviour;</item>
///   <item>ordinary socket-path aliases (a final-component symlink, a parent-directory symlink) converge on one
///   canonical endpoint identity, so aliasing cannot bypass the lock.</item>
/// </list>
/// Close-on-exec / OS-release of the socket lock is not retested here: <see cref="SocketLock"/> holds the very
/// same <see cref="FileLock"/> primitive whose inheritance behaviour <see cref="ModeLockInheritanceTests"/>
/// already pins, so a duplicate spawn-a-child test would only re-exercise shared machinery.
/// </summary>
public sealed class SocketOwnershipTests : IDisposable
{
    private readonly List<string> _dirs = [];
    private readonly List<string> _dbs = [];

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static string? Utf8OrNull(KeyState? state) =>
        state?.Value is byte[] value ? Encoding.UTF8.GetString(value) : null;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SecondDaemon_SameSocketDifferentDatabase_IsRefused_AndTheFirstDaemonKeepsServing()
    {
        // Daemon A owns socket S and database A; an existing RemoteStore connection is live and holds data A.
        await using TestDaemon a = await TestDaemon.StartAsync(Ct);
        using RemoteStore existing = RemoteStore.Connect(a.Socket);
        WriteResult wrote = await existing.CreateAsync("/data/a", Bytes("A"), ct: Ct);
        Assert.True(wrote.Succeeded);

        // Daemon B: a real product child on the SAME socket but a DIFFERENT database. Its mode lock does not
        // conflict with A's (different database), so #202 alone would let it proceed — under the old startup it
        // would unlink S and bind its own endpoint, splitting state. Socket-endpoint ownership makes it fail
        // fast instead: it cannot take S's socklock (A holds it). The guard bounds the old behaviour (B would
        // otherwise serve forever) so the regression fails promptly rather than hanging the suite.
        string dbB = NewDb();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromSeconds(30));
        CliResult serveB = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", a.Socket, "--db", dbB);

        Assert.Equal(1, serveB.ExitCode);
        Assert.StartsWith("turnstile:", serveB.FirstStdErrLine);
        Assert.Contains("socket", serveB.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // The first daemon's live socket is untouched, and B never created its own database (it failed before
        // opening one) — nothing split off.
        Assert.True(File.Exists(a.Socket), "the first daemon's live socket must not have been replaced");
        Assert.False(File.Exists(dbB), "the refused daemon must not have created its database");

        // The existing connection and a brand-new one both still reach daemon A and read data A.
        Assert.Equal("A", Utf8OrNull(await existing.GetAsync("/data/a", Ct)));
        using RemoteStore fresh = RemoteStore.Connect(a.Socket);
        Assert.Equal("A", Utf8OrNull(await fresh.GetAsync("/data/a", Ct)));
    }

    [Fact]
    public async Task LiveForeignListener_HoldingNoSocketLock_IsRefused_AndNotDeleted()
    {
        // A live listener that is NOT a Turnstile daemon and holds no socklock (a legacy daemon, or any other
        // process) answers at S. The socket lock is therefore free — but startup must still refuse, because a
        // successful connect proves the endpoint is live, and must NOT unlink it. Remove the liveness probe
        // (take the free lock, then delete unconditionally) and this daemon would replace S and serve forever;
        // the guard would fire and the exit-1 assertion fail.
        string socket = Path.Combine(NewDir(), "foreign.sock");
        using var foreign = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        foreign.Bind(new UnixDomainSocketEndPoint(socket));
        foreign.Listen(16);
        Assert.True(File.Exists(socket));

        string db = NewDb();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromSeconds(30));
        CliResult serve = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", socket, "--db", db);

        Assert.Equal(1, serve.ExitCode);
        Assert.StartsWith("turnstile:", serve.FirstStdErrLine);
        Assert.Contains("socket", serve.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // The foreign endpoint was neither unlinked nor disturbed: the file remains and a fresh connect to it
        // still succeeds (the listener is alive).
        Assert.True(File.Exists(socket), "a live foreign listener must not be unlinked");
        using var client = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(new UnixDomainSocketEndPoint(socket), Ct);
        Assert.True(client.Connected, "the foreign listener must still be reachable after the refused start");
    }

    [Fact]
    public async Task StaleSocketPathname_IsRemoved_AndTheDaemonServes()
    {
        // A genuinely stale socket file: bound then closed with no unlink (a crash leftover). The file is on
        // disk with no listener, so the probe classifies it stale (ECONNREFUSED-class), deletes it, and the
        // daemon binds and serves — the crash-restart path must keep working.
        string socket = Path.Combine(NewDir(), "stale.sock");
        RawUnixSocket.CreateStalePathname(socket);
        Assert.True(File.Exists(socket), "the stale socket file must be present so the daemon has something to clear");

        string db = NewDb();
        await using TestDaemon daemon = await TestDaemon.StartOnSocketAndDbAsync(socket, db, Ct);

        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);
        Assert.True(await remote.GetRevisionAsync(Ct) >= 0);
        WriteResult wrote = await remote.CreateAsync("/k", Bytes("v"), ct: Ct);
        Assert.True(wrote.Succeeded);
        Assert.Equal("v", Utf8OrNull(await remote.GetAsync("/k", Ct)));
    }

    [Fact]
    public void SocketIdentity_FinalComponentSymlink_ConvergesOnOneEndpoint()
    {
        // A sibling symlink names the same socket file. Both spellings must resolve to one canonical identity,
        // so the socklock sidecar they derive is the same file — the alias cannot take a second lock beside a
        // different name for the same endpoint. Revert canonicalization to a lexical path and these diverge.
        string dir = NewDir();
        string real = Path.Combine(dir, "real.sock");
        RawUnixSocket.CreateStalePathname(real);
        string alias = Path.Combine(dir, "alias.sock");
        File.CreateSymbolicLink(alias, "real.sock");

        string canonReal = CanonicalPath.Resolve(real, "socket");
        string canonAlias = CanonicalPath.Resolve(alias, "socket");

        Assert.Equal(canonReal, canonAlias);
        Assert.Equal(SocketLock.SidecarPath(canonReal), SocketLock.SidecarPath(canonAlias));
        Assert.NotEqual(Path.GetFullPath(alias), canonAlias); // proves symlink resolution, not lexical only
    }

    [Fact]
    public void SocketIdentity_ParentDirectorySymlink_ConvergesBeforeTheSocketExists()
    {
        // The same endpoint reached through a symlinked parent directory, before either socket file exists: the
        // identity must still converge, resolved from the (existing) parent, so two daemons that would bind
        // through different directory aliases contend on one socklock.
        string root = NewDir();
        string realDir = Path.Combine(root, "realdir");
        Directory.CreateDirectory(realDir);
        string aliasDir = Path.Combine(root, "aliasdir");
        Directory.CreateSymbolicLink(aliasDir, "realdir");

        string canonReal = CanonicalPath.Resolve(Path.Combine(realDir, "x.sock"), "socket");
        string canonAlias = CanonicalPath.Resolve(Path.Combine(aliasDir, "x.sock"), "socket");

        Assert.Equal(canonReal, canonAlias);
        Assert.Equal(SocketLock.SidecarPath(canonReal), SocketLock.SidecarPath(canonAlias));
    }

    private string NewDir()
    {
        // Short token and short segment: a bound Unix socket path under this directory must stay within the
        // platform limit (104 bytes on macOS, where the temp base is already ~48 chars).
        string dir = Path.Combine(Path.GetTempPath(), $"ts212-{Guid.NewGuid():N}"[..14]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    private string NewDb()
    {
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-socket-{Guid.NewGuid():N}.db");
        _dbs.Add(db);
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();

        foreach (string db in _dbs)
        {
            foreach (string path in new[] { db, db + "-wal", db + "-shm", db + "-modelock" })
            {
                TryDeleteFile(path);
            }
        }

        foreach (string dir in _dirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Unique per run; a leftover tree on a locked handle is harmless.
            }
            catch (UnauthorizedAccessException)
            {
                // Same: best-effort cleanup of a unique scratch tree.
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Unique per run; a locked handle is harmless.
        }
    }
}
