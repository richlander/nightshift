namespace Octoshift.Polling;

/// <summary>
/// The tunables for the adaptive poller. Defaults match the order brief: a 60s..600s hard band, a 10-merge
/// cadence window with EWMA decay 0.3 (recent gaps weighted heavier), and multiplicative backoff factor 2.
/// The cadence fractions turn the estimated inter-merge gap into reactive bounds (ceiling ≈ half the gap,
/// floor ≈ a tenth), so a fast shift stays vigilant while a slow one relaxes.
/// </summary>
internal readonly record struct PollingTuning
{
    public int MinIntervalSeconds { get; init; }
    public int MaxIntervalSeconds { get; init; }
    public int CadenceWindow { get; init; }
    public double CadenceDecay { get; init; }
    public double BackoffFactor { get; init; }
    public double CeilingFraction { get; init; }
    public double FloorFraction { get; init; }

    public static PollingTuning Default { get; } = new()
    {
        MinIntervalSeconds = 60,
        MaxIntervalSeconds = 600,
        CadenceWindow = 10,
        CadenceDecay = 0.3,
        BackoffFactor = 2.0,
        CeilingFraction = 0.5,
        FloorFraction = 0.1,
    };
}

/// <summary>The floor/ceiling reactive band the cadence estimate carves out of the hard clamp band.</summary>
internal readonly record struct IntervalBounds(double FloorSeconds, double CeilingSeconds);

/// <summary>The transport outcome of one poll — the signals that pace the next one.</summary>
internal readonly record struct PollOutcome
{
    /// <summary>A land happened this poll: reset the reactive backoff to the floor.</summary>
    public bool LandedSomething { get; init; }

    /// <summary>403/429/5xx or a depleted rate budget: back off to the ceiling.</summary>
    public bool RateLimited { get; init; }

    /// <summary>GitHub's <c>X-Poll-Interval</c> in seconds (0 = none): a hard lower bound.</summary>
    public int ProviderMinIntervalSeconds { get; init; }

    /// <summary>Seconds until <c>X-RateLimit-Reset</c> (0 = none): honored as a lower bound when rate-limited.</summary>
    public int RateLimitResetSeconds { get; init; }
}

/// <summary>The full input to one interval decision: the previous wait, the cadence estimate, board state, and the poll outcome.</summary>
internal readonly record struct PollerState
{
    /// <summary>The interval we just waited (seconds); the base the reactive backoff multiplies.</summary>
    public double PreviousIntervalSeconds { get; init; }

    /// <summary>The EWMA-estimated inter-merge gap in seconds, or null before enough history exists.</summary>
    public double? EstimatedGapSeconds { get; init; }

    /// <summary>True when the board shows an order at <c>done</c> awaiting land (pin to the floor).</summary>
    public bool BoardHasOutstandingDone { get; init; }

    /// <summary>This poll's transport outcome.</summary>
    public PollOutcome LastPoll { get; init; }
}

/// <summary>
/// A pure, testable two-timescale interval controller — no clock, no network, no daemon. Cadence (an EWMA
/// over recent inter-merge gaps, precedent: Jacobson/Karels TCP RTT and adaptive feed-refresh) sets the
/// reactive bounds; within them, backoff-with-full-jitter (precedent: AWS) relaxes on idle and resets on
/// activity; the board's <c>done</c> signal pins to the floor when a merge is imminent; and hard clamps
/// plus GitHub's own poll-interval / rate-limit headers have the final say. Signal precedence, strongest
/// first: board-imminent (floor) &gt; cadence EWMA (bounds) &gt; reactive backoff+jitter &gt; hard clamps /
/// rate-limit override.
/// </summary>
internal sealed class AdaptivePoller
{
    private readonly PollingTuning _tuning;

    public AdaptivePoller(PollingTuning tuning) => _tuning = tuning;

    /// <summary>
    /// Estimates the expected inter-merge gap (seconds) as an EWMA over the last <c>CadenceWindow</c> gaps
    /// between merge instants, recent gaps weighted heavier (decay = alpha). Returns null with fewer than
    /// two merges — no gap to measure — so the caller falls back to the static bootstrap band.
    /// </summary>
    public double? EstimateGapSeconds(IReadOnlyList<DateTimeOffset> mergeTimes)
    {
        if (mergeTimes.Count < 2)
        {
            return null;
        }

        var ordered = mergeTimes.OrderBy(t => t).ToList();
        var gaps = new List<double>(ordered.Count - 1);
        for (int i = 1; i < ordered.Count; i++)
        {
            gaps.Add((ordered[i] - ordered[i - 1]).TotalSeconds);
        }

        int window = Math.Max(1, _tuning.CadenceWindow);
        if (gaps.Count > window)
        {
            gaps = gaps.GetRange(gaps.Count - window, window);
        }

        double alpha = Math.Clamp(_tuning.CadenceDecay, 0.0, 1.0);
        double ewma = gaps[0];
        for (int i = 1; i < gaps.Count; i++)
        {
            ewma = (alpha * gaps[i]) + ((1 - alpha) * ewma);
        }

        return ewma;
    }

