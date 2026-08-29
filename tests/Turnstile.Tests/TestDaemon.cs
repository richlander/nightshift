namespace Turnstile.Tests;

using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Xunit;

/// <summary>
/// A real Turnstile <see cref="Daemon"/> on a private Unix socket — the one transport that delivers a
/// cross-process watch. This consolidates the start / wait-for-socket / drain-on-dispose boilerplate that the
/// watch suites had each copied inline (see the former <c>DaemonHarness</c> and <c>DaemonOnDb</c>). The socket
/// is generated and owned here; the database is either owned (generated and cleaned up) or caller-supplied, so
/// a reconnect can resume from state another process already committed. Optional <see cref="DaemonOptions"/>
/// keep the specialised needs — a per-instance logging provider, watch hooks — without a second fixture.
/// </summary>
internal sealed class TestDaemon : IAsyncDisposable
{
    private readonly string _dbPath;
    private readonly bool _ownsDb;
    private readonly CancellationTokenSource _cts;
    private readonly Task _run;
    private bool _stopped;

    private TestDaemon(string socket, string dbPath, bool ownsDb, CancellationTokenSource cts, Task run)
    {
        Socket = socket;
        _dbPath = dbPath;
        _ownsDb = ownsDb;
        _cts = cts;
        _run = run;
    }

    /// <summary>The Unix socket the daemon is listening on.</summary>
    public string Socket { get; }

    /// <summary>Starts a daemon on a fresh, owned database (generated here and cleaned up on dispose).</summary>
    public static Task<TestDaemon> StartAsync(CancellationToken ct, DaemonOptions? options = null)
        => StartAsync(NewDbPath(), ownsDb: true, options, ct);

    /// <summary>Starts a daemon on a caller-supplied database (not deleted on dispose — the caller owns it),
    /// so a reconnect can resume from state another process already committed to that file.</summary>
    public static Task<TestDaemon> StartOnDbAsync(string dbPath, CancellationToken ct, DaemonOptions? options = null)
        => StartAsync(dbPath, ownsDb: false, options, ct);

    /// <summary>A unique temp database path, matching the convention the other suites use.</summary>
    public static string NewDbPath()
        => Path.Combine(Path.GetTempPath(), $"turnstile-test-{Guid.NewGuid():N}.db");

    private static async Task<TestDaemon> StartAsync(string dbPath, bool ownsDb, DaemonOptions? options, CancellationToken ct)
    {
        string socket = Path.Combine(Path.GetTempPath(), $"ts-{Guid.NewGuid():N}.sock");
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = Daemon.RunAsync(socket, dbPath, options, cts.Token);

        for (int i = 0; i < 400 && !File.Exists(socket); i++)
        {
            await Task.Delay(25, ct);
        }

        Assert.True(File.Exists(socket), "daemon socket never appeared");
        return new TestDaemon(socket, dbPath, ownsDb, cts, run);
    }

    /// <summary>Stops the daemon and awaits its run task, so all request-handler logging is flushed before a
    /// caller inspects it. Idempotent.</summary>
    public async Task StopAsync()
    {
        if (_stopped)
        {
            return;
        }

        _stopped = true;
        _cts.Cancel();
        try
        {
            await _run;
        }
        catch
        {
            // Shutdown cancellation is expected.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
        SqliteConnection.ClearAllPools();

        List<string> paths = [Socket];
        if (_ownsDb)
        {
            paths.AddRange([_dbPath, _dbPath + "-wal", _dbPath + "-shm"]);
        }

        foreach (string path in paths)
        {
            if (File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (IOException)
                {
                    // The paths are unique per run; a locked handle is harmless.
                }
            }
        }
    }
}
