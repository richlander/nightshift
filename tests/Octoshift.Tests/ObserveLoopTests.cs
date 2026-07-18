namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;
using Xunit;

/// <summary>Loop-level tests for wait/watch over fake gh sources.</summary>
public class ObserveLoopTests
{
    private static readonly PollingTuning FastTuning = PollingTuning.Default with
    {
        MinIntervalSeconds = 1,
        MaxIntervalSeconds = 1,
    };

    [Fact]
    public async Task Wait_AlreadyTerminalOnFirstPoll_ReturnsImmediately()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(Array.Empty<OpenPr>());
        var merged = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [new MergedPr(41, "nightshift/3/op-a", DateTimeOffset.UtcNow)] });
        var emitted = new List<ObserveCommand.ObserveResult>();

        ObserveCommand.WaitOutcome outcome = await ObserveCommand.RunWaitLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            all: false,
            new ObserveCommand.ObserveLoopOptions
            {
                DelayAsync = static (_, _) => Task.CompletedTask,
                Emit = emitted.Add,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Polls);
        ObserveCommand.ObserveResult result = Assert.Single(emitted);
        Assert.Equal(ObserveTokens.Merged, result.Token);
        Assert.Equal(41, outcome.Resolution!.Value.Observation!.Value.Number);
    }

    [Fact]
    public async Task Wait_AlreadyTerminalChoosesLowestPrNumberDeterministically()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(new OpenPr(50, "nightshift/3/op-conflict", PrLifecycle.Open, Mergeability.Conflicting, []));
        var merged = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [new MergedPr(40, "nightshift/3/op-merged", DateTimeOffset.UtcNow)] });

        ObserveCommand.WaitOutcome outcome = await ObserveCommand.RunWaitLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            all: false,
            new ObserveCommand.ObserveLoopOptions { DelayAsync = static (_, _) => Task.CompletedTask },
            TestContext.Current.CancellationToken);

        Assert.Equal(1, outcome.Polls);
        Assert.Equal(ObserveTokens.Merged, outcome.Resolution!.Value.Token);
        Assert.Equal(40, outcome.Resolution.Value.Observation!.Value.Number);
    }

    [Fact]
    public async Task Wait_ReturnsOnTransitionToTerminalAcrossPolls()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(
            [new OpenPr(60, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Mergeable, [])],
            [new OpenPr(60, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Conflicting, [])]);
        var merged = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [] },
            new MergedPrPage { MergedPrs = [] });

        ObserveCommand.WaitOutcome outcome = await ObserveCommand.RunWaitLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            all: false,
            new ObserveCommand.ObserveLoopOptions { DelayAsync = static (_, _) => Task.CompletedTask },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, outcome.Polls);
        Assert.Equal(ObserveTokens.Conflict, outcome.Resolution!.Value.Token);
        Assert.Equal(60, outcome.Resolution.Value.Observation!.Value.Number);
    }

    [Fact]
    public async Task WaitAll_TracksUnionAcrossPollsAndWaitsForLatePr()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(
            [new OpenPr(1, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Mergeable, [])],
            [new OpenPr(2, "nightshift/3/op-b", PrLifecycle.Open, Mergeability.Mergeable, [])],
            [new OpenPr(2, "nightshift/3/op-b", PrLifecycle.Closed, Mergeability.Unknown, [])]);
        var merged = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [] },
            new MergedPrPage { MergedPrs = [new MergedPr(1, "nightshift/3/op-a", DateTimeOffset.UtcNow)] },
            new MergedPrPage { MergedPrs = [] });

        ObserveCommand.WaitOutcome outcome = await ObserveCommand.RunWaitLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            all: true,
            new ObserveCommand.ObserveLoopOptions { DelayAsync = static (_, _) => Task.CompletedTask },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.Polls);
        Assert.Equal(ObserveTokens.AllResolved, outcome.Resolution!.Value.Token);
        Assert.True(outcome.Resolution.Value.IsAllResolved);
        Assert.Equal(2, outcome.Resolution.Value.TotalObserved);
        Assert.Equal(1, outcome.Resolution.Value.MergedCount);
        Assert.Equal(1, outcome.Resolution.Value.ClosedCount);
    }

    [Fact]
    public async Task Watch_EmitsTransitionsAndKeepsRunningUntilStopped()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(
            [new OpenPr(7, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Mergeable, [])],
            [new OpenPr(7, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Conflicting, [])],
            [new OpenPr(7, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Mergeable, [])]);
        var merged = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [] },
            new MergedPrPage { MergedPrs = [] },
            new MergedPrPage { MergedPrs = [] });
        var emitted = new List<ObserveCommand.ObserveResult>();

        ObserveCommand.WatchOutcome outcome = await ObserveCommand.RunWatchLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            new ObserveCommand.ObserveLoopOptions
            {
                MaxPolls = 3,
                DelayAsync = static (_, _) => Task.CompletedTask,
                Emit = emitted.Add,
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(3, outcome.Polls);
        Assert.Equal(2, outcome.Emitted);
        Assert.Equal([ObserveTokens.Conflict, ObserveTokens.Open], emitted.Select(static result => result.Token).ToArray());
    }

    [Theory]
    [InlineData(0, 2, ObserveTokens.Conflict)] // Open + Conflicting
    [InlineData(1, 0, ObserveTokens.Closed)] // Closed + Unknown
    public async Task Wait_EmitsTerminalTokenForOpenSourceTerminalKinds(int lifecycle, int mergeability, string token)
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        var open = new FakeOpenPrSource(new OpenPr(13, "nightshift/3/op-a", (PrLifecycle)lifecycle, (Mergeability)mergeability, []));
        var merged = new FakeMergedPrSource(new MergedPrPage { MergedPrs = [] });

        ObserveCommand.WaitOutcome outcome = await ObserveCommand.RunWaitLoopAsync(
            scope,
            open,
            merged,
            new AdaptivePoller(FastTuning),
            FastTuning,
            all: false,
            new ObserveCommand.ObserveLoopOptions { DelayAsync = static (_, _) => Task.CompletedTask },
            TestContext.Current.CancellationToken);

        Assert.Equal(token, outcome.Resolution!.Value.Token);
        Assert.Equal(13, outcome.Resolution.Value.Observation!.Value.Number);
    }
}
