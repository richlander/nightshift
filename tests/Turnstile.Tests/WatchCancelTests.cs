namespace Turnstile.Tests;

using System.Net.Sockets;
using System.Text;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Exercises watch cancellation across the real daemon boundary — a live <see cref="Daemon"/> listening on a
/// Unix socket with a real <see cref="RemoteStore"/> (and, for the abrupt case, a raw socket) — rather than a
/// mocked helper. Regression cover for #87: normal watch cancellation / stream close must not emit exception
/// noise on the coordination path, while cancellation semantics and real failures are preserved.
/// </summary>
public sealed class WatchCancelTests : IAsyncDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-watchcancel-{Guid.NewGuid():N}.db");
    private readonly string _socketPath = Path.Combine(Path.GetTempPath(), $"ts-{Guid.NewGuid():N}.sock");
    private readonly CancellationTokenSource _daemonCts = new();
    private readonly Task _daemon;

    public WatchCancelTests() => _daemon = Daemon.RunAsync(_socketPath, _dbPath, _daemonCts.Token);

    private async Task WaitForSocketAsync(CancellationToken ct)
    {
        for (int i = 0; i < 400 && !File.Exists(_socketPath); i++)
        {
            await Task.Delay(25, ct);
        }

        Assert.True(File.Exists(_socketPath), "daemon socket never appeared");
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task CancellingWatch_ThrowsOperationCanceled_LeavesDaemonQuietAndHealthy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var unobserved = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) { unobserved.Add(e.Exception); }
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await WaitForSocketAsync(ct);
            using RemoteStore store = RemoteStore.Connect(_socketPath);
            await store.CreateAsync("/a", Bytes("1"), ct: ct);

            // Consumer pattern from the coordination path: watch, and cancel while suspended awaiting the
            // next event. Cancellation must surface as OperationCanceledException (semantics preserved).
            using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await foreach (WatchMessage msg in store.WatchAsync("/", 0, watchCts.Token))
                {
                    if (msg is WatchSyncMessage)
                    {
                        watchCts.CancelAfter(50);
                    }
                }
            });

            // Surface any unobserved task exceptions left behind by the abandoned stream.
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50, ct);
            }

            // The daemon must remain healthy after a cancelled watch: a fresh watch still drains the backlog
            // and reaches sync, and a fresh write still commits.
            using RemoteStore store2 = RemoteStore.Connect(_socketPath);
            await store2.CreateAsync("/b", Bytes("2"), ct: ct);
            bool sawSync = false;
            using var followCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            await foreach (WatchMessage msg in store2.WatchAsync("/", 0, followCts.Token))
            {
                if (msg is WatchSyncMessage)
                {
                    sawSync = true;
                    followCts.Cancel();
                }
            }

            Assert.True(sawSync, "daemon did not serve a follow-up watch after a cancelled one");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The follow-up watch's own cancellation completing the await foreach — expected teardown.
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.True(
                unobserved.Count == 0,
                "watch cancellation left unobserved task exceptions:\n" + string.Join("\n", unobserved));
        }
    }

    [Fact]
    public async Task AbruptClientReset_MidWrite_DaemonStaysQuietAndHealthy()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var unobserved = new List<Exception>();
        void OnUnobserved(object? _, UnobservedTaskExceptionEventArgs e)
        {
            lock (unobserved) { unobserved.Add(e.Exception); }
            e.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await WaitForSocketAsync(ct);

            // Preload a large backlog so the daemon has plenty to stream and blocks on a full send buffer
            // when the client stops reading — the window in which a disconnect races an in-flight write.
            using (RemoteStore seed = RemoteStore.Connect(_socketPath))
            {
                byte[] big = Bytes(new string('x', 4096));
                for (int i = 0; i < 2000; i++)
                {
                    await seed.CreateAsync($"/k/{i:D5}", big, ct: ct);
                }
            }

            // Raw socket: request the watch, read only the headers, then abruptly RST the connection while
            // the daemon is mid-write. The daemon must treat this as a normal disconnect (no noise), not
            // an unhandled exception.
            using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(_socketPath), ct);
                await socket.SendAsync(Encoding.ASCII.GetBytes("GET /watch?from=0 HTTP/1.1\r\nHost: localhost\r\n\r\n"), SocketFlags.None, ct);
                await socket.ReceiveAsync(new byte[512], SocketFlags.None, ct); // headers only, then stop reading
                await Task.Delay(300, ct);                                      // let the send buffer fill
                socket.LingerState = new LingerOption(true, 0);                 // force RST on close
            }

            await Task.Delay(500, ct);
            for (int i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(50, ct);
            }

            // The daemon must still serve after the abrupt reset.
            using RemoteStore store = RemoteStore.Connect(_socketPath);
            long revision = await store.GetRevisionAsync(ct);
            Assert.True(revision >= 2000, $"daemon unhealthy after abrupt reset (revision={revision})");
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }

        lock (unobserved)
        {
            Assert.True(
                unobserved.Count == 0,
                "abrupt client reset left unobserved task exceptions:\n" + string.Join("\n", unobserved));
        }
    }

    [Fact]
    public async Task RealWriteFailure_StillSurfaces_ThroughTheDaemon()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await WaitForSocketAsync(ct);
        using RemoteStore store = RemoteStore.Connect(_socketPath);

        // A genuinely invalid request (an oversized value the daemon rejects with 400) must still fail
        // loudly: the disconnect handling only quietens teardown after RequestAborted, never errors raised
        // while the request is live.
        byte[] tooBig = new byte[(64 * 1024) + 1];
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await store.CreateAsync("/oversized", tooBig, ct: ct));
    }

    public async ValueTask DisposeAsync()
    {
        _daemonCts.Cancel();
        try { await _daemon; } catch { /* expected on shutdown */ }
        _daemonCts.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm", _socketPath })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }
}
