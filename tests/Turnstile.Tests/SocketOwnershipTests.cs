namespace Turnstile.Tests;

using System.Diagnostics;
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
/// <para>The fix is an exclusive <see cref="SocketLock"/> — a <see cref="FileLock"/> on the socket's canonical
/// identity, acquired <em>before</em> any bind — plus a <b>fail-closed existence check</b>: Turnstile never
/// unlinks an existing socket path, because it cannot prove the path is not a live listener (no connect-only
/// probe can tell a crash leftover apart from a saturated, uncooperative live server). These prove the contract
/// at the real process/socket boundary:</para>
/// <list type="bullet">
///   <item>a second product daemon on the same socket but a different database is refused (it cannot take the
///   endpoint lock the first holds), the first daemon's socket survives, and both an existing and a fresh
///   client still reach the first daemon and its data — the claim the old unconditional-delete startup could
///   not make;</item>
///   <item>a live <em>foreign</em> listener holding no socklock is refused on the existence check and left in
///   place — the guard for the fail-closed refusal, distinct from the lock;</item>
///   <item>a <em>saturated</em> live foreign listener is refused promptly without any connect at all — the
///   regression for the removed, blocking connect probe;</item>
///   <item>a stale socket pathname (bound then closed with no unlink) is <em>refused</em>, left untouched, and
///   only after the operator explicitly removes it does the daemon bind and serve — the deliberate
///   safety-over-convenience tradeoff;</item>
///   <item>an ordinary graceful restart on the same socket still succeeds, because a graceful shutdown removes
///   the daemon's own socket (only an unclean crash leftover is refused);</item>
///   <item>a final-component symlink to an existing endpoint is refused, leaving both the link and its target
///   untouched — no unlink, no rebind through the alias;</item>
///   <item>a final-component <em>dangling</em> symlink (its target absent) is refused with the same CLI
///   contract — exit 1, first-line <c>turnstile:</c> — rather than crashing on an unhandled canonicalization
///   exception, and the dangling link is left untouched with no database created;</item>
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
        // fast instead: it cannot take S's socklock (A holds it exclusively for its whole lifetime). The guard
        // bounds the old behaviour (B would otherwise serve forever) so the regression fails promptly rather
        // than hanging the suite.
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
        // process) answers at S. The socket lock is therefore free — but startup must still refuse, because
        // *any* existing entry at the path is refused (Turnstile never unlinks a path it cannot prove is dead)
        // and must NOT be unlinked. There is no connect probe: the refusal is the existence check. Remove the
        // existence refusal and startup would instead attempt to bind over the live endpoint — EADDRINUSE — so
        // the clean typed refusal (exit 1, first-line `turnstile:`) would degrade into a raw bind failure.
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
    public async Task SaturatedForeignListener_IsRefusedPromptly_WithoutConnecting_AndNotDeleted()
    {
        // A live foreign listener that never accepts, with its accept queue best-effort saturated — the exact
        // shape that made the old connect(2) probe unsafe: a saturated Linux accept queue can block a connect
        // indefinitely (bypassing cancellation), and a saturated macOS listener can answer ECONNREFUSED and be
        // mistaken for stale. The new startup never connects, so it must refuse on the existence check
        // regardless of how uncooperative the listener is, and do so promptly.
        string socket = Path.Combine(NewDir(), "saturated.sock");
        using var foreign = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        foreign.Bind(new UnixDomainSocketEndPoint(socket));
        foreign.Listen(1); // tiny backlog; the listener never calls Accept
        Assert.True(File.Exists(socket));

        // Fill the queue with non-blocking connects so the test itself can never hang on a saturated endpoint.
        // The assertion below does not depend on how many land — only on the daemon refusing without probing.
        var pending = new List<Socket>();
        try
        {
            for (int i = 0; i < 32; i++)
            {
                var c = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) { Blocking = false };
                try
                {
                    c.Connect(new UnixDomainSocketEndPoint(socket));
                }
                catch (SocketException)
                {
                    // EINPROGRESS/EWOULDBLOCK (connect in flight) or the queue is full: either way, best effort.
                }

                pending.Add(c);
            }

            string db = NewDb();
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            guard.CancelAfter(TimeSpan.FromSeconds(30));

            var elapsed = Stopwatch.StartNew();
            CliResult serve = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", socket, "--db", db);
            elapsed.Stop();

            Assert.Equal(1, serve.ExitCode);
            Assert.StartsWith("turnstile:", serve.FirstStdErrLine);
            Assert.Contains("socket", serve.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

            // Prompt: with no connect probe, refusal cannot be delayed by the saturated queue. The bound is
            // generous (it only needs to catch an *indefinite* block from a reintroduced probe), yet well under
            // the 30s guard that would otherwise mask a hang.
            Assert.True(
                elapsed.Elapsed < TimeSpan.FromSeconds(20),
                $"startup must refuse a saturated listener promptly, without a blocking probe; took {elapsed.Elapsed}");

            // The saturated live endpoint was not unlinked.
            Assert.True(File.Exists(socket), "a saturated live listener must not be unlinked");
        }
        finally
        {
            foreach (Socket c in pending)
            {
                c.Dispose();
            }
        }
    }

    [Fact]
    public async Task StaleSocketPathname_IsRefused_ThenBindsAfterExplicitOperatorCleanup()
    {
        // A genuinely stale socket file: bound then closed with no unlink (a crash leftover). The daemon cannot
        // prove it is not a live listener, so it fails closed — the path is left untouched and startup refuses.
        // This is the deliberate safety-over-convenience tradeoff: an unclean crash needs explicit cleanup.
        string socket = Path.Combine(NewDir(), "stale.sock");
        RawUnixSocket.CreateStalePathname(socket);
        Assert.True(File.Exists(socket), "the stale socket file must be present so the daemon has something to refuse");

        string db = NewDb();
        using (var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct))
        {
            guard.CancelAfter(TimeSpan.FromSeconds(30));
            CliResult refused = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", socket, "--db", db);

            Assert.Equal(1, refused.ExitCode);
            Assert.StartsWith("turnstile:", refused.FirstStdErrLine);
            Assert.Contains("socket", refused.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);
        }

        // The refusal never unlinked the path, and never created the database — a fail-closed start touches
        // nothing.
        Assert.True(File.Exists(socket), "a fail-closed refusal must not delete the stale path");
        Assert.False(File.Exists(db), "the refused daemon must not have created its database");

        // The operator explicitly clears the stale path — the deliberate manual step — and only now does the
        // daemon bind and serve on that same socket.
        File.Delete(socket);
        await using TestDaemon daemon = await TestDaemon.StartOnSocketAndDbAsync(socket, db, Ct);

        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);
        Assert.True(await remote.GetRevisionAsync(Ct) >= 0);
        WriteResult wrote = await remote.CreateAsync("/k", Bytes("v"), ct: Ct);
        Assert.True(wrote.Succeeded);
        Assert.Equal("v", Utf8OrNull(await remote.GetAsync("/k", Ct)));
    }

    [Fact]
    public async Task Daemon_AfterGracefulShutdown_RestartsOnTheSameSocket()
    {
        // The ordinary restart the fail-closed policy must still allow: a graceful shutdown unlinks the daemon's
        // own socket (Kestrel removes it on unbind), so the endpoint is free and the next start binds. Only an
        // *unclean* crash leftover is refused.
        string socket = TestDaemon.NewSocketPath();
        string db = NewDb();

        await using (TestDaemon first = await TestDaemon.StartOnSocketAndDbAsync(socket, db, Ct))
        {
            using RemoteStore r = RemoteStore.Connect(first.Socket);
            Assert.True((await r.CreateAsync("/k", Bytes("v"), ct: Ct)).Succeeded);

            await first.StopAsync();
            Assert.False(File.Exists(socket), "a graceful shutdown must remove the daemon's own socket");
        }

        await using TestDaemon second = await TestDaemon.StartOnSocketAndDbAsync(socket, db, Ct);
        using RemoteStore fresh = RemoteStore.Connect(second.Socket);
        Assert.Equal("v", Utf8OrNull(await fresh.GetAsync("/k", Ct)));
    }

    [Fact]
    public async Task Start_FinalComponentSymlinkToExistingEndpoint_IsRefused_AndLeavesLinkAndTargetUntouched()
    {
        // The socket is reached through a final-component symlink whose target is an existing (here stale)
        // endpoint. The lock keys on the canonical target, and the existence check sees the target through the
        // link — so startup refuses. It must not unlink either the link or the target, and must not rebind the
        // alias name to a different endpoint than the one it locked (the #212 final-symlink hazard).
        string dir = NewDir();
        string real = Path.Combine(dir, "real.sock");
        RawUnixSocket.CreateStalePathname(real);
        string alias = Path.Combine(dir, "alias.sock");
        File.CreateSymbolicLink(alias, "real.sock");

        string db = NewDb();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromSeconds(30));
        CliResult serve = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", alias, "--db", db);

        Assert.Equal(1, serve.ExitCode);
        Assert.StartsWith("turnstile:", serve.FirstStdErrLine);
        Assert.Contains("socket", serve.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // Both the link and its target survive, untouched: no unlink, no rebind through the alias.
        Assert.True(File.Exists(real), "the symlink target must not be unlinked");
        Assert.Equal("real.sock", new FileInfo(alias).LinkTarget);
        Assert.False(File.Exists(db), "the refused daemon must not have created its database");
    }

    [Fact]
    public async Task Start_FinalComponentDanglingSymlink_IsRefused_WithTheCliContract_AndLeavesTheLinkUntouched()
    {
        // The socket path's final component is a symlink whose target does not exist (a *dangling* leaf). Its
        // identity cannot be canonicalized honestly — appending its name to the resolved parent would lock the
        // link's own path while the OS followed it elsewhere — so CanonicalPath.Resolve refuses it with a
        // precise DanglingSymlinkException *before* any lock or bind. The daemon must translate that into the
        // same typed refusal every other fail-closed socket condition emits: exit 1, first-line `turnstile:`,
        // never a raw unhandled-exception crash (the round-2 finding, where the native binary exited 134). It
        // must not create the database, and must leave the dangling link exactly as it found it — no unlink.
        string dir = NewDir();
        string alias = Path.Combine(dir, "dangling.sock");
        File.CreateSymbolicLink(alias, "no-such-target.sock");
        Assert.False(File.Exists(Path.Combine(dir, "no-such-target.sock")), "the symlink target must be absent — this is the dangling case");
        Assert.Equal("no-such-target.sock", new FileInfo(alias).LinkTarget);

        string db = NewDb();
        using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        guard.CancelAfter(TimeSpan.FromSeconds(30));
        CliResult serve = await CliProcess.RunAsync(null, guard.Token, "serve", "--socket", alias, "--db", db);

        Assert.Equal(1, serve.ExitCode);
        Assert.StartsWith("turnstile:", serve.FirstStdErrLine);
        Assert.Contains("socket", serve.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // The dangling link itself is untouched (still points at the absent target), and no database was created.
        Assert.Equal("no-such-target.sock", new FileInfo(alias).LinkTarget);
        Assert.False(File.Exists(db), "the refused daemon must not have created its database");
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

    [Fact]
    public void SecondSocketLock_OnSameCanonicalIdentity_IsRefused_WithNoSocketFilePresent()
    {
        // The endpoint lock's unique job — beyond the existence check — is to serialize compliant daemons and
        // close the absent-path race, where neither daemon has bound yet so no socket file exists for an
        // existence check to catch. With nothing at the path, only the lock can arbitrate. This isolates the
        // lock: remove the SocketLock (or its flock) and the second acquire would wrongly succeed here, even
        // though the existence check still passes every foreign/stale-file test.
        string socket = Path.Combine(NewDir(), "race.sock");
        string canonical = CanonicalPath.Resolve(socket, "socket");
        Assert.False(File.Exists(socket), "no endpoint exists yet — only the lock, not the existence check, can arbitrate");

        using SocketLock first = SocketLock.Acquire(socket, canonical);
        TurnstileSocketInUseException refused = Assert.Throws<TurnstileSocketInUseException>(
            () => SocketLock.Acquire(socket, canonical));
        Assert.Contains("already owns the socket endpoint", refused.Message);
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
