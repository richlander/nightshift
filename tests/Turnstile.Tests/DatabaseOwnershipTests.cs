namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202, the load-bearing half: rejecting a <see cref="LocalStore"/> watch is not enough on its own,
/// because a direct store could still <em>write</em> to the same file while a daemon is watching it. That
/// write commits and advances the revision but pulses only its own process-local signal, so the daemon's
/// watcher — parked on a different signal — never wakes. The fix is an ownership contract enforced by a
/// cross-process <see cref="ModeLock"/>: a daemon owns a database <em>exclusively</em>, or any number of
/// direct stores share it, never both. With no direct store able to open the file behind a daemon's back,
/// every commit flows through the daemon and its watch is genuinely live.
///
/// <para>These tests prove the contract at the process boundary, where it is real:</para>
/// <list type="bullet">
///   <item>a daemon owns the database, so a forced-direct child fails visibly and commits nothing — and a
///   write through the daemon then wakes a live watcher promptly, no polling (the mutation guard: remove the
///   lock and the child commits behind the daemon, and this test's "no commit" and live-delivery claims
///   break);</item>
///   <item>the reverse — an open direct store blocks a daemon from starting, and the daemon serves once the
///   store closes;</item>
///   <item>multiple direct stores (in-process and a separate OS process) still coexist with no daemon,
///   preserving #199.</item>
/// </list>
/// Every boundary crossing is a real <c>turnstile</c> child process, so the exit code and first-line error are
/// the product's own and nothing here mutates the host's <see cref="Console"/> or <see cref="Environment"/>.
/// </summary>
public sealed class DatabaseOwnershipTests : IDisposable
{
    private readonly List<string> _dbs = [];

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task DaemonOwnsDatabase_ForcedDirectChildFailsWithoutCommitting_AndDaemonWatchStaysLive()
    {
        // A real daemon takes exclusive ownership of a fresh database and a RemoteStore parks a live watch on
        // it, consuming through the one-shot sync so nothing is buffered ahead.
        await using TestDaemon daemon = await TestDaemon.StartAsync(Ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        await using IAsyncEnumerator<WatchMessage> watch =
            remote.WatchAsync("/events/", 0, Ct).GetAsyncEnumerator(Ct);
        await ConsumeThroughSyncAsync(watch);

        // Force a product child toward the LocalStore fallback on the same file: TURNSTILE_SOCKET points at an
        // absent path so no daemon is reached, TURNSTILE_DB is the file the daemon owns. `queue push` is a
        // library-mode-capable path, so without the ownership lock it would commit here. With it, the daemon's
        // exclusive lock refuses the direct open before SQLite is touched, so the child fails and writes
        // nothing.
        var childEnv = new Dictionary<string, string>
        {
            ["TURNSTILE_DB"] = daemon.DbPath,
            ["TURNSTILE_SOCKET"] = daemon.DbPath + ".nosock",
        };
        CliResult push = await CliProcess.RunAsync(childEnv, Ct, "queue", "push", "/events", "--value", "ghost");

        Assert.Equal(1, push.ExitCode);
        Assert.StartsWith("turnstile:", push.FirstStdErrLine);
        Assert.Contains("daemon", push.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);

        // The child committed nothing: through the daemon (the only path to the file) the events prefix is
        // empty. This is the direct claim the old design could not make — a direct write can no longer slip
        // past the daemon's watch.
        Assert.Empty(await remote.RangeAsync("/events/", ct: Ct));

        // A write through the daemon — the only writer left — wakes the live watcher. MoveNextAsync blocks
        // until the daemon delivers the event; there is no sleep and no poll driving this.
        WriteResult write = await remote.CreateAsync("/events/real", Bytes("v"), ct: Ct);
        Assert.True(write.Succeeded);

        WatchEvent delivered = await NextEventForKeyAsync(watch, "/events/real");
        Assert.Equal(write.Revision, delivered.Revision);
        Assert.False(delivered.Deleted);
    }

    [Fact]
    public async Task OpenDirectStore_BlocksDaemonStartVisibly_ThenDaemonServesOnceItCloses()
    {
        string db = NewDb();

        // A direct store is open (shared mode lock held). While it is, a real `turnstile serve` child cannot
        // take the exclusive lock, so it fails fast with the established non-success signal instead of starting
        // with a watch it could not keep live.
        LocalStore holder = await LocalStore.OpenAsync(db);
        try
        {
            string socket = Path.Combine(Path.GetTempPath(), $"ts-own-{Guid.NewGuid():N}.sock");
            var env = new Dictionary<string, string> { ["TURNSTILE_DB"] = db, ["TURNSTILE_SOCKET"] = socket };

            // Fail-fast safety net: if the lock did NOT hold and serve started, it would run forever — bound it
            // so the test fails promptly rather than hanging the suite. On the correct path serve exits 1 well
            // inside this budget.
            using var guard = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            guard.CancelAfter(TimeSpan.FromSeconds(30));
            CliResult serve = await CliProcess.RunAsync(env, guard.Token, "serve", "--db", db, "--socket", socket);

            Assert.Equal(1, serve.ExitCode);
            Assert.StartsWith("turnstile:", serve.FirstStdErrLine);
            Assert.Contains("daemon", serve.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(socket), "a daemon that failed to take ownership must not have bound a socket");
        }
        finally
        {
            holder.Dispose();
        }

        // With the direct store closed the lock is free, so a daemon now acquires it and serves normally.
        await using TestDaemon daemon = await TestDaemon.StartOnDbAsync(db, Ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);
        Assert.True(await remote.GetRevisionAsync(Ct) >= 0);
    }

    [Fact]
    public async Task OpenLocalStore_ThrowsTypedInUse_WhenADaemonOwnsTheDatabase()
    {
        // The in-process, typed face of the same rule and the mutation guard for the shared-lock acquire:
        // remove ModeLock.AcquireShared from LocalStore.OpenAsync and this open would succeed against a
        // daemon-owned file instead of throwing.
        await using TestDaemon daemon = await TestDaemon.StartAsync(Ct);

        TurnstileDatabaseInUseException ex =
            await Assert.ThrowsAsync<TurnstileDatabaseInUseException>(() => LocalStore.OpenAsync(daemon.DbPath));
        Assert.Contains("daemon", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MultipleDirectStores_CoexistWithoutADaemon_InProcessAndAcrossProcesses()
    {
        // #199 preserved: with no daemon, the shared mode lock lets many direct stores hold one file at once.
        // Two in-process stores each see the other's committed rows...
        string db = NewDb();
        using LocalStore a = await LocalStore.OpenAsync(db);
        using LocalStore b = await LocalStore.OpenAsync(db);

        await a.CreateAsync("/k/a", Bytes("1"), ct: Ct);
        await b.CreateAsync("/k/b", Bytes("2"), ct: Ct);

        Assert.NotNull(await b.GetAsync("/k/a", Ct));
        Assert.NotNull(await a.GetAsync("/k/b", Ct));

        // ...and a genuinely separate OS process opens the very same file in library mode and commits too,
        // proving the shared lock crosses the process boundary, not just in-process instances.
        var env = new Dictionary<string, string>
        {
            ["TURNSTILE_DB"] = db,
            ["TURNSTILE_SOCKET"] = db + ".nosock",
        };
        CliResult push = await CliProcess.RunAsync(env, Ct, "queue", "push", "/q", "--value", "x");
        Assert.Equal(0, push.ExitCode);

        string committed = push.StdOut.Trim();
        Assert.StartsWith("/q/item/", committed);
        Assert.NotNull(await a.GetAsync(committed, Ct));
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

    private string NewDb()
    {
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-ownership-{Guid.NewGuid():N}.db");
        _dbs.Add(db);
        return db;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string db in _dbs)
        {
            foreach (string path in new[] { db, db + "-wal", db + "-shm", db + "-modelock", db + ".nosock" })
            {
                if (File.Exists(path))
                {
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
        }
    }
}
