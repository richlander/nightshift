namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Config;
using Nightshift.Turnstile;
using Xunit;

// Stacked orders §5 — land a stack in topological order: a dependent is landable only once every order in
// its spec `after` has itself reached `landed`. These tests exercise the topological gate at each seam it
// touches: the shared predicate (AreDependenciesLandedAsync), the edge-triggered coordinate predicate, the
// standing once-probe, and the LandCommand refusal.
public class LandStackTests
{
    [Fact]
    public async Task AreDependenciesLanded_NoAfterDeps_IsLandable()
    {
        var getter = BuildGetter(new()
        {
            ["/plan/1/order/solo/spec"] = "{\"plan\":\"1\",\"order\":\"solo\"}",
        });

        Assert.True(await CoordinateCommand.AreDependenciesLandedAsync(
            getter, "/plan/1/order/solo", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AreDependenciesLanded_DependencyLanded_IsLandable()
    {
        var getter = BuildGetter(new()
        {
            ["/plan/1/order/child/spec"] = "{\"plan\":\"1\",\"order\":\"child\",\"after\":[\"contract\"]}",
            ["/plan/1/order/contract/state"] = "{\"status\":\"landed\"}",
        });

        Assert.True(await CoordinateCommand.AreDependenciesLandedAsync(
            getter, "/plan/1/order/child", TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("done")]
    [InlineData("changes-requested")]
    [InlineData("ready")]
    public async Task AreDependenciesLanded_DependencyNotLanded_IsNotLandable(string depStatus)
    {
        var getter = BuildGetter(new()
        {
            ["/plan/1/order/child/spec"] = "{\"plan\":\"1\",\"order\":\"child\",\"after\":[\"contract\"]}",
            ["/plan/1/order/contract/state"] = $"{{\"status\":\"{depStatus}\"}}",
        });

        Assert.False(await CoordinateCommand.AreDependenciesLandedAsync(
            getter, "/plan/1/order/child", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AreDependenciesLanded_DependencyStateMissing_FailsClosed()
    {
        var getter = BuildGetter(new()
        {
            ["/plan/1/order/child/spec"] = "{\"plan\":\"1\",\"order\":\"child\",\"after\":[\"contract\"]}",
        });

        Assert.False(await CoordinateCommand.AreDependenciesLandedAsync(
            getter, "/plan/1/order/child", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AreDependenciesLanded_MultipleDeps_RequiresAllLanded()
    {
        var getter = BuildGetter(new()
        {
            ["/plan/1/order/child/spec"] = "{\"plan\":\"1\",\"order\":\"child\",\"after\":[\"a\",\"b\"]}",
            ["/plan/1/order/a/state"] = "{\"status\":\"landed\"}",
            ["/plan/1/order/b/state"] = "{\"status\":\"done\"}",
        });

        Assert.False(await CoordinateCommand.AreDependenciesLandedAsync(
            getter, "/plan/1/order/child", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Predicate_DoneChild_WithUnlandedContract_IsGated()
    {
        string child = "/plan/12/order/child";
        var getter = BuildGetter(new()
        {
            [$"{child}/state"] = "{\"status\":\"done\"}",
            [$"{child}/spec"] = "{\"plan\":\"12\",\"order\":\"child\",\"after\":[\"contract\"]}",
        });
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal($"{child}/state", Deleted: false, Revision: 44)),
            getter,
            TestContext.Current.CancellationToken);

        Assert.Null(outcome);
    }

    [Fact]
    public async Task Predicate_DoneChild_WithLandedContract_SurfacesDone()
    {
        string child = "/plan/12/order/child";
        var getter = BuildGetter(new()
        {
            [$"{child}/state"] = "{\"status\":\"done\"}",
            [$"{child}/spec"] = "{\"plan\":\"12\",\"order\":\"child\",\"after\":[\"contract\"]}",
            ["/plan/12/order/contract/state"] = "{\"status\":\"landed\"}",
        });
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal($"{child}/state", Deleted: false, Revision: 44)),
            getter,
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(
            $"COORD plan=/plan/12 order={child} transition=done status=done",
            outcome!.Render());
    }

    [Fact]
    public async Task Predicate_DoneOrder_NoDeps_SurfacesDone()
    {
        string solo = "/plan/12/order/solo";
        var getter = BuildGetter(new()
        {
            [$"{solo}/state"] = "{\"status\":\"done\"}",
            [$"{solo}/spec"] = "{\"plan\":\"12\",\"order\":\"solo\"}",
        });
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal($"{solo}/state", Deleted: false, Revision: 44)),
            getter,
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(
            $"COORD plan=/plan/12 order={solo} transition=done status=done",
            outcome!.Render());
    }

    [Fact]
    public async Task Predicate_LandedContract_StaysUngated()
    {
        string contract = "/plan/12/order/contract";
        var getter = BuildGetter(new()
        {
            [$"{contract}/state"] = "{\"status\":\"landed\"}",
        });
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal($"{contract}/state", Deleted: false, Revision: 44)),
            getter,
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal(
            $"COORD plan=/plan/12 order={contract} transition=landed status=landed",
            outcome!.Render());
    }

    private static Func<string, CancellationToken, Task<KvItem?>> BuildGetter(Dictionary<string, string> values)
        => (key, _) => Task.FromResult(values.TryGetValue(key, out string? value) ? Item(key, value) : null);

    private static KvItem Item(string key, string value)
        => new(key, CreateRevision: 1, ModRevision: 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(value));
}

public sealed class LandStackIntegrationTests : IClassFixture<TurnstileFixture>
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    private readonly TurnstileFixture _fixture;

    public LandStackIntegrationTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Land_ChildWithUnlandedContract_Refuses()
    {
        string scope = $"land-stack-{Guid.NewGuid():N}";
        string child = $"/plan/{scope}/order/child";
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (TurnstileClient client = _fixture.Connect())
        {
            await client.SetAsync($"{child}/spec", $"{{\"plan\":\"{scope}\",\"order\":\"child\",\"after\":[\"contract\"]}}", ct);
            await client.SetAsync($"{child}/state", "{\"status\":\"done\"}", ct);
        }

        InvocationResult result = await InvokeLandAsync(child, reason: null);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("unlanded dependencies", result.Stderr);

        using (TurnstileClient client = _fixture.Connect())
        {
            KvItem? state = await client.GetAsync($"{child}/state", ct);
            Assert.NotNull(state);
            Assert.Contains("\"status\":\"done\"", state!.Text);
        }
    }

    [Fact]
    public async Task Land_ChildAfterContractLanded_Lands()
    {
        string scope = $"land-stack-{Guid.NewGuid():N}";
        string child = $"/plan/{scope}/order/child";
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (TurnstileClient client = _fixture.Connect())
        {
            await client.SetAsync($"{child}/spec", $"{{\"plan\":\"{scope}\",\"order\":\"child\",\"after\":[\"contract\"]}}", ct);
            await client.SetAsync($"{child}/state", "{\"status\":\"done\"}", ct);
            await client.SetAsync($"/plan/{scope}/order/contract/state", "{\"status\":\"landed\"}", ct);
        }

        InvocationResult result = await InvokeLandAsync(child, reason: null);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"LANDED {child}{Environment.NewLine}", result.Stdout);

        using (TurnstileClient client = _fixture.Connect())
        {
            KvItem? state = await client.GetAsync($"{child}/state", ct);
            Assert.NotNull(state);
            Assert.Contains("\"status\":\"landed\"", state!.Text);
        }
    }

    [Fact]
    public async Task OnceProbe_DoneChild_WithUnlandedContract_NotSurfaced()
    {
        string scope = $"land-stack-{Guid.NewGuid():N}";
        string child = $"/plan/{scope}/order/child";
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (TurnstileClient client = _fixture.Connect())
        {
            await client.SetAsync($"{child}/spec", $"{{\"plan\":\"{scope}\",\"order\":\"child\",\"after\":[\"contract\"]}}", ct);
            await client.SetAsync($"{child}/state", "{\"status\":\"done\"}", ct);
        }

        InvocationResult result = await InvokeCoordinateAsync(scope, timeoutSecs: null, once: true);

        Assert.Equal(ExitCode.NoCoordinate, result.ExitCode);
    }

    [Fact]
    public async Task OnceProbe_DoneChild_AfterContractLanded_SurfacesDone()
    {
        string scope = $"land-stack-{Guid.NewGuid():N}";
        string child = $"/plan/{scope}/order/child";
        CancellationToken ct = TestContext.Current.CancellationToken;

        using (TurnstileClient client = _fixture.Connect())
        {
            await client.SetAsync($"{child}/spec", $"{{\"plan\":\"{scope}\",\"order\":\"child\",\"after\":[\"contract\"]}}", ct);
            await client.SetAsync($"{child}/state", "{\"status\":\"done\"}", ct);
            await client.SetAsync($"/plan/{scope}/order/contract/state", "{\"status\":\"landed\"}", ct);
        }

        InvocationResult result = await InvokeCoordinateAsync(scope, timeoutSecs: null, once: true);

        Assert.Equal(ExitCode.Coordinate, result.ExitCode);
        Assert.Equal(
            $"COORD plan=/plan/{scope} order={child} transition=done status=done{Environment.NewLine}",
            result.Stdout);
    }

    private Task<InvocationResult> InvokeLandAsync(string orderBase, string? reason)
        => InvokeAsync(() => LandCommand.RunAsync(orderBase, reason));

    private Task<InvocationResult> InvokeCoordinateAsync(string? scope, int? timeoutSecs, bool once)
        => InvokeAsync(() => CoordinateCommand.RunAsync(scope, timeoutSecs, once));

    private async Task<InvocationResult> InvokeAsync(Func<Task<int>> run)
    {
        await ConsoleLock.WaitAsync(TestContext.Current.CancellationToken);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        string? originalTurnstileSocket = Environment.GetEnvironmentVariable("TURNSTILE_SOCKET");
        string? originalNightshiftSocket = Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET");
        string? originalNightshiftConfig = Environment.GetEnvironmentVariable("NIGHTSHIFT_CONFIG");
        string? originalRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtimeDir = Path.Combine(AppContext.BaseDirectory, "land-stack-runtime", Guid.NewGuid().ToString("N"));
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

            int exitCode = await run();
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
