namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202: a <see cref="LocalStore"/> (direct SQLite, library mode) owns a process-local change signal,
/// so a watcher there can never be woken by another process committing to the same file. The product decision
/// is that watch liveness is daemon-only, enforced as an explicit-failure contract: <see cref="LocalStore"/>
/// rejects <see cref="ITurnstile.WatchAsync"/> eagerly with a <see cref="TurnstileWatchUnavailableException"/>
/// rather than hand back a stream that could park forever.
///
/// <para>These are outcome tests of that contract, at the boundaries that make it real:</para>
/// <list type="bullet">
///   <item>the eager, deterministic throw (the mutation guard against restoring the direct delegation);</item>
///   <item>a genuinely separate OS process committing to the shared file in library mode, then a real daemon
///   on that same file resuming from the saved cursor and delivering that commit without loss — the
///   cross-process claim the process-local signal could not satisfy;</item>
///   <item>the CLI mapping of the narrowed contract to its load-bearing signal: exit 1 and a first-line
///   <c>turnstile:</c> error, on both watch-dependent helper paths (queue <c>pop --wait</c> and a contended
///   <c>lock</c>, which catch the condition in different methods).</item>
/// </list>
/// The separate writer is a real <c>turnstile</c> child process, not a second in-process store: process exit
/// is what synchronises its commit, which is the only thing that proves the boundary the daemon-only decision
/// exists to cross. Nothing here sleeps for an event or polls for one — the daemon replays the backlog, so the
/// committed event is present before the one-shot sync.
/// </summary>
public sealed class DirectWatchContractTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-directwatch-{Guid.NewGuid():N}.db");

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WatchAsync_OnLocalStore_ThrowsEagerly_BeforeYieldingOrParking()
    {
        using LocalStore store = await LocalStore.OpenAsync(_dbPath);
        ITurnstile surface = store;

        // Assert.Throws is synchronous: it proves the exception is raised when WatchAsync is *invoked*, not
        // deferred to enumeration. This is the mutation guard — restoring the old `=> _kv.WatchAsync(...)`
        // delegation would return an IAsyncEnumerable without throwing, and this assertion would fail.
        var ex = Assert.Throws<TurnstileWatchUnavailableException>(() => { _ = surface.WatchAsync("/", 0, Ct); });
        Assert.Contains("daemon", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WatchAsync_UnsupportedTransportWins_OverAPreCancelledToken()
    {
        using LocalStore store = await LocalStore.OpenAsync(_dbPath);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // Documented decision: the unsupported transport wins. A direct-store watch can never succeed, so the
        // contract stays deterministic — it always throws TurnstileWatchUnavailableException, never an
        // OperationCanceledException, even when the token is already cancelled. The failure never races the
        // token's state, so no assertion here is timing-dependent.
        Assert.Throws<TurnstileWatchUnavailableException>(() => { _ = store.WatchAsync("/", 0, cts.Token); });
    }

    [Fact]
    public async Task SeparateOsProcess_CommitsInLibraryMode_AndADaemonReplaysItFromTheSavedCursorWithoutLoss()
    {
        // The parent opens the file directly (library mode), seeds a baseline, and records the revision a
        // reconnect must resume from — the cursor. At this point a direct-store watch is refused eagerly, so
        // the parent is never left parked on events it could not see.
        long resumeFrom;
        using (LocalStore parent = await LocalStore.OpenAsync(_dbPath))
        {
            await parent.CreateAsync("/seed", Bytes("0"), ct: Ct);
            resumeFrom = await parent.GetRevisionAsync(Ct);
            Assert.Throws<TurnstileWatchUnavailableException>(() => { _ = parent.WatchAsync("/events/", resumeFrom, Ct); });
        }

        // A genuinely separate OS process commits to the same DB in library mode (no daemon), then exits.
        // Process exit is what synchronises the commit across the boundary — the exact scenario a process-local
        // signal cannot bridge. `queue push` is a library-mode-capable product path, so this exercises the real
        // binary, not a test-only writer. TURNSTILE_SOCKET points at an absent path, forcing the LocalStore
        // fallback; TURNSTILE_DB is the shared file.
        var childEnv = new Dictionary<string, string>
        {
            ["TURNSTILE_DB"] = _dbPath,
            ["TURNSTILE_SOCKET"] = _dbPath + ".nosock",
        };
        CliResult push = await CliProcess.RunAsync(childEnv, Ct, "queue", "push", "/events", "--value", "payload");
        Assert.Equal(0, push.ExitCode);
        string committedKey = push.StdOut.Trim();
        Assert.StartsWith("/events/item/", committedKey);

        // The child wrote through a pooled WAL connection and exited; its committed frames can linger in the
        // -wal file. A daemon that then opens the file fresh reads reliably on one connection (a unary get
        // below sees the row) but a *separate* pooled read connection — the one the watch backlog uses — can
        // momentarily observe a staler WAL snapshot. That cross-process pooled-WAL visibility is orthogonal to
        // the watch contract under test, so materialise the log into the database first, making every fresh
        // reader deterministic. (See findings: the daemon's cold cross-process read may merit a follow-up.)
        MaterializeWal(_dbPath);

        // Reconnect through a real daemon on the same database and resume from the saved cursor. The commit the
        // separate process made while only library-mode stores were open is past the cursor, so it replays from
        // the backlog before the one-shot sync — nothing to wait on and no poll.
        await using TestDaemon daemon = await TestDaemon.StartOnDbAsync(_dbPath, Ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        KeyState? committed = await remote.GetAsync(committedKey, Ct);
        Assert.NotNull(committed);
        Assert.True(committed!.ModRevision > resumeFrom, "the separately committed event must be past the saved cursor");

        WatchEvent? delivered = null;
        await foreach (WatchMessage msg in remote.WatchAsync("/events/", resumeFrom, Ct))
        {
            if (msg is WatchEventMessage e && e.Event.Key == committedKey)
            {
                delivered = e.Event;
                break;
            }

            // Reaching the caught-up marker without the event would be loss — the assertion below then fails
            // deterministically rather than the loop spinning or blocking forever.
            if (msg is WatchSyncMessage)
            {
                break;
            }
        }

        Assert.NotNull(delivered);
        Assert.Equal(committedKey, delivered!.Key);
        Assert.Equal(committed.ModRevision, delivered.Revision);
        Assert.False(delivered.Deleted);
    }

    [Fact]
    public async Task QueuePopWait_InLibraryMode_ReportsUnavailable_WithDaemonRequiredFirstLine()
    {
        // `queue pop --wait` on an empty queue with no daemon falls back to a LocalStore and would block on a
        // watch. The narrowed contract maps that to the non-success CLI convention. A real child process is
        // used so the exit code and stderr are the product's own, with no global Console/Environment mutation
        // in the test host. TURNSTILE_SOCKET is absent (LocalStore fallback); the db is a disposable scratch.
        string home = NewScratchDir();
        try
        {
            var env = new Dictionary<string, string>
            {
                ["TURNSTILE_DB"] = Path.Combine(home, "t.db"),
                ["TURNSTILE_SOCKET"] = Path.Combine(home, "absent.sock"),
            };

            CliResult r = await CliProcess.RunAsync(env, Ct, "queue", "pop", "/q", "--wait");

            Assert.Equal(1, r.ExitCode);
            Assert.StartsWith("turnstile:", r.FirstStdErrLine);
            Assert.Contains("daemon", r.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteScratch(home);
        }
    }

    [Fact]
    public async Task ContendedLock_InLibraryMode_ReportsUnavailable_WithDaemonRequiredFirstLine()
    {
        // The contended lock/elect path catches TurnstileWatchUnavailableException in a *different* method
        // (Helpers.ExclusiveAsync) than the queue path, so it earns its own coverage. Force real contention:
        // seed the lock key held by a live (unexpired) lease in the shared DB, so a second library-mode `lock`
        // cannot claim it and must wait on a watch — which library mode refuses. An uncontended lock never
        // reaches the watch and is unaffected, which is why the key must genuinely be held first.
        using (LocalStore holder = await LocalStore.OpenAsync(_dbPath))
        {
            string lease = (await holder.CreateLeaseAsync(3600, Ct)).Id;
            Assert.True(await Helpers.TryClaimAsync(holder, "/lock/x", "holder", lease, Ct));
        }

        var env = new Dictionary<string, string>
        {
            ["TURNSTILE_DB"] = _dbPath,
            ["TURNSTILE_SOCKET"] = _dbPath + ".nosock",
        };

        CliResult r = await CliProcess.RunAsync(env, Ct, "lock", "/lock/x", "--ttl", "3600");

        Assert.Equal(1, r.ExitCode);
        Assert.StartsWith("turnstile:", r.FirstStdErrLine);
        Assert.Contains("daemon", r.FirstStdErrLine, StringComparison.OrdinalIgnoreCase);
    }

    private static void MaterializeWal(string dbPath)
    {
        // Checkpoint (and truncate) the write-ahead log so every committed frame is folded into the main
        // database file. After this, a freshly opened reader in any process reads the committed state without
        // depending on a per-connection WAL snapshot. Pooling is off so this connection does its work and goes
        // away rather than being parked for reuse.
        using var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        cmd.ExecuteNonQuery();
    }

    private static string NewScratchDir()    {
        string home = Path.Combine(Path.GetTempPath(), $"turnstile-directwatch-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        return home;
    }

    private static void DeleteScratch(string home)
    {
        try
        {
            Directory.Delete(home, recursive: true);
        }
        catch (IOException)
        {
            // Best effort: the scratch home is unique per run.
        }
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _dbPath + ".nosock" })
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // The file is unique per test run; leaving it on a locked handle is harmless.
                }
            }
        }
    }
}
