namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;

/// <summary>
/// <c>octoshift reconcile</c> — the membrane controller (design doc §4, §5, §8): inbound merge→land plus
/// outbound done→PR-open, conflict/CI→rework, and fan-out issue closing. Octoshift links nothing: it reads
/// the board via <c>nightshift where --output json</c> and mutates coordination via subprocess calls, while
/// GitHub reads/writes stay behind gh-backed interfaces. Long-running with a clean Ctrl-C exit (0);
/// <c>--once</c> does one sweep and exits (cron/testing). Poll cadence is adaptive (see
/// <see cref="AdaptivePoller"/>).
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
        var existingPrSource = new GhExistingOrderPrSource(scope);
        var openSource = new GhOpenPrSource(scope);
        var orderIssueSource = new GhOrderIssuePrSource(scope);
        var issueClient = new GhIssueClient(scope);
        var poller = new AdaptivePoller(tuning);
        IPrOpenSource prOpenSource = CreatePrOpenSource(scope, out GitHubAppInstallationTokenProvider? tokenProvider);

        try
        {
            if (once)
            {
                int mergedCode = await RunOnceAsync(nightshift, source, ct);
                try
                {
                    await SweepOpenPrOnceAsync(nightshift, existingPrSource, prOpenSource, ct);
                    await SweepReworkOnceAsync(nightshift, openSource, ct);
                    await SweepIssueCloseOnceAsync(orderIssueSource, issueClient, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                }

                return mergedCode;
            }

            await RunLoopAsync(nightshift, source, existingPrSource, prOpenSource, openSource, orderIssueSource, issueClient, poller, tuning, ct);
            return ExitCode.Ok;
        }
        finally
        {
            tokenProvider?.Dispose();
        }
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

    /// <summary>
    /// A single outbound PR-open sweep (§5 remote-dev): read the board, compute done orders with no existing
    /// OPEN/MERGED PR, and open each branch idempotently.
    /// </summary>
    internal static async Task<IReadOnlyList<OpenPrAction>> SweepOpenPrOnceAsync(
        INightshiftClient nightshift,
        IExistingOrderPrSource existingPrSource,
        IPrOpenSource prOpenSource,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        ExistingOrderPrsSnapshot existing = await existingPrSource.FetchOpenOrMergedAsync(ct);
        if (!existing.Success)
        {
            return [];
        }

        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(board, existing.OpenOrMergedHeadBranches);
        await ApplyPrOpenAsync(prOpenSource, actions, new HashSet<string>(StringComparer.Ordinal), ct);
        return actions;
    }

    /// <summary>
    /// Resident-loop outbound PR-open pass, deduped across polls via
    /// <see cref="ReconcileState.OpenedOrders"/> so eventual-consistency windows cannot duplicate opens.
    /// </summary>
    internal static async Task<IReadOnlyList<OpenPrAction>> OpenPrPassAsync(
        INightshiftClient nightshift,
        IExistingOrderPrSource existingPrSource,
        IPrOpenSource prOpenSource,
        ReconcileState state,
        CancellationToken ct)
    {
        BoardState board = await nightshift.GetBoardAsync(ct);
        ExistingOrderPrsSnapshot existing = await existingPrSource.FetchOpenOrMergedAsync(ct);
        if (!existing.Success)
        {
            return [];
        }

        IReadOnlyList<OpenPrAction> actions = OpenPrDecision.Decide(board, existing.OpenOrMergedHeadBranches);
        await ApplyPrOpenAsync(prOpenSource, actions, state.OpenedOrders, ct);
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
    /// A single fan-out issue-close sweep (§4.3): compute closable issues from bound order-PRs and close
    /// each still-open issue once with a short pointer to the merged orders that fulfilled it.
    /// </summary>
    internal static async Task<IReadOnlyList<IssueCloseAction>> SweepIssueCloseOnceAsync(
        IOrderIssuePrSource source,
        IIssueClient issueClient,
        CancellationToken ct)
    {
        IReadOnlyList<OrderIssuePr> orderPrs = await source.FetchOrderIssuePrsAsync(ct);
        IReadOnlyList<IssueCloseAction> actions = IssueCloseDecision.Decide(orderPrs);
        await ApplyIssueClosuresAsync(issueClient, actions, new HashSet<int>(), ct);
        return actions;
    }

    /// <summary>The resident-loop issue-close pass, deduped across polls via <see cref="ReconcileState.HandledIssues"/>.</summary>
    internal static async Task<IReadOnlyList<IssueCloseAction>> IssueClosePassAsync(
        IOrderIssuePrSource source,
        IIssueClient issueClient,
        ReconcileState state,
        CancellationToken ct)
    {
        IReadOnlyList<OrderIssuePr> orderPrs = await source.FetchOrderIssuePrsAsync(ct);
        IReadOnlyList<IssueCloseAction> actions = IssueCloseDecision.Decide(orderPrs);
        await ApplyIssueClosuresAsync(issueClient, actions, state.HandledIssues, ct);
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
                    // Not a routed action — surfaced for the Planner or Product Manager, never as success-shaped stdout (§4.3, §9.5).
                    Console.Error.WriteLine($"ESCALATE {action.OrderBase} (PR #{action.PrNumber} {action.Directive}: needs the Planner or Product Manager)");
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
        IExistingOrderPrSource existingPrSource,
        IPrOpenSource prOpenSource,
        IOpenPrSource openSource,
        IOrderIssuePrSource orderIssueSource,
        IIssueClient issueClient,
        AdaptivePoller poller,
        PollingTuning tuning,
        CancellationToken ct)
    {
        var state = new ReconcileState { IntervalSeconds = tuning.MinIntervalSeconds };
        string? lastMode = null;

        Console.WriteLine("octoshift: reconciling merged PRs -> nightshift land, done orders -> gh pr create, open PRs -> nightshift rework, fan-out issues -> close (Ctrl-C to stop)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                PollerState pollerState = await PollOnceAsync(nightshift, source, state, poller, tuning, ct);
                await OpenPrPassAsync(nightshift, existingPrSource, prOpenSource, state, ct);
                await ReworkPassAsync(nightshift, openSource, state, ct);
                await IssueClosePassAsync(orderIssueSource, issueClient, state, ct);

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

    /// <summary>
    /// Applies outbound PR-open actions and emits one additive token line on success:
    /// <c>OPENED /plan/&lt;plan&gt;/order/&lt;order&gt; (PR #&lt;n&gt;)</c>.
    /// </summary>
    private static async Task ApplyPrOpenAsync(
        IPrOpenSource prOpenSource,
        IReadOnlyList<OpenPrAction> actions,
        HashSet<string> openedOrders,
        CancellationToken ct)
    {
        foreach (OpenPrAction action in actions)
        {
            if (openedOrders.Contains(action.OrderBase))
            {
                continue;
            }

            PrOpenOutcome outcome = await prOpenSource.OpenAsync(action.OrderBase, action.HeadBranch, ct);
            switch (outcome.Kind)
            {
                case PrOpenOutcomeKind.Opened:
                    Console.WriteLine($"OPENED {action.OrderBase} (PR #{outcome.PrNumber})");
                    openedOrders.Add(action.OrderBase);
                    break;
                case PrOpenOutcomeKind.AlreadyExists:
                    openedOrders.Add(action.OrderBase);
                    break;
            }
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

        return new ApplyResult(actions, landedSomething);
    }

    private static async Task ApplyIssueClosuresAsync(
        IIssueClient issueClient,
        IReadOnlyList<IssueCloseAction> actions,
        HashSet<int> handledIssues,
        CancellationToken ct)
    {
        foreach (IssueCloseAction action in actions)
        {
            if (handledIssues.Contains(action.IssueNumber))
            {
                continue;
            }

            IssueState state = await issueClient.GetIssueStateAsync(action.IssueNumber, ct);
            if (state == IssueState.Closed)
            {
                handledIssues.Add(action.IssueNumber);
                continue;
            }

            if (state != IssueState.Open)
            {
                continue;
            }

            IssueCloseOutcome outcome = await issueClient.CloseIssueAsync(action.IssueNumber, BuildIssueCloseComment(action), ct);
            switch (outcome)
            {
                case IssueCloseOutcome.Closed:
                    Console.WriteLine($"CLOSED #{action.IssueNumber} ({string.Join(", ", action.Orders)})");
                    handledIssues.Add(action.IssueNumber);
                    break;
                case IssueCloseOutcome.AlreadyClosed:
                    handledIssues.Add(action.IssueNumber);
                    break;
            }
        }
    }

    private static string BuildIssueCloseComment(IssueCloseAction action)
        => $"Closing after all bound Nightshift orders merged: {string.Join(", ", action.Orders)}.";

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
        public HashSet<int> HandledIssues { get; } = [];
        public HashSet<string> OpenedOrders { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ApplyResult(IReadOnlyList<LandAction> Actions, bool LandedSomething);

    internal static IPrOpenSource CreatePrOpenSource(
        string repo,
        out GitHubAppInstallationTokenProvider? tokenProvider)
        => CreatePrOpenSource(
            repo,
            new FileGitHubAppCredentialsSource(),
            NullPrOpenMetadataProvider.Instance,
            NullPrOpenAuditSink.Instance,
            out tokenProvider);

    internal static IPrOpenSource CreatePrOpenSource(
        string repo,
        IGitHubAppCredentialsSource credentialsSource,
        IPrOpenMetadataProvider metadataProvider,
        IPrOpenAuditSink auditSink,
        out GitHubAppInstallationTokenProvider? tokenProvider)
    {
        ArgumentNullException.ThrowIfNull(credentialsSource);
        ArgumentNullException.ThrowIfNull(metadataProvider);
        ArgumentNullException.ThrowIfNull(auditSink);

        tokenProvider = null;
        try
        {
            GitHubAppCredentials credentials = credentialsSource.Load();
            tokenProvider = new GitHubAppInstallationTokenProvider(credentials);
            Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync = GhAuthenticatedRunner.Create(tokenProvider);
            return new GhPrOpenSource(repo, credentials.Actor, metadataProvider, auditSink, runGhAsync, () => DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new DisabledPrOpenSource(ex.Message);
        }
    }
}
