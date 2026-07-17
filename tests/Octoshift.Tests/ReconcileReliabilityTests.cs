namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;
using Xunit;

/// <summary>End-to-end reconcile-loop reliability invariants around retry, watermarking, and clean cancellation.</summary>
public class ReconcileReliabilityTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-15T12:00:00Z");
    private static readonly AdaptivePoller Poller = new(PollingTuning.Default);

    private static MergedPr Pr(int number, string order, DateTimeOffset mergedAt)
        => new(number, $"nightshift/2/{order}", mergedAt);

    private static BoardState Board(params (string OrderBase, string Status)[] rows)
        => BoardState.FromRows(rows.Select(r => new BoardRow { OrderBase = r.OrderBase, Status = r.Status }));

    [Fact]
    public async Task PollOnce_LandFailureDoesNotAdvanceWatermark_AndMergedPrRetries()
    {
        var state = new ReconcileCommand.ReconcileState { IntervalSeconds = 60 };
        var nightshift = new FakeNightshiftClient(
            Board(("/plan/2/order/op-a", "done")),
            landResults: [false, true]);
        var source = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [Pr(1, "op-a", T0)] },
            new MergedPrPage { MergedPrs = [Pr(1, "op-a", T0)] });

        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);
        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);

        Assert.Equal(T0, state.Since);
        Assert.Equal([null, null], source.SinceArgs);
        Assert.Equal(
            [("/plan/2/order/op-a", "merged #1"), ("/plan/2/order/op-a", "merged #1")],
            nightshift.Lands);
    }

    [Fact]
    public async Task PollOnce_DoneOnBoardWithoutMergedPrs_DoesNotLand()
    {
        var state = new ReconcileCommand.ReconcileState { IntervalSeconds = 60 };
        var nightshift = new FakeNightshiftClient(Board(("/plan/2/order/op-a", "done")));
        var source = new FakeMergedPrSource(new MergedPrPage { MergedPrs = [] });

        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);

        Assert.Empty(nightshift.Lands);
    }

    [Fact]
    public async Task PollOnce_SameSecondSiblingPrsBothLand()
    {
        var state = new ReconcileCommand.ReconcileState { IntervalSeconds = 60 };
        var nightshift = new FakeNightshiftClient(BoardState.Empty);
        var source = new FakeMergedPrSource(
            new MergedPrPage { MergedPrs = [Pr(10, "op-a", T0)] },
            new MergedPrPage { MergedPrs = [Pr(10, "op-a", T0), Pr(11, "op-b", T0)] });

        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);
        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);

        Assert.Equal(T0, state.Since);
        Assert.Equal(
            [("/plan/2/order/op-a", "merged #10"), ("/plan/2/order/op-b", "merged #11")],
            nightshift.Lands);
    }

    [Fact]
    public async Task PollOnce_TruncatedSweepClampsWatermarkAndDeepMergeLandsNext()
    {
        DateTimeOffset boundary = T0.AddDays(-7);
        var state = new ReconcileCommand.ReconcileState { IntervalSeconds = 60 };
        var nightshift = new FakeNightshiftClient(BoardState.Empty);
        var source = new FakeMergedPrSource(
            new MergedPrPage
            {
                MergedPrs = [Pr(1, "op-new", T0.AddMinutes(30)), Pr(2, "op-stale", boundary)],
                Truncated = true,
                OldestSeenMergedAt = boundary,
            },
            new MergedPrPage { MergedPrs = [Pr(3, "op-deep", T0.AddDays(-1))] });

        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);
        await ReconcileCommand.PollOnceAsync(nightshift, source, state, Poller, PollingTuning.Default, TestContext.Current.CancellationToken);

        Assert.Equal(boundary, source.SinceArgs[1]);
        Assert.Equal(
            [
                ("/plan/2/order/op-new", "merged #1"),
                ("/plan/2/order/op-stale", "merged #2"),
                ("/plan/2/order/op-deep", "merged #3"),
            ],
            nightshift.Lands);
    }

    [Fact]
    public async Task RunOnceAsync_CanceledSweepExitsOk()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        int exit = await ReconcileCommand.RunOnceAsync(
            new FakeNightshiftClient(BoardState.Empty),
            new FakeMergedPrSource(),
            cts.Token);

        Assert.Equal(ExitCode.Ok, exit);
    }
}
