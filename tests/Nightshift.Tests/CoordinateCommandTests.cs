namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Config;
using Nightshift.Turnstile;
using Xunit;

public class CoordinateCommandTests
{
    [Theory]
    [InlineData("done")]
    [InlineData("landed")]
    [InlineData("escalated")]
    public async Task Predicate_StateTransition_ReturnsCoordPayload(string status)
    {
        string orderBase = "/plan/12/order/op-a";
        string stateKey = $"{orderBase}/state";
        var values = new Dictionary<string, string> { [stateKey] = $"{{\"status\":\"{status}\"}}" };
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(stateKey, Deleted: false, Revision: 44)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(
            $"COORD plan=/plan/12 order={orderBase} transition={status} status={status}",
            outcome!.Render());
        Assert.Equal(ExitCode.Coordinate, outcome.ExitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("changes-requested")]
    public async Task Predicate_ClaimDelete_WithRequeueEligibleState_ReturnsRequeue(string? status)
    {
        string orderBase = "/plan/77/order/op-r";
        string claimKey = $"{orderBase}/claim";
        string stateKey = $"{orderBase}/state";
        var values = new Dictionary<string, string>();
        if (status is not null)
        {
            values[stateKey] = $"{{\"status\":\"{status}\"}}";
        }

        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(claimKey, Deleted: true, Revision: 50)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(
            $"COORD plan=/plan/77 order={orderBase} transition=requeued status=ready",
            outcome!.Render());
    }

    [Theory]
    [InlineData("done")]
    [InlineData("landed")]
    [InlineData("blocked")]
    [InlineData("escalated")]
    [InlineData("refused")]
    [InlineData("declined")]
    public async Task Predicate_ClaimDelete_WithIneligibleState_DoesNotReturnRequeue(string status)
    {
        string orderBase = "/plan/77/order/op-r";
        string claimKey = $"{orderBase}/claim";
        string stateKey = $"{orderBase}/state";
        var values = new Dictionary<string, string>
        {
            [stateKey] = $"{{\"status\":\"{status}\"}}",
        };

        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(claimKey, Deleted: true, Revision: 50)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task WaitForActionCore_BlockingMode_IsEdgeTriggeredAndSkipsStandingProbe()
    {
        int probeCalls = 0;
        int reconcileCalls = 0;

        CoordinateCommand.CoordinateOutcome? result = await CoordinateCommand.WaitForActionCoreAsync(
            once: false,
            timeoutSecs: 5,
            probeActionableAsync: _ =>
            {
                probeCalls++;
                return Task.FromResult<CoordinateCommand.CoordinateOutcome?>(
                    CoordinateCommand.CoordinateOutcome.Action("/plan/1/order/op-probe", "done", "done"));
            },
            currentRevision: _ => Task.FromResult(40L),
            scopes:
            [
                new FilteredWaitEngine.WatchScope(
                    "plan",
                    (from, _) => Replay([new WatchSignal("/plan/1/order/op-edge/state", Deleted: false, Revision: 41)])),
            ],
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (edge, _) =>
            {
                reconcileCalls++;
                return Task.FromResult<CoordinateCommand.CoordinateOutcome?>(
                    CoordinateCommand.CoordinateOutcome.Action("/plan/1/order/op-edge", "done", "done"));
            },
            reconcileSnapshotAsync: _ => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            TestContext.Current.CancellationToken);

        Assert.Equal(0, probeCalls);
        Assert.Equal(1, reconcileCalls);
        Assert.NotNull(result);
        Assert.Equal(
            "COORD plan=/plan/1 order=/plan/1/order/op-edge transition=done status=done",
            result!.Render());
    }

    [Fact]
    public async Task WaitForActionCore_Once_UsesProbeAndDoesNotStartWatchers()
    {
        int probeCalls = 0;
        int revisionCalls = 0;
        int watchCalls = 0;

        CoordinateCommand.CoordinateOutcome? result = await CoordinateCommand.WaitForActionCoreAsync(
            once: true,
            timeoutSecs: null,
            probeActionableAsync: _ =>
            {
                probeCalls++;
                return Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null);
            },
            currentRevision: _ =>
            {
                revisionCalls++;
                return Task.FromResult(1L);
            },
            scopes:
            [
                new FilteredWaitEngine.WatchScope(
                    "plan",
                    (_, _) =>
                    {
                        watchCalls++;
                        return Replay([]);
                    }),
            ],
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (_, _) => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            reconcileSnapshotAsync: _ => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, probeCalls);
        Assert.Equal(0, revisionCalls);
        Assert.Equal(0, watchCalls);
    }

    [Fact]
    public async Task FilteredWaitEngine_PerScopeFloor_DoesNotDropLosingScopeEdge()
    {
        WatchSignal[] planSignals = [new("/plan/9/order/op-z/state", Deleted: false, Revision: 42)];
        WatchSignal[] controlSignals = [new("/control/noise", Deleted: false, Revision: 43)];
        int reconcileCalls = 0;

        FilteredWaitEngine.WaitResult<CoordinateCommand.CoordinateOutcome> result = await FilteredWaitEngine.WaitForMatchAsync(
            scopes:
            [
                new FilteredWaitEngine.WatchScope(
                    "plan",
                    (from, token) => ReplayOrPark(
                        planSignals.Where(s => s.Revision > from).Take(1).ToArray(),
                        delay: TimeSpan.FromMilliseconds(80),
                        token)),
                new FilteredWaitEngine.WatchScope(
                    "control",
                    (from, token) => ReplayOrPark(
                        controlSignals.Where(s => s.Revision > from).Take(1).ToArray(),
                        delay: TimeSpan.FromMilliseconds(10),
                        token)),
            ],
            currentRevision: _ => Task.FromResult(99L),
            fromRevision: 40,
            deadline: DateTime.UtcNow.AddSeconds(2),
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (edge, _) =>
            {
                reconcileCalls++;
                return Task.FromResult(
                    edge.Scope == "plan"
                        ? CoordinateCommand.CoordinateOutcome.Action("/plan/9/order/op-z", "done", "done")
                        : null);
            },
            reconcileSnapshotAsync: _ => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.NotNull(result.Match);
        Assert.Equal(2, reconcileCalls);
        Assert.Equal(
            "COORD plan=/plan/9 order=/plan/9/order/op-z transition=done status=done",
            result.Match!.Render());
    }

    [Fact]
    public async Task FilteredWaitEngine_OnCompaction_RunsFullReconcile()
    {
        int watchCalls = 0;
        int snapshotCalls = 0;

        FilteredWaitEngine.WaitResult<CoordinateCommand.CoordinateOutcome> result = await FilteredWaitEngine.WaitForMatchAsync(
            scopes:
            [
                new FilteredWaitEngine.WatchScope(
                    "plan",
                    (from, _) =>
                    {
                        watchCalls++;
                        if (watchCalls == 1)
                        {
                            throw new WatchCompactedException("/plan/", from, compactRevision: 120);
                        }

                        return Replay([]);
                    }),
            ],
            currentRevision: _ => Task.FromResult(120L),
            fromRevision: 100,
            deadline: DateTime.UtcNow.AddSeconds(2),
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (_, _) => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            reconcileSnapshotAsync: _ =>
            {
                snapshotCalls++;
                return Task.FromResult<CoordinateCommand.CoordinateOutcome?>(
                    CoordinateCommand.CoordinateOutcome.Action("/plan/8/order/op-c", "escalated", "escalated"));
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.NotNull(result.Match);
        Assert.Equal(1, snapshotCalls);
        Assert.Equal(
            "COORD plan=/plan/8 order=/plan/8/order/op-c transition=escalated status=escalated",
            result.Match!.Render());
    }

    private static Func<string, CancellationToken, Task<KvItem?>> BuildGetter(Dictionary<string, string> values)
        => (key, _) => Task.FromResult(values.TryGetValue(key, out string? value) ? Item(key, value) : null);

    private static async IAsyncEnumerable<WatchSignal> Replay(WatchSignal[] signals, TimeSpan? delay = null)
    {
        foreach (WatchSignal signal in signals)
        {
            if (delay is { } d && d > TimeSpan.Zero)
            {
                await Task.Delay(d, TestContext.Current.CancellationToken);
            }

            yield return signal;
            await Task.Yield();
        }
    }

    private static IAsyncEnumerable<WatchSignal> ReplayOrPark(WatchSignal[] signals, TimeSpan delay, CancellationToken ct)
        => signals.Length > 0 ? Replay(signals, delay) : Park(ct);

    private static async IAsyncEnumerable<WatchSignal> Park([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        yield break;
    }

    private static KvItem Item(string key, string value)
        => new(key, CreateRevision: 1, ModRevision: 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(value));
}

public sealed class CoordinateCommandIntegrationTests : IClassFixture<TurnstileFixture>
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    private readonly TurnstileFixture _fixture;

    public CoordinateCommandIntegrationTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OnceProbe_WithStandingRequeue_ReturnsCoordRequeued()
    {
        string scope = $"coord-once-{Guid.NewGuid():N}";
        string orderBase = $"/plan/{scope}/order/op1";
        string readyKey = $"/ready/{scope}/op1";
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (TurnstileClient client = _fixture.Connect())
        {
            await client.SetAsync($"{orderBase}/branch", $"nightshift/{scope}/op1", ct);
            await client.SetAsync(readyKey, orderBase, ct);
            await client.DeleteAsync($"{orderBase}/claim", ct);
        }

        InvocationResult result = await InvokeCoordinateAsync(scope, timeoutSecs: null, once: true);
        Assert.Equal(ExitCode.Coordinate, result.ExitCode);
        Assert.Equal(
            $"COORD plan=/plan/{scope} order={orderBase} transition=requeued status=ready{Environment.NewLine}",
            result.Stdout);
        Assert.Empty(result.Stderr);
    }

    private async Task<InvocationResult> InvokeCoordinateAsync(string? scope, int? timeoutSecs, bool once)
    {
        await ConsoleLock.WaitAsync(TestContext.Current.CancellationToken);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        string? originalTurnstileSocket = Environment.GetEnvironmentVariable("TURNSTILE_SOCKET");
        string? originalNightshiftSocket = Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET");
        string? originalNightshiftConfig = Environment.GetEnvironmentVariable("NIGHTSHIFT_CONFIG");
        string? originalRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtimeDir = Path.Combine(AppContext.BaseDirectory, "coordinate-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        try
        {
            SocketResolver.UseFlag(null);
            Environment.SetEnvironmentVariable("TURNSTILE_SOCKET", _fixture.Socket);
            Environment.SetEnvironmentVariable("NIGHTSHIFT_SOCKET", null);
            Environment.SetEnvironmentVariable("NIGHTSHIFT_CONFIG", null);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);

            Console.SetOut(stdout);
            Console.SetError(stderr);

            int exitCode = await CoordinateCommand.RunAsync(scope, timeoutSecs, once);
            return new InvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("TURNSTILE_SOCKET", originalTurnstileSocket);
            Environment.SetEnvironmentVariable("NIGHTSHIFT_SOCKET", originalNightshiftSocket);
            Environment.SetEnvironmentVariable("NIGHTSHIFT_CONFIG", originalNightshiftConfig);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", originalRuntime);
            try
            {
                Directory.Delete(runtimeDir, recursive: true);
            }
            catch (Exception)
            {
            }

            ConsoleLock.Release();
        }
    }

    private sealed record InvocationResult(int ExitCode, string Stdout, string Stderr);
}
