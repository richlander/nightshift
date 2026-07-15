namespace Octoshift.Tests;

using Octoshift.Polling;
using Xunit;

/// <summary>
/// The pure adaptive-poll controller: cadence EWMA sets the band, board-imminent pins to the floor, a land
/// resets to the floor, an idle poll backs off with bounded jitter, a rate-limit backs off to the ceiling,
/// and GitHub's poll-interval / rate-limit-reset headers act as lower bounds that can exceed the max. No
/// clock, no network — every case is a deterministic function call.
/// </summary>
public class AdaptivePollerTests
{
    private static readonly AdaptivePoller Poller = new(PollingTuning.Default);

    private static IReadOnlyList<DateTimeOffset> MergesWithGaps(params double[] gapSeconds)
    {
        var times = new List<DateTimeOffset> { DateTimeOffset.UnixEpoch };
        foreach (double gap in gapSeconds)
        {
            times.Add(times[^1].AddSeconds(gap));
        }

        return times;
    }

    private static PollerState State(
        double previous = 60,
        double? estimate = null,
        bool boardDone = false,
        bool landed = false,
        bool rateLimited = false,
        int providerMin = 0,
        int resetSeconds = 0)
        => new()
        {
            PreviousIntervalSeconds = previous,
            EstimatedGapSeconds = estimate,
            BoardHasOutstandingDone = boardDone,
            LastPoll = new PollOutcome
            {
                LandedSomething = landed,
                RateLimited = rateLimited,
                ProviderMinIntervalSeconds = providerMin,
                RateLimitResetSeconds = resetSeconds,
            },
        };

    [Fact]
    public void EstimateGap_NullWithFewerThanTwoMerges()
    {
        Assert.Null(Poller.EstimateGapSeconds([]));
        Assert.Null(Poller.EstimateGapSeconds([DateTimeOffset.UnixEpoch]));
    }

    [Fact]
    public void EstimateGap_UniformGaps_ReturnsThatGap()
        => Assert.Equal(1200, Poller.EstimateGapSeconds(MergesWithGaps(1200, 1200, 1200))!.Value, precision: 6);

    [Fact]
    public void EstimateGap_WeightsRecentGapsHeavier()
    {
        // gaps oldest->newest: 6000 then 1200. EWMA(alpha=0.3) = 0.3*1200 + 0.7*6000 = 4560.
        double? estimate = Poller.EstimateGapSeconds(MergesWithGaps(6000, 1200));

        Assert.Equal(4560, estimate!.Value, precision: 6);
    }

    [Fact]
    public void Bounds_NoEstimate_OpensToFullHardBand()
    {
        IntervalBounds bounds = Poller.Bounds(null);

        Assert.Equal(60, bounds.FloorSeconds);
        Assert.Equal(600, bounds.CeilingSeconds);
    }

    [Fact]
    public void Bounds_FastCadence_IsTighterThanSlowCadence()
    {
        IntervalBounds fast = Poller.Bounds(1200);   // ~20 min gaps
        IntervalBounds slow = Poller.Bounds(21600);  // ~6 h gaps

        // A faster cadence permits a lower floor (stay vigilant); a slower one relaxes upward.
        Assert.True(fast.FloorSeconds < slow.FloorSeconds);
        Assert.Equal(120, fast.FloorSeconds);
        Assert.Equal(600, slow.FloorSeconds);

        // And the reactive ceiling scales with the gap until it saturates at the hard max.
        Assert.True(Poller.Bounds(600).CeilingSeconds < Poller.Bounds(1200).CeilingSeconds);
    }

    [Fact]
    public void BoardImminent_PinsToFloor()
    {
        double next = Poller.NextIntervalSeconds(State(previous: 480, estimate: 1200, boardDone: true), jitterSample: 1.0);

        Assert.Equal(120, next); // floor of the 1200s-cadence band, despite a large previous interval
    }

    [Fact]
    public void Land_ResetsToFloor()
    {
        double next = Poller.NextIntervalSeconds(State(previous: 480, estimate: 1200, landed: true), jitterSample: 1.0);

        Assert.Equal(120, next);
    }

    [Fact]
    public void RateLimited_BacksOffToCeiling()
    {
        double next = Poller.NextIntervalSeconds(State(previous: 120, estimate: 1200, rateLimited: true), jitterSample: 0.0);

        Assert.Equal(600, next); // ceiling of the 1200s-cadence band
    }

    [Fact]
    public void RateLimitReset_HonoredAsLowerBoundAboveMax()
    {
        double next = Poller.NextIntervalSeconds(
            State(estimate: 1200, rateLimited: true, resetSeconds: 900),
            jitterSample: 0.0);

        Assert.Equal(900, next); // reset (900s) exceeds the 600s hard max and wins as a lower bound
    }

    [Fact]
    public void ProviderPollInterval_ClampsFloorUp()
    {
        double next = Poller.NextIntervalSeconds(State(previous: 60, providerMin: 300), jitterSample: 0.0);

        Assert.Equal(300, next); // X-Poll-Interval floor overrides the otherwise-60s backoff
    }

    [Fact]
    public void Backoff_FullJitter_StaysWithinBand()
    {
        double atZero = Poller.NextIntervalSeconds(State(previous: 60), jitterSample: 0.0);
        double atOne = Poller.NextIntervalSeconds(State(previous: 60), jitterSample: 1.0);

        Assert.Equal(60, atZero);   // full jitter can draw all the way down to the floor
        Assert.Equal(120, atOne);   // ...up to previous*backoff (60*2), capped by the ceiling
        Assert.InRange(Poller.NextIntervalSeconds(State(previous: 60), jitterSample: 0.5), 60, 120);
    }

    [Fact]
    public void Backoff_DecaysTowardCeilingThenResets()
    {
        // Repeated idle polls (max jitter) climb multiplicatively and saturate at the hard ceiling.
        double interval = 60;
        var seen = new List<double>();
        for (int i = 0; i < 6; i++)
        {
            interval = Poller.NextIntervalSeconds(State(previous: interval), jitterSample: 1.0);
            seen.Add(interval);
        }

        Assert.Equal(new double[] { 120, 240, 480, 600, 600, 600 }, seen);

        // A land then resets to the floor.
        double afterLand = Poller.NextIntervalSeconds(State(previous: interval, landed: true), jitterSample: 1.0);
        Assert.Equal(60, afterLand);
    }
}