    /// <summary>
    /// Turns a cadence estimate into the reactive band: ceiling ≈ <c>CeilingFraction</c>×gap and floor ≈
    /// <c>FloorFraction</c>×gap, each clamped into the hard [min, max] band (floor never above ceiling).
    /// With no estimate it opens to the full hard band — the bootstrap default.
    /// </summary>
    public IntervalBounds Bounds(double? estimatedGapSeconds)
    {
        double min = _tuning.MinIntervalSeconds;
        double max = Math.Max(_tuning.MinIntervalSeconds, _tuning.MaxIntervalSeconds);
        if (estimatedGapSeconds is not { } gap || gap <= 0)
        {
            return new IntervalBounds(min, max);
        }

        double ceiling = Math.Clamp(_tuning.CeilingFraction * gap, min, max);
        double floor = Math.Clamp(_tuning.FloorFraction * gap, min, ceiling);
        return new IntervalBounds(floor, ceiling);
    }

    /// <summary>
    /// Computes the next interval in seconds. <paramref name="jitterSample"/> is a caller-supplied uniform
    /// draw in [0,1) (injected so the function stays pure and testable); the daemon passes
    /// <see cref="Random.NextDouble"/>. Board-imminent pins to the floor, a fresh land resets to the floor,
    /// a rate-limit backs off to the ceiling, and otherwise the interval backs off multiplicatively from the
    /// previous wait with full jitter — always within the cadence band, then hard-clamped, then raised (never
    /// lowered) to honor GitHub's poll-interval and rate-limit-reset floors.
    /// </summary>
    public double NextIntervalSeconds(PollerState state, double jitterSample)
    {
        IntervalBounds bounds = Bounds(state.EstimatedGapSeconds);
        double floor = bounds.FloorSeconds;
        double ceiling = bounds.CeilingSeconds;

        double candidate;
        if (state.BoardHasOutstandingDone)
        {
            candidate = floor; // strongest signal: a merge is imminent
        }
        else if (state.LastPoll.LandedSomething)
        {
            candidate = floor; // reset-on-activity
        }
        else if (state.LastPoll.RateLimited)
        {
            candidate = ceiling; // back off to the ceiling on error / depleted budget
        }
        else
        {
            candidate = FullJitter(state.PreviousIntervalSeconds, floor, ceiling, jitterSample);
        }

        // Hard clamps always apply within normal operation...
        candidate = Math.Clamp(candidate, _tuning.MinIntervalSeconds, Math.Max(_tuning.MinIntervalSeconds, _tuning.MaxIntervalSeconds));

        // ...then provider overrides act as lower bounds that may exceed the max: never poll below GitHub's
        // advertised interval, and when rate-limited wait at least until the budget resets.
        if (state.LastPoll.ProviderMinIntervalSeconds > 0)
        {
            candidate = Math.Max(candidate, state.LastPoll.ProviderMinIntervalSeconds);
        }

        if (state.LastPoll.RateLimited && state.LastPoll.RateLimitResetSeconds > 0)
        {
            candidate = Math.Max(candidate, state.LastPoll.RateLimitResetSeconds);
        }

        return candidate;
    }

    /// <summary>
    /// One reactive backoff step with AWS-style full jitter: grow the previous interval by the backoff
    /// factor, cap it at the ceiling, then draw uniformly across the whole [floor, cap] span so concurrent
    /// watchers de-correlate. A previous interval at or below the floor still climbs (the floor seeds it).
    /// </summary>
    private double FullJitter(double previous, double floor, double ceiling, double jitterSample)
    {
        double seed = previous > floor ? previous : floor;
        double cap = Math.Min(ceiling, seed * _tuning.BackoffFactor);
        cap = Math.Max(cap, floor);
        double sample = Math.Clamp(jitterSample, 0.0, 1.0);
        return floor + (sample * (cap - floor));
    }
}
