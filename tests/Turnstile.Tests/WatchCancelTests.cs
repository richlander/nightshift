namespace Turnstile.Tests;

using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Watch cancellation / teardown exercised across the real daemon boundary — a live <see cref="Daemon"/> on a
/// Unix socket with a real <see cref="RemoteStore"/> (and, for the abrupt case, a raw socket). Regression cover
/// for #87: a watch ending by client cancellation or stream close must not surface as a request-handler error
/// on the coordination path, while a genuine failure raised while the request is still live must still be
/// logged and propagated. Request-handler logging is observed through a per-daemon logging provider (not a
/// process-global hook), and the daemon is drained (its run task awaited) before logs are asserted, so no
/// assertion depends on a sleep or a GC.
/// </summary>
public sealed class WatchCancelTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task CancellingWatch_PreservesCancellation_EmitsNoRequestHandlerError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonHarness.StartAsync(hooks: null, ct);
        using RemoteStore store = RemoteStore.Connect(daemon.SocketPath);
        await store.CreateAsync("/a", Bytes("1"), ct: ct);

        // Coordination pattern: watch, and cancel while suspended awaiting the next event. Cancellation must
        // surface to the caller as OperationCanceledException — the watch semantics are preserved.
        using var watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (WatchMessage msg in store.WatchAsync("/", 0, watchCts.Token))
            {
                if (msg is WatchSyncMessage)
                {
                    watchCts.Cancel();
                }
            }
        });

        // The daemon is still healthy: a fresh unary call round-trips.
        Assert.True(await store.GetRevisionAsync(ct) >= 1);

        await daemon.StopAsync();
        AssertNoRequestHandlerError(daemon);
    }

    [Fact]
    public async Task AbruptClientReset_MidStream_EmitsNoRequestHandlerError()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonHarness.StartAsync(hooks: null, ct);

        // Preload a backlog so the daemon is actively streaming when the client vanishes.
        using (RemoteStore seed = RemoteStore.Connect(daemon.SocketPath))
        {
            byte[] big = Bytes(new string('x', 4096));
            for (int i = 0; i < 2000; i++)
            {
                await seed.CreateAsync($"/k/{i:D5}", big, ct: ct);
            }
        }

        // Raw socket: ask for the watch, read one chunk of the stream, then abruptly RST the connection while
        // the daemon is still writing. This is the exact race the teardown ordering must survive.
        using (var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified))
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(daemon.SocketPath), ct);
            await socket.SendAsync(Encoding.ASCII.GetBytes("GET /watch?from=0 HTTP/1.1\r\nHost: localhost\r\n\r\n"), SocketFlags.None, ct);
            await socket.ReceiveAsync(new byte[1024], SocketFlags.None, ct); // headers + first bytes of the stream
            socket.LingerState = new LingerOption(true, 0);                  // force RST rather than a graceful FIN
        }

        // The daemon still serves after the reset.
        using RemoteStore store = RemoteStore.Connect(daemon.SocketPath);
        Assert.True(await store.GetRevisionAsync(ct) >= 2000);

        await daemon.StopAsync();
        AssertNoRequestHandlerError(daemon);
    }

    [Fact]
    public async Task LiveWatchFailure_WhileConnected_IsLoggedAndPropagated()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string marker = $"live-message-failure-{Guid.NewGuid():N}";
        var hooks = new WatchHooks { BeforeMessageWrite = () => throw new NotSupportedException(marker) };
        await using var daemon = await DaemonHarness.StartAsync(hooks, ct);

        using RemoteStore store = RemoteStore.Connect(daemon.SocketPath);
        await store.CreateAsync("/a", Bytes("1"), ct: ct);

        // The failure is raised while the client is connected (RequestAborted is false), so it must NOT be
        // treated as a disconnect: it surfaces to the client and is logged by the daemon.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (WatchMessage msg in store.WatchAsync("/", 0, ct))
            {
            }
        });

        await daemon.StopAsync();
        Assert.Contains(
            daemon.Logs,
            e => e.Exception is NotSupportedException nse && nse.Message == marker);
    }

    [Fact]
    public async Task HeartbeatTeardown_ObservesPendingMove_SoFailurePropagatesUnmasked()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string marker = $"live-heartbeat-failure-{Guid.NewGuid():N}";
        var hooks = new WatchHooks
        {
            // A short heartbeat so the test does not wait on the real 30s cadence, and a failure raised from
            // the heartbeat write — the one spot where a move is genuinely in flight. If teardown disposes the
            // enumerator without first observing that move, the state machine throws "Concurrent operations are
            // not supported", which replaces this marker in the finally and is what the pre-fix code logged.
            HeartbeatInterval = TimeSpan.FromMilliseconds(50),
            BeforeHeartbeatWrite = () => throw new NotSupportedException(marker),
        };
        await using var daemon = await DaemonHarness.StartAsync(hooks, ct);

        using RemoteStore store = RemoteStore.Connect(daemon.SocketPath);
        await store.CreateAsync("/a", Bytes("1"), ct: ct);
        long tip = await store.GetRevisionAsync(ct);

        // Watch from the tip: the backlog is empty, so after the sync message the move waits — it is in flight
        // when the heartbeat fires.
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (WatchMessage msg in store.WatchAsync("/", tip, ct))
            {
            }
        });

        await daemon.StopAsync();

        var loggedNotSupported = daemon.Logs
            .Select(e => e.Exception)
            .OfType<NotSupportedException>()
            .ToList();

        // Our live failure surfaced and was logged...
        Assert.Contains(loggedNotSupported, e => e.Message == marker);

        // ...and nothing else did: pre-fix, disposing the enumerator while the move was in flight threw its
        // own NotSupportedException ("Specified method is not supported.") from DisposeAsync, which replaced
        // this marker in the finally. Observing the move before disposing is what keeps the real failure intact.
        Assert.All(loggedNotSupported, e => Assert.Equal(marker, e.Message));
    }

    private static void AssertNoRequestHandlerError(DaemonHarness daemon)
    {
        var errors = daemon.Logs.Where(e => e.Level >= LogLevel.Error).ToList();
        Assert.True(
            errors.Count == 0,
            "watch teardown logged request-handler error(s):\n" + string.Join("\n", errors.Select(e => $"[{e.Level}] {e.Category}: {e.Message} :: {e.Exception}")));
    }

    /// <summary>Adds this suite's per-instance log capture on top of the shared <see cref="TestDaemon"/>
    /// lifecycle (start, wait-for-socket, drain-on-stop, cleanup), so the request-handler logging can be
    /// asserted without duplicating the daemon boilerplate the other watch suite also needs.</summary>
    private sealed class DaemonHarness(TestDaemon daemon, CapturingLoggerProvider logs) : IAsyncDisposable
    {
        public string SocketPath => daemon.Socket;

        public IReadOnlyList<CapturedLog> Logs => logs.Entries;

        public static async Task<DaemonHarness> StartAsync(WatchHooks? hooks, CancellationToken ct)
        {
            var logs = new CapturingLoggerProvider();
            TestDaemon daemon = await TestDaemon.StartAsync(
                ct,
                new DaemonOptions { LoggerProvider = logs, WatchHooks = hooks ?? WatchHooks.None });
            return new DaemonHarness(daemon, logs);
        }

        public Task StopAsync() => daemon.StopAsync();

        public ValueTask DisposeAsync() => daemon.DisposeAsync();
    }

    private sealed record CapturedLog(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<CapturedLog> _entries = new();

        public IReadOnlyList<CapturedLog> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _entries);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
                => entries.Enqueue(new CapturedLog(category, logLevel, formatter(state, exception), exception));
        }
    }
}
