namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;

/// <summary>
/// <c>octoshift reconcile</c> — the inbound merge→land membrane (design doc §4.1, §8). It reconciles
/// GitHub's <b>merge</b> truth against Turnstile's <b>dispatch</b> truth: poll GitHub for merged nightshift
/// PRs and, for each one whose order is not already landed, run <c>nightshift land &lt;base&gt;</c> — a pure
/// Turnstile write that wakes the running <c>plan</c> controller, which promotes dependents. Octoshift links
/// nothing: it reads the board via <c>nightshift where --output json</c> and lands via <c>nightshift land</c>,
/// both as subprocesses, and it is the only gh-aware component. It never touches git or code. Long-running
/// with a clean Ctrl-C exit (0); <c>--once</c> does a single sweep and exits (cron/testing). Poll cadence is
/// adaptive (see <see cref="AdaptivePoller"/>).
/// </summary>
internal static class ReconcileCommand
{
    private const string By = "octoshift";

    public static async Task<int> RunAsync(
        string? repo,
        string? socket,
        bool once,
        int? minInterval,
        int? maxInterval,
        int? cadenceWindow,
        double? cadenceDecay,
        double? backoff)
    {
        string? scope = RepoScope.Resolve(repo);
        if (scope is null)
        {
            Console.Error.WriteLine("octoshift reconcile: could not resolve a repo — run inside a git worktree or pass --repo owner/name");
            return ExitCode.Usage;
        }

        PollingTuning tuning = BuildTuning(minInterval, maxInterval, cadenceWindow, cadenceDecay, backoff);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        var nightshift = new NightshiftCli(socket);
        var source = new GhMergedPrSource(scope);
        var openSource = new GhOpenPrSource(scope);
        var poller = new AdaptivePoller(tuning);

        if (once)
        {
            int mergedCode = await RunOnceAsync(nightshift, source, ct);
            try
            {
                await SweepReworkOnceAsync(nightshift, openSource, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
            }

            return mergedCode;
        }

        await RunLoopAsync(nightshift, source, openSource, poller, tuning, ct);
        return ExitCode.Ok;
    }

    /// <summary>A single sweep: read the board, fetch all recent merges, land everything merged-but-unlanded.</summary>
    internal static async Task<IReadOnlyList<LandAction>> SweepOnceAsync(
        INightshiftClient nightshift,
        IMergedPrSource source,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        MergedPrPage page = await source.FetchMergedAsync(null, null, ct);
        ApplyResult result = await ApplyAsync(nightshift, board, page.MergedPrs, new Dictionary<int, DateTimeOffset>(), ct);
        return result.Actions;
    }

    /// <summary>A single rework sweep: read the board, fetch all open order-PRs, bounce every eligible one.</summary>
    internal static async Task<IReadOnlyList<ReworkAction>> SweepReworkOnceAsync(
        INightshiftClient nightshift,
        IOpenPrSource source,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        IReadOnlyList<OpenPr> open = await source.FetchOpenAsync(ct);
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(open, board);
        await ApplyReworkAsync(nightshift, actions, new HashSet<int>(), ct);
        return actions;
    }

    /// <summary>The resident-loop rework pass: like the sweep, but dedups escalations across polls via the run state.</summary>
    internal static async Task<IReadOnlyList<ReworkAction>> ReworkPassAsync(
        INightshiftClient nightshift,
        IOpenPrSource source,
        ReconcileState state,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        IReadOnlyList<OpenPr> open = await source.FetchOpenAsync(ct);
        IReadOnlyList<ReworkAction> actions = ReworkDecision.Decide(open, board);
        await ApplyReworkAsync(nightshift, actions, state.EscalatedPrs, ct);
        return actions;
    }

    /// <summary>
    /// Routes each decided rework action: a bounce goes to <c>nightshift rework</c> (idempotent — the board
    /// gate stops a second bounce once it flips to <c>changes-requested</c>) and echoes <c>REWORK</c>; an
    /// escalation takes no coordination action and surfaces a single <c>ESCALATE</c> line per closed PR,
    /// deduped through <paramref name="escalated"/> so a resident loop never re-alerts the same PR.
    /// </summary>
    private static async Task ApplyReworkAsync(
        INightshiftClient nightshift,
        IReadOnlyList<ReworkAction> actions,
        HashSet<int> escalated,
        CancellationToken ct)
    {
        foreach (ReworkAction action in actions)
        {
            if (action.Kind == ReworkKind.Escalate)
            {
                if (escalated.Add(action.PrNumber))
                {
                    // Not a routed action — surfaced for a human, never as success-shaped stdout (§4.3, §9.5).
                    Console.Error.WriteLine($"ESCALATE {action.OrderBase} (PR #{action.PrNumber} {action.Directive}: needs a human)");
                }

                continue;
            }

            if (await nightshift.ReworkAsync(action.OrderBase, action.Directive, ct))
            {
                Console.WriteLine($"REWORK {action.OrderBase} ({action.Directive})");
            }
        }
    }

    internal static async Task<int> RunOnceAsync(
        INightshiftClient nightshift,
        IMergedPrSource source,
        CancellationToken ct)
    {
        try
        {
            await SweepOnceAsync(nightshift, source, ct);
            return ExitCode.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
            return ExitCode.Ok;
        }
    }

    private static async Task RunLoopAsync(
        INightshiftClient nightshift,
        IMergedPrSource source,
        IOpenPrSource openSource,
        AdaptivePoller poller,
        PollingTuning tuning,
        CancellationToken ct)
    {
        var state = new ReconcileState { IntervalSeconds = tuning.MinIntervalSeconds };
        string? lastMode = null;

        Console.WriteLine("octoshift: reconciling merged PRs -> nightshift land, open PRs -> nightshift rework (Ctrl-C to stop)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                PollerState pollerState = await PollOnceAsync(nightshift, source, state, poller, tuning, ct);
                await ReworkPassAsync(nightshift, openSource, state, ct);

                state.IntervalSeconds = poller.NextIntervalSeconds(pollerState, Random.Shared.NextDouble());
                lastMode = NoteTransition(pollerState, state.IntervalSeconds, lastMode);

                await Task.Delay(TimeSpan.FromSeconds(state.IntervalSeconds), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
        }
    }

    internal static async Task<PollerState> PollOnceAsync(
        INightshiftClient nightshift,
        IMergedPrSource source,
        ReconcileState state,
        AdaptivePoller poller,
        PollingTuning tuning,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        MergedPrPage page = await source.FetchMergedAsync(state.Since, state.ETag, ct);
        state.ETag = page.ETag;

        ApplyResult result = await ApplyAsync(nightshift, board, page.MergedPrs, state.HandledPrs, ct);
        RecordHistory(state, page.MergedPrs, tuning.CadenceWindow);
        state.Since = AdvanceWatermark(state.Since, page.MergedPrs, state.HandledPrs, page.Truncated, page.OldestSeenMergedAt);
        PruneHandledPrs(state);

        var outcome = new PollOutcome
        {
            LandedSomething = result.LandedSomething,
            RateLimited = page.RateLimited,
            ProviderMinIntervalSeconds = page.ProviderMinIntervalSeconds,
            RateLimitResetSeconds = page.RateLimitResetSeconds,
        };

        return new PollerState
        {
            PreviousIntervalSeconds = state.IntervalSeconds,
            EstimatedGapSeconds = poller.EstimateGapSeconds(state.RecentMerges.Select(pr => pr.MergedAt).ToArray()),
            BoardHasOutstandingDone = board.HasOutstandingDone,
            LastPoll = outcome,
        };
    }

    /// <summary>Lands every decided order via <c>nightshift land</c> (idempotent) and echoes one line each.</summary>
    private static async Task<ApplyResult> ApplyAsync(
        INightshiftClient nightshift,
        BoardState board,
        IEnumerable<MergedPr> merged,
        IDictionary<int, DateTimeOffset> handledPrs,
        CancellationToken ct)
    {
        var actions = new List<LandAction>();
        var attemptedOrders = new HashSet<string>(StringComparer.Ordinal);
        var landedOrders = new HashSet<string>(StringComparer.Ordinal);
        bool landedSomething = false;

        foreach (MergedPr pr in merged)
        {
            if (OrderRef.FromBranch(pr.HeadBranch) is not { } order)
            {
                continue;
            }

            string orderBase = order.Base;
            if (handledPrs.ContainsKey(pr.Number))
            {
                landedOrders.Add(orderBase);
                continue;
            }

            if (board.IsLanded(orderBase) || landedOrders.Contains(orderBase))
            {
                handledPrs[pr.Number] = pr.MergedAt;
                landedOrders.Add(orderBase);
                continue;
            }

            if (!attemptedOrders.Add(orderBase))
            {
                continue;
            }

            var action = new LandAction(orderBase, pr.Number);
            if (await nightshift.LandAsync(action.OrderBase, action.Reason, ct))
            {
                Console.WriteLine($"LANDED {action.OrderBase} ({action.Reason})");
                actions.Add(action);
                handledPrs[pr.Number] = pr.MergedAt;
                landedOrders.Add(orderBase);
                landedSomething = true;
            }
        }

        foreach (string orderBase in board.OutstandingDoneOrders)
        {
            if (attemptedOrders.Contains(orderBase) || landedOrders.Contains(orderBase))
            {
                continue;
            }

            const string reason = "board done";
            if (await nightshift.LandAsync(orderBase, reason, ct))
            {
                Console.WriteLine($"LANDED {orderBase} ({reason})");
                landedSomething = true;
            }
        }

        return new ApplyResult(actions, landedSomething);
    }

    internal static DateTimeOffset? AdvanceWatermark(
        DateTimeOffset? current,
        IReadOnlyList<MergedPr> merged,
        IReadOnlyDictionary<int, DateTimeOffset> handledPrs,
        bool truncated = false,
        DateTimeOffset? oldestSeenMergedAt = null)
    {
        DateTimeOffset? firstUnhandled = null;
        foreach (MergedPr pr in merged)
        {
            if (handledPrs.ContainsKey(pr.Number))
            {
                continue;
            }

            firstUnhandled = firstUnhandled is null || pr.MergedAt < firstUnhandled.Value ? pr.MergedAt : firstUnhandled;
        }

        DateTimeOffset? advanced = current;
        foreach (MergedPr pr in merged)
        {
            if (!handledPrs.ContainsKey(pr.Number)
                || (firstUnhandled is { } barrier && pr.MergedAt >= barrier))
            {
                continue;
            }

            if (advanced is null || pr.MergedAt > advanced)
            {
                advanced = pr.MergedAt;
            }
        }

        if (firstUnhandled is null)
        {
            foreach (MergedPr pr in merged)
            {
                if (handledPrs.ContainsKey(pr.Number) && (advanced is null || pr.MergedAt > advanced))
                {
                    advanced = pr.MergedAt;
                }
            }
        }

        if (truncated && oldestSeenMergedAt is { } boundary && (advanced is null || advanced > boundary))
        {
            advanced = boundary;
        }

        return advanced;
    }

    private static void RecordHistory(ReconcileState state, IReadOnlyList<MergedPr> merged, int cadenceWindow)
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

    private static void PruneHandledPrs(ReconcileState state)
    {
        if (state.Since is not { } since)
        {
            return;
        }

        foreach (int number in state.HandledPrs.Where(kvp => kvp.Value < since).Select(kvp => kvp.Key).ToArray())
        {
            state.HandledPrs.Remove(number);
        }
    }

    /// <summary>Prints a low-verbosity note only when the pacing mode changes; returns the new mode.</summary>
    private static string NoteTransition(PollerState state, double interval, string? lastMode)
    {
        string mode = state.BoardHasOutstandingDone ? "board-pinned"
            : state.LastPoll.LandedSomething ? "reset"
            : state.LastPoll.RateLimited ? "rate-limited"
            : "backoff";

        if (mode != lastMode)
        {
            Console.WriteLine($"octoshift: {mode}, polling every ~{(int)Math.Round(interval)}s");
        }

        return mode;
    }

    private static PollingTuning BuildTuning(int? minInterval, int? maxInterval, int? cadenceWindow, double? cadenceDecay, double? backoff)
    {
        PollingTuning t = PollingTuning.Default;
        return t with
        {
            MinIntervalSeconds = minInterval is { } min && min > 0 ? min : t.MinIntervalSeconds,
            MaxIntervalSeconds = maxInterval is { } max && max > 0 ? max : t.MaxIntervalSeconds,
            CadenceWindow = cadenceWindow is { } w && w > 0 ? w : t.CadenceWindow,
            CadenceDecay = cadenceDecay is { } d and > 0 and <= 1 ? d : t.CadenceDecay,
            BackoffFactor = backoff is { } b && b > 1 ? b : t.BackoffFactor,
        };
    }

    internal sealed class ReconcileState
    {
        public DateTimeOffset? Since { get; set; }
        public string? ETag { get; set; }
        public double IntervalSeconds { get; set; }
        public Dictionary<int, DateTimeOffset> HandledPrs { get; } = [];
        public List<MergedPr> RecentMerges { get; } = [];
        public HashSet<int> EscalatedPrs { get; } = [];
    }

    private sealed record ApplyResult(IReadOnlyList<LandAction> Actions, bool LandedSomething);
}
