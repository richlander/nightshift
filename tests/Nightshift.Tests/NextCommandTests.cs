namespace Nightshift.Tests;

using Nightshift;
using Nightshift.Commands;
using Nightshift.Config;
using Nightshift.Turnstile;
using Xunit;

public class NextCommandTests : IClassFixture<TurnstileFixture>
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    private readonly TurnstileFixture _fixture;

    public NextCommandTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task WaitForChangeCore_OnCompaction_ReRangesToCurrentRevision()
    {
        long refreshed = await NextCommand.WaitForChangeCoreAsync(
            (_, _) => throw new WatchCompactedException("/", 40, 100),
            _ => Task.FromResult(101L),
            fromRevision: 40,
            budget: TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(101L, refreshed);
    }

    [Fact]
    public async Task WaitForChangeCore_OnSignal_ReturnsSignalRevision()
    {
        long refreshed = await NextCommand.WaitForChangeCoreAsync(
            (_, _) => Replay([new WatchSignal("/ready/1/op", Deleted: false, Revision: 77)]),
            _ => throw new InvalidOperationException("current revision must not be queried on event"),
            fromRevision: 40,
            budget: TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(77L, refreshed);
    }

    [Fact]
    public async Task WaitForScopedChangeCore_ControlWake_WinsWhenFirst()
    {
        NextCommand.WaitResult result = await NextCommand.WaitForScopedChangeCoreAsync(
            (_, _) => Replay([new WatchSignal("/ready/1/op", Deleted: false, Revision: 72)], delay: TimeSpan.FromMilliseconds(150)),
            (_, _) => Replay([new WatchSignal("/control/draining", Deleted: false, Revision: 71)], delay: TimeSpan.FromMilliseconds(25)),
            _ => Task.FromResult(80L),
            fromRevision: 40,
            budget: TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Equal(NextCommand.WakeSource.Control, result.Source);
        Assert.Equal(71L, result.Revision);
    }

    [Fact]
    public async Task Once_WithNoReadyRows_ReturnsNoWork()
    {
        await ResetControlKeysAsync();

        InvocationResult result = await InvokeNextAsync(
            scope: $"once-empty-{Guid.NewGuid():N}",
            timeoutSecs: null,
            once: true);

        Assert.Equal(ExitCode.NoWork, result.ExitCode);
        Assert.Equal($"NOWORK{Environment.NewLine}", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task Once_WithClaimableOrder_ReturnsWork()
    {
        await ResetControlKeysAsync();
        string scope = $"once-work-{Guid.NewGuid():N}";
        string orderBase = $"/plan/{scope}/order/op1";
        string readyKey = $"/ready/{scope}/op1";

        InvocationResult result = await InvokeNextAsync(
            scope,
            timeoutSecs: null,
            once: true,
            beforeRun: async ct =>
            {
                using TurnstileClient client = _fixture.Connect();
                await client.SetAsync($"{orderBase}/spec", "{\"title\":\"test order\"}", ct);
                await client.SetAsync(readyKey, orderBase, ct);
            });

        Assert.Equal(ExitCode.Ok, result.ExitCode);
        Assert.StartsWith($"WORK {orderBase}{Environment.NewLine}", result.Stdout, StringComparison.Ordinal);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task DefaultWait_ReturnsDraining_WhenDrainingAppearsWhileParked()
    {
        await ResetControlKeysAsync();
        try
        {
            InvocationResult result = await InvokeNextAsync(
                scope: $"drain-{Guid.NewGuid():N}",
                timeoutSecs: null,
                once: false,
                whileRunning: async ct =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                    using TurnstileClient client = _fixture.Connect();
                    await client.SetAsync("/control/draining", "1", ct);
                });

            Assert.Equal(ExitCode.Draining, result.ExitCode);
            Assert.Equal($"DRAINING{Environment.NewLine}", result.Stdout);
            Assert.Empty(result.Stderr);
        }
        finally
        {
            await ResetControlKeysAsync();
        }
    }

    [Fact]
    public async Task DefaultWait_ReturnsHalt_WhenHaltAppearsWhileParked()
    {
        await ResetControlKeysAsync();
        try
        {
            InvocationResult result = await InvokeNextAsync(
                scope: $"halt-{Guid.NewGuid():N}",
                timeoutSecs: null,
                once: false,
                whileRunning: async ct =>
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
                    using TurnstileClient client = _fixture.Connect();
                    await client.SetAsync("/control/halt", "1", ct);
                });

            Assert.Equal(ExitCode.Halt, result.ExitCode);
            Assert.Equal($"HALT{Environment.NewLine}", result.Stdout);
            Assert.Empty(result.Stderr);
        }
        finally
        {
            await ResetControlKeysAsync();
        }
    }

    [Fact]
    public async Task WhileParked_RenewsPresenceLease()
    {
        await ResetControlKeysAsync();
        TimeSpan originalCadence = NextCommand.KeepAliveCadence;
        NextCommand.KeepAliveCadence = TimeSpan.FromMilliseconds(200);
        try
        {
            InvocationResult result = await InvokeNextAsync(
                scope: $"presence-{Guid.NewGuid():N}",
                timeoutSecs: 3,
                once: false,
                beforeRun: async ct =>
                {
                    using TurnstileClient client = _fixture.Connect();
                    string leaseId = await client.CreateLeaseAsync(2, ct);
                    await client.PutLeasedAsync(Presence.Key, "active", leaseId, ct);
                    Presence.Save(new PresenceState(leaseId));
                });

            Assert.Equal(ExitCode.NoWork, result.ExitCode);
            Assert.Equal($"NOWORK{Environment.NewLine}", result.Stdout);
            Assert.Empty(result.Stderr);

            using TurnstileClient verify = _fixture.Connect();
            KvItem? entry = await verify.GetAsync(Presence.Key, TestContext.Current.CancellationToken);
            Assert.NotNull(entry);
            Assert.Equal("active", entry!.Text);
        }
        finally
        {
            NextCommand.KeepAliveCadence = originalCadence;
            await ResetControlKeysAsync();
        }
    }

    private async Task ResetControlKeysAsync()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        await client.DeleteAsync("/control/draining", ct);
        await client.DeleteAsync("/control/halt", ct);
    }

    private static async IAsyncEnumerable<WatchSignal> Replay(IEnumerable<WatchSignal> events, TimeSpan? delay = null)
    {
        foreach (WatchSignal signal in events)
        {
            if (delay is { } d && d > TimeSpan.Zero)
            {
                await Task.Delay(d, TestContext.Current.CancellationToken);
            }

            yield return signal;
        }
    }

    private async Task<InvocationResult> InvokeNextAsync(
        string? scope,
        int? timeoutSecs,
        bool once,
        Func<CancellationToken, Task>? beforeRun = null,
        Func<CancellationToken, Task>? whileRunning = null)
    {
        await ConsoleLock.WaitAsync(TestContext.Current.CancellationToken);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        string? originalTurnstileSocket = Environment.GetEnvironmentVariable("TURNSTILE_SOCKET");
        string? originalNightshiftSocket = Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET");
        string? originalNightshiftConfig = Environment.GetEnvironmentVariable("NIGHTSHIFT_CONFIG");
        string? originalRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtimeDir = Path.Combine(AppContext.BaseDirectory, "next-runtime", Guid.NewGuid().ToString("N"));
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
            CancellationToken ct = TestContext.Current.CancellationToken;

            if (beforeRun is not null)
            {
                await beforeRun(ct);
            }

            Task<int> run = NextCommand.RunAsync(scope, timeoutSecs, once);
            if (whileRunning is not null)
            {
                await whileRunning(ct);
            }

            Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(8), ct));
            if (completed != run)
            {
                throw new TimeoutException("next did not complete within the test budget");
            }

            int exitCode = await run;
            return new InvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Presence.Clear();
            Session.Clear();
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
                // Best-effort cleanup for files that may be held during a failing test.
            }

            ConsoleLock.Release();
        }
    }

    private sealed record InvocationResult(int ExitCode, string Stdout, string Stderr);
}
