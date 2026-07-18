namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;

/// <summary>
/// Read-only observation verbs over the same gh poll engine as <see cref="ReconcileCommand"/>:
/// <c>wait</c> blocks for terminal transitions and <c>watch</c> streams state transitions.
/// </summary>
internal static class ObserveCommand
{
    /// <summary>Runs <c>octoshift wait &lt;scope&gt;</c>.</summary>
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

    private static async Task<int> WaitAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning,
        bool all)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        var state = new ObserveState { IntervalSeconds = tuning.MinIntervalSeconds };
        var tracked = new Dictionary<int, ObservationState>();
        bool trackingSetEstablished = false;
        bool firstPoll = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ObservePoll poll = await PollOnceAsync(scope, openSource, mergedSource, state, poller, tuning, ct);

                if (all)
                {
                    if (!trackingSetEstablished && poll.Snapshot.Count > 0)
                    {
                        trackingSetEstablished = true;
                        foreach ((int number, PrObservation observation) in poll.Snapshot)
                        {
                            tracked[number] = observation.State;
                        }
                    }

                    if (trackingSetEstablished)
                    {
                        foreach ((int number, PrObservation observation) in poll.Snapshot)
                        {
                            if (tracked.ContainsKey(number))
                            {
                                tracked[number] = observation.State;
                            }
                        }

                        if (tracked.Count > 0 && tracked.Values.All(ObservationDecision.IsTerminal))
                        {
                            Console.WriteLine(BuildAllResolvedLine(scope, tracked));
                            return ExitCode.Ok;
                        }
                    }
                }
                else if (!firstPoll)
                {
                    PrTransition? terminal = poll.Transitions.FirstOrDefault(static transition => transition.IsTerminal);
                    if (terminal is { } resolved)
                    {
                        Console.WriteLine(BuildTransitionLine(resolved));
                        return ExitCode.Ok;
                    }
                }

                firstPoll = false;
                await Task.Delay(TimeSpan.FromSeconds(state.IntervalSeconds), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
            return ExitCode.Ok;
        }

        return ExitCode.Ok;
    }

    private static async Task<int> WatchAsync(
        ObservationScope scope,
        IOpenPrSource openSource,
        IMergedPrSource mergedSource,
        AdaptivePoller poller,
        PollingTuning tuning)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        var state = new ObserveState { IntervalSeconds = tuning.MinIntervalSeconds };
        bool firstPoll = true;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                ObservePoll poll = await PollOnceAsync(scope, openSource, mergedSource, state, poller, tuning, ct);

                if (!firstPoll)
                {
                    foreach (PrTransition transition in poll.Transitions)
                    {
                        Console.WriteLine(BuildTransitionLine(transition));
                    }
                }

                firstPoll = false;
                await Task.Delay(TimeSpan.FromSeconds(state.IntervalSeconds), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
            return ExitCode.Ok;
        }

        return ExitCode.Ok;
    }

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

        RecordMergedHistory(state, mergedPage.MergedPrs, tuning.CadenceWindow);

        var pollerState = new PollerState
        {
            PreviousIntervalSeconds = state.IntervalSeconds,
            EstimatedGapSeconds = poller.EstimateGapSeconds(state.RecentMerges.ToArray()),
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

    private static string BuildTransitionLine(PrTransition transition)
        => $"{transition.Token} {transition.Current.OrderBase} (PR #{transition.Current.Number}, {transition.Current.HeadBranch})";

    private static string BuildAllResolvedLine(ObservationScope scope, IReadOnlyDictionary<int, ObservationState> tracked)
    {
        int merged = tracked.Values.Count(static state => state == ObservationState.Merged);
        int closed = tracked.Values.Count(static state => state == ObservationState.Closed);
        int conflicting = tracked.Values.Count(static state => state == ObservationState.Conflicting);
        return $"{ObserveTokens.AllResolved} {scope.Base} (total={tracked.Count}, merged={merged}, closed={closed}, conflict={conflicting})";
    }

    private static void RecordMergedHistory(ObserveState state, IReadOnlyList<MergedPr> merged, int cadenceWindow)
    {
        foreach (MergedPr pr in merged)
        {
            if (state.RecentMergePrs.Add(pr.Number))
            {
                state.RecentMerges.Add(pr.MergedAt);
            }
        }

        int maxCount = Math.Max(2, cadenceWindow + 1);
        state.RecentMerges.Sort();
        if (state.RecentMerges.Count <= maxCount)
        {
            return;
        }

        int drop = state.RecentMerges.Count - maxCount;
        state.RecentMerges.RemoveRange(0, drop);
        if (state.Since is not { } since)
        {
            return;
        }

        foreach (int number in state.HandledMergedPrs.Where(kvp => kvp.Value < since).Select(kvp => kvp.Key).ToArray())
        {
            state.RecentMergePrs.Remove(number);
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

        public HashSet<int> RecentMergePrs { get; } = [];

        public List<DateTimeOffset> RecentMerges { get; } = [];

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
