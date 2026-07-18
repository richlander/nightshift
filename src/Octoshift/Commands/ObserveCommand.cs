namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;

/// <summary>
/// Read-only observation verbs over the same gh poll engine as <see cref="ReconcileCommand"/>:
/// <c>wait</c> blocks for terminal resolutions and <c>watch</c> streams state transitions.
/// </summary>
internal static class ObserveCommand
{
    /// <summary>
    /// Structured output emitted by wait/watch loops, so tests can assert decisions without scraping console
    /// output.
    /// </summary>
    internal readonly record struct ObserveResult(
        string Token,
        string Line,
        PrObservation? Observation,
        bool IsAllResolved,
        int TotalObserved,
        int MergedCount,
        int ClosedCount,
        int ConflictCount);

    /// <summary>Outcome of one wait loop run.</summary>
    internal readonly record struct WaitOutcome(ObserveResult? Resolution, int Polls);

    /// <summary>Outcome of one watch loop run.</summary>
    internal readonly record struct WatchOutcome(int Polls, int Emitted);

    /// <summary>
    /// Optional loop controls for tests: cap poll count, replace delay, and capture structured emitted events.
    /// </summary>
    internal sealed record ObserveLoopOptions
    {
        public int? MaxPolls { get; init; }

        public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = static (delay, ct) => Task.Delay(delay, ct);

        public Action<ObserveResult>? Emit { get; init; }
    }

    /// <summary>
    /// Runs <c>octoshift wait &lt;scope&gt;</c>. For non-<c>--all</c>, if several PRs are already terminal at
    /// entry, it returns immediately with the lowest PR number for deterministic output.
    /// </summary>
    public static async Task<int> RunWaitAsync(
        string scopeText,
        string? repo,
        bool all,
        int? minInterval,
        int? maxInterval,
        int? cadenceWindow,
        double? cadenceDecay,
        double? backoff)
    {
        if (TryBuildContext(scopeText, repo, minInterval, maxInterval, cadenceWindow, cadenceDecay, backoff) is not { } context)
        {
            return ExitCode.Usage;
        }

        return await WaitAsync(context.Scope, context.OpenSource, context.MergedSource, context.Poller, context.Tuning, all);
    }

    /// <summary>Runs <c>octoshift watch &lt;scope&gt;</c>.</summary>
    public static async Task<int> RunWatchAsync(
        string scopeText,
        string? repo,
        int? minInterval,
        int? maxInterval,
        int? cadenceWindow,
        double? cadenceDecay,
        double? backoff)
    {
        if (TryBuildContext(scopeText, repo, minInterval, maxInterval, cadenceWindow, cadenceDecay, backoff) is not { } context)
        {
            return ExitCode.Usage;
        }

        return await WatchAsync(context.Scope, context.OpenSource, context.MergedSource, context.Poller, context.Tuning);
    }

