namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202 follow-up: the ownership <see cref="ModeLock"/> is only as good as the identity it locks on.
/// Deriving the sidecar name lexically (<see cref="Path.GetFullPath(string)"/>) never follows a symlink, so a
/// database reached through an <em>ordinary</em> alias — a symlinked file, or a file under a symlinked
/// directory — takes a sidecar beside a different name for the same inode. A daemon on <c>real/store.db</c> and
/// a direct child on <c>alias/store.db</c> would then lock two different sidecars while SQLite opened one file:
/// the direct commit succeeds and the daemon's watch stays parked. That is not hostile tampering — it is
/// supported path aliasing.
///
/// <para><see cref="DatabasePath.Canonicalize"/> closes it by resolving every intermediate and final symlink
/// through <c>realpath(3)</c> to one canonical identity, handed to both the lock and SQLite. These tests prove
/// the fix where it is real — at the process boundary, through an ordinary pre-existing symlink — and pin the
/// canonicalization behavior for existing, not-yet-created, and dangling paths.</para>
///
/// <para>The final-component-symlink real-process test is the strict mutation guard: the sidecar suffix
/// (<c>-modelock</c>) is appended to the <em>link's</em> name, so <c>real/store.db</c> and its sibling
/// <c>real/alias.db</c> name two different sidecar inodes. Revert the sidecar to a lexical path and the
/// forced-direct child takes that different lock, its commit lands behind the daemon, and the "child fails /
/// nothing committed" assertions break. The parent-<em>directory</em>-symlink real-process test documents that
/// end-to-end shape too, though its sidecar co-locates inside the aliased directory (so <c>flock</c>'s own open
/// resolution already lands on the daemon's sidecar inode there); the strict lexical-vs-canonical distinction
/// for parent-directory aliases is pinned by the convergence unit tests, which compare the canonical identities
/// directly.</para>
/// </summary>
public sealed class DatabasePathAliasTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DaemonOwnsRealFile_ForcedDirectChildViaFinalComponentSymlink_FailsWithoutCommitting_AndDaemonWatchStaysLive()
    {
        // A daemon exclusively owns the real database file. A sibling symlink names the very same file.
        string root = NewDir();
        string dbReal = Path.Combine(root, "store.db");
        await using TestDaemon daemon = await TestDaemon.StartOnDbAsync(dbReal, Ct);

        string aliasFile = Path.Combine(root, "alias.db");
        File.CreateSymbolicLink(aliasFile, "store.db"); // final-component symlink to the daemon-owned file

        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);
        await using IAsyncEnumerator<WatchMessage> watch =
            remote.WatchAsync("/events/", 0, Ct).GetAsyncEnumerator(Ct);
        await ConsumeThroughSyncAsync(watch);

        // Force a real product child toward the LocalStore fallback on the *alias*: an absent socket, and
        // TURNSTILE_DB pointing at the symlink. Canonicalization resolves the alias to the daemon-owned real
        // file, so the shared open collides with the daemon's exclusive lock and is refused before SQLite is
        // touched. Lexically (the reverted bug) the alias would take its own sidecar and this commit would land.
        CliResult push = await RunChildAsync(daemon.Socket, aliasFile, "queue", "push", "/events", "--value", "ghost");

        Assert.Equal(1, push.ExitCode);
        Assert.StartsWith("turnstile:", push.FirstStdErrLine);
        Assert.Contains("daemon", push.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // The child committed nothing: through the daemon (the only writer) the events prefix is empty.
        Assert.Empty(await remote.RangeAsync("/events/", ct: Ct));

        // The daemon — the only writer left — wakes the live watcher. No sleep, no poll.
        WriteResult write = await remote.CreateAsync("/events/real", Bytes("v"), ct: Ct);
        Assert.True(write.Succeeded);

        WatchEvent delivered = await NextEventForKeyAsync(watch, "/events/real");
        Assert.Equal(write.Revision, delivered.Revision);
        Assert.False(delivered.Deleted);
    }

    [Fact]
    public async Task DaemonOwnsRealFile_ForcedDirectChildViaParentDirectorySymlink_FailsWithoutCommitting_AndDaemonWatchStaysLive()
    {
        // The intermediate-directory case realpath handles but File.ResolveLinkTarget on the file alone would
        // miss: the daemon owns realdir/store.db, and aliasdir is a symlink to realdir. (Here the sidecar
        // co-locates inside the aliased directory, so flock's own open-time resolution already lands on the
        // daemon's sidecar inode; canonicalization additionally makes the identity explicit and consistent, and
        // the strict lexical-vs-canonical distinction for this shape is pinned by the convergence unit tests.)
        string root = NewDir();
        string realDir = Path.Combine(root, "realdir");
        Directory.CreateDirectory(realDir);
        string dbReal = Path.Combine(realDir, "store.db");
        await using TestDaemon daemon = await TestDaemon.StartOnDbAsync(dbReal, Ct);

        string aliasDir = Path.Combine(root, "aliasdir");
        Directory.CreateSymbolicLink(aliasDir, "realdir"); // parent-directory symlink
        string dbAlias = Path.Combine(aliasDir, "store.db");

        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);
        await using IAsyncEnumerator<WatchMessage> watch =
            remote.WatchAsync("/events/", 0, Ct).GetAsyncEnumerator(Ct);
        await ConsumeThroughSyncAsync(watch);

        CliResult push = await RunChildAsync(daemon.Socket, dbAlias, "queue", "push", "/events", "--value", "ghost");

        Assert.Equal(1, push.ExitCode);
        Assert.StartsWith("turnstile:", push.FirstStdErrLine);
        Assert.Contains("daemon", push.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        Assert.Empty(await remote.RangeAsync("/events/", ct: Ct));

        WriteResult write = await remote.CreateAsync("/events/real", Bytes("v"), ct: Ct);
        Assert.True(write.Succeeded);

        WatchEvent delivered = await NextEventForKeyAsync(watch, "/events/real");
        Assert.Equal(write.Revision, delivered.Revision);
        Assert.False(delivered.Deleted);
    }

    [Fact]
    public async Task OpenLocalStore_ViaAliases_ThrowsTypedInUse_ConsistentlyWithTheRealPath_WhenADaemonOwnsIt()
    {
        // The in-process, typed face of lock-identity consistency: with a daemon owning the real file, opening
        // it directly is refused whether the path is the real name, a final-component symlink, or a name under
        // a symlinked directory — all three resolve to the daemon's one sidecar. Revert canonicalization and
        // the two alias opens would succeed against a daemon-owned file instead of throwing.
        string root = NewDir();
        string realDir = Path.Combine(root, "realdir");
        Directory.CreateDirectory(realDir);
        string dbReal = Path.Combine(realDir, "store.db");
        await using TestDaemon daemon = await TestDaemon.StartOnDbAsync(dbReal, Ct);

        string aliasFile = Path.Combine(realDir, "alias.db");
        File.CreateSymbolicLink(aliasFile, "store.db");
        string aliasDir = Path.Combine(root, "aliasdir");
        Directory.CreateSymbolicLink(aliasDir, "realdir");
        string dbViaAliasDir = Path.Combine(aliasDir, "store.db");

        foreach (string path in new[] { dbReal, aliasFile, dbViaAliasDir })
        {
            TurnstileDatabaseInUseException ex =
                await Assert.ThrowsAsync<TurnstileDatabaseInUseException>(() => LocalStore.OpenAsync(path));
            Assert.Contains("daemon", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Canonicalize_FinalComponentSymlink_ConvergesOnTheRealFile()
    {
        string root = NewDir();
        string real = Path.Combine(root, "store.db");
        File.WriteAllText(real, string.Empty);
        string alias = Path.Combine(root, "alias.db");
        File.CreateSymbolicLink(alias, "store.db");

        string canonReal = DatabasePath.Canonicalize(real);
        string canonAlias = DatabasePath.Canonicalize(alias);

        Assert.Equal(canonReal, canonAlias);
        Assert.EndsWith("store.db", canonReal);
        Assert.True(File.Exists(canonAlias), "the canonical path must name a real, openable file");
        Assert.NotEqual(Path.GetFullPath(alias), canonAlias); // proves symlink resolution, not lexical only
    }

    [Fact]
    public void Canonicalize_ParentDirectorySymlink_ConvergesOnTheRealFile()
    {
        string root = NewDir();
        string realDir = Path.Combine(root, "realdir");
        Directory.CreateDirectory(realDir);
        string real = Path.Combine(realDir, "store.db");
        File.WriteAllText(real, string.Empty);
        string aliasDir = Path.Combine(root, "aliasdir");
        Directory.CreateSymbolicLink(aliasDir, "realdir");

        string canonReal = DatabasePath.Canonicalize(real);
        string canonViaAliasDir = DatabasePath.Canonicalize(Path.Combine(aliasDir, "store.db"));

        Assert.Equal(canonReal, canonViaAliasDir);
    }

    [Fact]
    public void Canonicalize_NotYetCreatedUnderAliasedDirectory_ConvergesBeforeCreation_WithoutCreatingTheFile()
    {
        string root = NewDir();
        string realDir = Path.Combine(root, "realdir");
        Directory.CreateDirectory(realDir);
        string aliasDir = Path.Combine(root, "aliasdir");
        Directory.CreateSymbolicLink(aliasDir, "realdir");

        // Neither file exists yet. The identity must still converge, resolved from the (existing) parent, so a
        // daemon and a direct store that create the file through different directory aliases still agree.
        string canonReal = DatabasePath.Canonicalize(Path.Combine(realDir, "new.db"));
        string canonAlias = DatabasePath.Canonicalize(Path.Combine(aliasDir, "new.db"));

        Assert.Equal(canonReal, canonAlias);
        Assert.False(File.Exists(canonReal), "canonicalization resolves identity; it must not create the database file");
    }

    [Fact]
    public void Canonicalize_DanglingFinalSymlink_FailsVisibly_RatherThanDerivingASplitIdentity()
    {
        // A final-component symlink whose target is absent cannot be canonicalized honestly: appending its name
        // to the resolved parent would lock the link's path while SQLite followed it elsewhere. Refuse loudly.
        string root = NewDir();
        string dangling = Path.Combine(root, "dangling.db");
        File.CreateSymbolicLink(dangling, "does-not-exist.db");

        // The database path still fails visibly with an IOException — canonicalization refuses a dangling final
        // symlink for a database exactly as before. The refusal is now a precise IOException subtype
        // (DanglingSymlinkException) so the socket-startup path can translate only this one case to its typed
        // refusal; database callers keep the unchanged IOException contract, verified with the base type here.
        IOException ex = Assert.ThrowsAny<IOException>(() => DatabasePath.Canonicalize(dangling));
        Assert.Contains("dangling", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<CliResult> RunChildAsync(string daemonSocket, string dbPath, params string[] args)
    {
        // Force the LocalStore fallback: a socket path that no daemon is listening on (distinct from the real
        // daemon's socket) plus the database (via whatever alias) under TURNSTILE_DB.
        var env = new Dictionary<string, string>
        {
            ["TURNSTILE_DB"] = dbPath,
            ["TURNSTILE_SOCKET"] = daemonSocket + ".nosock",
        };
        return await CliProcess.RunAsync(env, Ct, args);
    }

    private static async Task ConsumeThroughSyncAsync(IAsyncEnumerator<WatchMessage> watch)
    {
        while (await watch.MoveNextAsync())
        {
            if (watch.Current is WatchSyncMessage)
            {
                return;
            }
        }

        Assert.Fail("watch ended before the one-shot sync");
    }

    private static async Task<WatchEvent> NextEventForKeyAsync(IAsyncEnumerator<WatchMessage> watch, string key)
    {
        while (await watch.MoveNextAsync())
        {
            if (watch.Current is WatchEventMessage e && e.Event.Key == key)
            {
                return e.Event;
            }
        }

        Assert.Fail($"watch ended before delivering an event for {key}");
        throw new InvalidOperationException("unreachable");
    }

    private string NewDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"turnstile-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
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
}
