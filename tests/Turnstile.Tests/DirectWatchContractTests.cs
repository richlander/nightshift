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
/// These are outcome tests of that contract. The failure is deterministic — it is raised the instant
/// <c>WatchAsync</c> is invoked — so proving it needs neither a sleep nor a race. The reconnect half uses a
/// real daemon on a Unix socket (<see cref="RemoteStore"/> over the wire) reopened on the same database file:
/// the smallest genuine transport boundary. The "other writer" is a second, independently opened store over
/// the one file — exactly the multi-instance precondition #199/#202 describe (see
/// <c>ConcurrentWritersTests</c>) — which is why a full external OS process would add cost without adding
/// coverage the contract does not already pin.
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
    public async Task SeparateWriter_LeavesNoParkedLocalWatcher_AndTheDaemonResumesFromTheSavedRevisionWithoutLoss()
    {
        // Process A opens the file directly and reaches the point where it would watch. It records the
        // revision it is caught up to — the cursor a reconnect must resume from — then discovers watch is
        // unavailable here. The rejection is prompt and total: A is never left parked.
        long resumeFrom;
        using (LocalStore a = await LocalStore.OpenAsync(_dbPath))
        {
            await a.CreateAsync("/seed", Bytes("0"), ct: Ct);
            resumeFrom = await a.GetRevisionAsync(Ct);

            Assert.Throws<TurnstileWatchUnavailableException>(() => { _ = a.WatchAsync("/events/", resumeFrom, Ct); });
        }

        // Process B — a genuinely separate store instance over the same file — commits the event A would have
        // waited for. Under the old process-local signal this is exactly the commit A's watcher could never
        // see. Its revision is strictly after the saved cursor.
        const string committedKey = "/events/e1";
        WriteResult committed;
        using (LocalStore b = await LocalStore.OpenAsync(_dbPath))
        {
            committed = await b.CreateAsync(committedKey, Bytes("payload"), ct: Ct);
        }

        Assert.Equal(WriteStatus.Created, committed.Status);
        Assert.True(committed.Revision > resumeFrom, "the separately committed event must be past the saved cursor");

        // Reconnect through a real daemon on the same database and resume from the saved revision. The event
        // committed while only direct stores were open is delivered without loss — the backlog replays it
        // before the one-shot sync, so there is nothing to wait on and no sleep.
        await using var daemon = await DaemonOnDb.StartAsync(_dbPath, Ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        WatchEvent? delivered = null;
        await foreach (WatchMessage msg in remote.WatchAsync("/events/", resumeFrom, Ct))
        {
            if (msg is WatchEventMessage e)
            {
                delivered = e.Event;
                break;
            }
        }

        Assert.NotNull(delivered);
        Assert.Equal(committedKey, delivered!.Key);
        Assert.Equal(committed.Revision, delivered.Revision);
    }

    [Fact]
    public async Task QueuePopWait_InLibraryMode_ReportsUnavailable_RatherThanParking()
    {
        // The CLI mapping (#3): `queue pop --wait` on an empty queue with no daemon falls back to a LocalStore
        // and would block on a watch. The watch-unavailable condition is mapped to the non-success convention
        // (exit 1), not a hang. TURNSTILE_HOME redirects both the default socket and db into a scratch dir, so
        // no daemon answers and the fallback db is disposable.
        string home = Path.Combine(Path.GetTempPath(), $"turnstile-directwatch-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(home);
        string? priorHome = Environment.GetEnvironmentVariable("TURNSTILE_HOME");
        Environment.SetEnvironmentVariable("TURNSTILE_HOME", home);
        try
        {
            int rc = await Helpers.QueueAsync(["pop", "/directwatch-q", "--wait"]);
            Assert.Equal(1, rc);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TURNSTILE_HOME", priorHome);
            SqliteConnection.ClearAllPools();
            try
            {
                Directory.Delete(home, recursive: true);
            }
            catch (IOException)
            {
                // Best effort: the scratch home is unique per run.
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
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

    /// <summary>A real daemon on a Unix socket, opened on a caller-supplied database file so a reconnect can
    /// resume from state another instance already committed. Torn down on dispose; the shared db belongs to the
    /// test and is cleaned up there.</summary>
    private sealed class DaemonOnDb : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts;
        private readonly Task _run;

        private DaemonOnDb(string socket, CancellationTokenSource cts, Task run)
        {
            Socket = socket;
            _cts = cts;
            _run = run;
        }

        public string Socket { get; }

        public static async Task<DaemonOnDb> StartAsync(string dbPath, CancellationToken ct)
        {
            string socket = Path.Combine(Path.GetTempPath(), $"ts-directwatch-{Guid.NewGuid():N}.sock");
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task run = Daemon.RunAsync(socket, dbPath, cts.Token);
            for (int i = 0; i < 400 && !File.Exists(socket); i++)
            {
                await Task.Delay(25, ct);
            }

            Assert.True(File.Exists(socket), "daemon socket never appeared");
            return new DaemonOnDb(socket, cts, run);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _run;
            }
            catch
            {
                // Shutdown cancellation is expected.
            }

            _cts.Dispose();
            if (File.Exists(Socket))
            {
                try
                {
                    File.Delete(Socket);
                }
                catch (IOException)
                {
                    // The socket path is unique per run.
                }
            }
        }
    }
}