    /// <summary>
    /// Testable wait loop over injectable sources. Non-<c>--all</c> returns as soon as one PR in scope is
    /// terminal (merged, closed, conflicting); <c>--all</c> tracks the union of every PR seen across polls
    /// and resolves only when all ever-seen PRs are terminal. Inherent limit: this can only wait on PRs that
    /// have opened and become observable in GitHub results.
    /// </summary>
    internal static async Task<WaitOutcome> RunWaitLoopAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning,
        bool all,
        ObserveLoopOptions? options,
        CancellationToken ct)
    {
        options ??= new ObserveLoopOptions();
        var state = new ObserveState { IntervalSeconds = tuning.MinIntervalSeconds };
        var tracked = new Dictionary<int, ObservationState>();
        int polls = 0;

        while (!ct.IsCancellationRequested)
        {
            ObservePoll poll = await PollOnceAsync(scope, openSource, mergedSource, state, poller, tuning, ct);
            polls++;

            if (all)
            {
                foreach ((int number, PrObservation observation) in poll.Snapshot)
                {
                    tracked[number] = observation.State;
                }

                if (tracked.Count > 0 && tracked.Values.All(ObservationDecision.IsTerminal))
                {
                    ObserveResult resolved = BuildAllResolvedResult(scope, tracked);
                    options.Emit?.Invoke(resolved);
                    return new WaitOutcome(resolved, polls);
                }
            }
            else if (ResolveSingleWait(polls, poll) is { } resolved)
            {
                options.Emit?.Invoke(resolved);
                return new WaitOutcome(resolved, polls);
            }

            if (ReachedPollLimit(options, polls))
            {
                return new WaitOutcome(null, polls);
            }

            await options.DelayAsync(TimeSpan.FromSeconds(state.IntervalSeconds), ct);
        }

        throw new OperationCanceledException(ct);
    }

    /// <summary>
    /// Testable watch loop over injectable sources. Emits transitions and runs until canceled (or a test poll
    /// cap is reached).
    /// </summary>
    internal static async Task<WatchOutcome> RunWatchLoopAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning,
        ObserveLoopOptions? options,
        CancellationToken ct)
    {
        options ??= new ObserveLoopOptions();
        var state = new ObserveState { IntervalSeconds = tuning.MinIntervalSeconds };
        int polls = 0;
        int emitted = 0;

        while (!ct.IsCancellationRequested)
        {
            ObservePoll poll = await PollOnceAsync(scope, openSource, mergedSource, state, poller, tuning, ct);
            polls++;

            if (polls > 1)
            {
                foreach (PrTransition transition in poll.Transitions)
                {
                    ObserveResult result = BuildTransitionResult(transition.Current);
                    options.Emit?.Invoke(result);
                    emitted++;
                }
            }

            if (ReachedPollLimit(options, polls))
            {
                return new WatchOutcome(polls, emitted);
            }

            await options.DelayAsync(TimeSpan.FromSeconds(state.IntervalSeconds), ct);
        }

        throw new OperationCanceledException(ct);
    }

    private static async Task<int> WaitAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning,
        bool all)
    {
        using var cts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;

        try
        {
            await RunWaitLoopAsync(
                scope,
                openSource,
                mergedSource,
                poller,
                tuning,
                all,
                new ObserveLoopOptions { Emit = static result => Console.WriteLine(result.Line) },
                ct);

            return ExitCode.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
            return ExitCode.Ok;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static async Task<int> WatchAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning)
    {
        using var cts = new CancellationTokenSource();
        CancellationToken ct = cts.Token;
        ConsoleCancelEventHandler handler = (_, e) => { e.Cancel = true; cts.Cancel(); };
        Console.CancelKeyPress += handler;

        try
        {
            await RunWatchLoopAsync(
                scope,
                openSource,
                mergedSource,
                poller,
                tuning,
                new ObserveLoopOptions { Emit = static result => Console.WriteLine(result.Line) },
                ct);

            return ExitCode.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
            return ExitCode.Ok;
        }
        finally
        {
            Console.CancelKeyPress -= handler;
        }
    }

    private static ObserveResult? ResolveSingleWait(int pollIndex, ObservePoll poll)
    {
        if (pollIndex == 1)
        {
            foreach (PrObservation observation in ObservationDecision.TerminalNow(poll.Snapshot))
            {
                return BuildTransitionResult(observation);
            }

            return null;
        }

        foreach (PrTransition transition in poll.Transitions)
        {
            if (transition.IsTerminal)
            {
                return BuildTransitionResult(transition.Current);
            }
        }

        return null;
    }

    private static bool ReachedPollLimit(ObserveLoopOptions options, int polls)
        => options.MaxPolls is { } maxPolls && polls >= maxPolls;

    private static async Task<ObservePoll> PollOnceAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        ObserveState state,
        AdaptivePoller poller,
        PollingTuning tuning,
        CancellationToken ct)
    {
        MergedPrPage mergedPage = await mergedSource.FetchMergedAsync(state.Since, state.ETag, ct);
        state.ETag = mergedPage.ETag;

        foreach (MergedPr pr in mergedPage.MergedPrs)
        {
            state.HandledMergedPrs[pr.Number] = pr.MergedAt;
        }

        RecordMergedHistory(state, mergedPage.MergedPrs, tuning.CadenceWindow);

        state.Since = ReconcileCommand.AdvanceWatermark(
            state.Since,
            mergedPage.MergedPrs,
            state.HandledMergedPrs,
            mergedPage.Truncated,
            mergedPage.OldestSeenMergedAt);
        PruneHandledMergedPrs(state);

        IReadOnlyList<OpenPr> open = await openSource.FetchOpenAsync(ct);
        IReadOnlyDictionary<int, PrObservation> snapshot = ObservationDecision.BuildSnapshot(scope, open, mergedPage.MergedPrs);
        IReadOnlyList<PrTransition> transitions = ObservationDecision.Transitions(state.LastSnapshot, snapshot);
        state.LastSnapshot = snapshot.ToDictionary(static kvp => kvp.Key, static kvp => kvp.Value);

        var pollerState = new PollerState
        {
            PreviousIntervalSeconds = state.IntervalSeconds,
            EstimatedGapSeconds = poller.EstimateGapSeconds(state.RecentMerges.Select(static pr => pr.MergedAt).ToArray()),
            BoardHasOutstandingDone = false,
            LastPoll = new PollOutcome
            {
                LandedSomething = transitions.Count > 0,
                RateLimited = mergedPage.RateLimited,
                ProviderMinIntervalSeconds = mergedPage.ProviderMinIntervalSeconds,
                RateLimitResetSeconds = mergedPage.RateLimitResetSeconds,
            },
        };

        state.IntervalSeconds = poller.NextIntervalSeconds(pollerState, Random.Shared.NextDouble());
        return new ObservePoll(snapshot, transitions);
    }

    private static BuildContext? TryBuildContext(
        string scopeText,
        string? repo,
        int? minInterval,
        int? maxInterval,
        int? cadenceWindow,
        double? cadenceDecay,
        double? backoff)
    {
        string? resolvedRepo = RepoScope.Resolve(repo);
        if (resolvedRepo is null)
        {
            Console.Error.WriteLine("octoshift observe: could not resolve a repo — run inside a git worktree or pass --repo owner/name");
            return null;
        }

        ObservationScope? scope = ObservationScope.Parse(scopeText);
        if (scope is not { } parsedScope)
        {
            Console.Error.WriteLine("octoshift observe: scope must be a plan or order (e.g. /plan/3 or /plan/3/order/op1)");
            return null;
        }

        PollingTuning tuning = BuildTuning(minInterval, maxInterval, cadenceWindow, cadenceDecay, backoff);
        return new BuildContext(
            parsedScope,
            new GhOpenPrSource(resolvedRepo, parsedScope.BranchSearch, parsedScope.IsOrder, limit: 1000),
            new GhMergedPrSource(resolvedRepo, parsedScope.BranchSearch, parsedScope.IsOrder),
            new AdaptivePoller(tuning),
            tuning);
    }

    private static ObserveResult BuildTransitionResult(PrObservation observation)
    {
        string token = ObservationDecision.TokenOf(observation.State);
        return new ObserveResult(
            token,
            $"{token} {observation.OrderBase} (PR #{observation.Number}, {observation.HeadBranch})",
            observation,
            IsAllResolved: false,
            TotalObserved: 1,
            MergedCount: observation.State == ObservationState.Merged ? 1 : 0,
            ClosedCount: observation.State == ObservationState.Closed ? 1 : 0,
            ConflictCount: observation.State == ObservationState.Conflicting ? 1 : 0);
    }

    private static ObserveResult BuildAllResolvedResult(ObservationScope scope, IReadOnlyDictionary<int, ObservationState> tracked)
    {
        int merged = tracked.Values.Count(static state => state == ObservationState.Merged);
        int closed = tracked.Values.Count(static state => state == ObservationState.Closed);
        int conflicting = tracked.Values.Count(static state => state == ObservationState.Conflicting);
        string token = ObserveTokens.AllResolved;
        return new ObserveResult(
            token,
            $"{token} {scope.Base} (total={tracked.Count}, merged={merged}, closed={closed}, conflict={conflicting})",
            Observation: null,
            IsAllResolved: true,
            TotalObserved: tracked.Count,
            MergedCount: merged,
            ClosedCount: closed,
            ConflictCount: conflicting);
    }

    private static void RecordMergedHistory(ObserveState state, IReadOnlyList<MergedPr> merged, int cadenceWindow)
    {
        foreach (MergedPr pr in merged)
        {
            if (state.RecentMerges.Any(existing => existing.Number == pr.Number))
            {
                continue;
            }

            state.RecentMerges.Add(pr);
        }

        int maxCount = Math.Max(2, cadenceWindow + 1);
        state.RecentMerges.Sort((a, b) => a.MergedAt.CompareTo(b.MergedAt));
        if (state.RecentMerges.Count > maxCount)
        {
            state.RecentMerges.RemoveRange(0, state.RecentMerges.Count - maxCount);
        }
    }

    private static void PruneHandledMergedPrs(ObserveState state)
    {
        if (state.Since is not { } since)
        {
            return;
        }

        foreach (int number in state.HandledMergedPrs.Where(kvp => kvp.Value < since).Select(kvp => kvp.Key).ToArray())
        {
            state.HandledMergedPrs.Remove(number);
        }
    }

    private static PollingTuning BuildTuning(int? minInterval, int? maxInterval, int? cadenceWindow, double? cadenceDecay, double? backoff)
    {
        PollingTuning t = PollingTuning.Default;
        return t with
        {
            MinIntervalSeconds = minInterval is { } min && min > 0 ? min : t.MinIntervalSeconds,
            MaxIntervalSeconds = maxInterval is { } max && max > 0 ? max : t.MaxIntervalSeconds,
            CadenceWindow = cadenceWindow is { } window && window > 0 ? window : t.CadenceWindow,
            CadenceDecay = cadenceDecay is { } decay and > 0 and <= 1 ? decay : t.CadenceDecay,
            BackoffFactor = backoff is { } factor && factor > 1 ? factor : t.BackoffFactor,
        };
    }

    private sealed class ObserveState
    {
        public DateTimeOffset? Since { get; set; }

        public string? ETag { get; set; }

        public double IntervalSeconds { get; set; }

        public Dictionary<int, DateTimeOffset> HandledMergedPrs { get; } = [];

        public List<MergedPr> RecentMerges { get; } = [];

        public Dictionary<int, PrObservation> LastSnapshot { get; set; } = [];
    }

    private readonly record struct ObservePoll(
        IReadOnlyDictionary<int, PrObservation> Snapshot,
        IReadOnlyList<PrTransition> Transitions);

    private readonly record struct BuildContext(
        ObservationScope Scope,
        IOpenPrSource OpenSource,
        IMergedPrSource MergedSource,
        AdaptivePoller Poller,
        PollingTuning Tuning);
}
