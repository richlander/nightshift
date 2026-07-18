namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
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

    [Fact]
    public async Task Predicate_ClaimDeleteThenReadyPut_ReturnsRequeue()
    {
        string orderBase = "/plan/77/order/op-r";
        OrderRef order = OrderRef.FromBase(orderBase)!.Value;
        string claimKey = $"{orderBase}/claim";
        string readyKey = order.ReadyKey;

        var values = new Dictionary<string, string>
        {
            [claimKey] = "agent-a",
        };
        var predicate = new CoordinateCommand.CoordinatePredicate();

        values.Remove(claimKey);
        CoordinateCommand.CoordinateOutcome? afterClaimDelete = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(claimKey, Deleted: true, Revision: 50)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.Null(afterClaimDelete);

        values[readyKey] = orderBase;
        CoordinateCommand.CoordinateOutcome? afterReadyPut = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("ready", new WatchSignal(readyKey, Deleted: false, Revision: 51)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.NotNull(afterReadyPut);
        Assert.Equal(
            $"COORD plan=/plan/77 order={orderBase} transition=requeued status=ready",
            afterReadyPut!.Render());
        Assert.Equal(ExitCode.Coordinate, afterReadyPut.ExitCode);
    }

    [Fact]
    public async Task FilteredWaitEngine_NonActionableEdge_RearmsUntilPredicateMatches()
    {
        WatchSignal[] signals =
        [
            new("/plan/9/order/op-z/state", Deleted: false, Revision: 41),
            new("/plan/9/order/op-z/state", Deleted: false, Revision: 42),
        ];
        int reconcileCalls = 0;

        FilteredWaitEngine.WaitResult<CoordinateCommand.CoordinateOutcome> result = await FilteredWaitEngine.WaitForMatchAsync(
            scopes:
            [
                new FilteredWaitEngine.WatchScope(
                    "plan",
                    (from, _) => Replay(signals.Where(s => s.Revision > from).Take(1).ToArray())),
            ],
            currentRevision: _ => Task.FromResult(42L),
            fromRevision: 40,
            deadline: DateTime.UtcNow.AddSeconds(2),
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (edge, _) =>
            {
                reconcileCalls++;
                return Task.FromResult(
                    reconcileCalls == 1
                        ? null
                        : CoordinateCommand.CoordinateOutcome.Action("/plan/9/order/op-z", "done", "done"));
            },
            TestContext.Current.CancellationToken);

        Assert.False(result.TimedOut);
        Assert.NotNull(result.Match);
        Assert.Equal(2, reconcileCalls);
        Assert.Equal(
            "COORD plan=/plan/9 order=/plan/9/order/op-z transition=done status=done",
            result.Match!.Render());
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
                    (from, token) =>
                    {
                        watchCalls++;
                        return Replay([]);
                    }),
            ],
            keepAliveAsync: _ => Task.CompletedTask,
            reconcileAsync: (_, _) => Task.FromResult<CoordinateCommand.CoordinateOutcome?>(null),
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(1, probeCalls);
        Assert.Equal(0, revisionCalls);
        Assert.Equal(0, watchCalls);
    }

    private static Func<string, CancellationToken, Task<KvItem?>> BuildGetter(Dictionary<string, string> values)
        => (key, _) => Task.FromResult(values.TryGetValue(key, out string? value) ? Item(key, value) : null);

    private static async IAsyncEnumerable<WatchSignal> Replay(WatchSignal[] signals)
    {
        foreach (WatchSignal signal in signals)
        {
            yield return signal;
            await Task.Yield();
        }
    }

    private static KvItem Item(string key, string value)
        => new(key, CreateRevision: 1, ModRevision: 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(value));
}
