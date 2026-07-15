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
        var poller = new AdaptivePoller(tuning);

        if (once)
        {
            await SweepOnceAsync(nightshift, source, ct);
            return ExitCode.Ok;
        }

        await RunLoopAsync(nightshift, source, poller, tuning, ct);
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
        return await ApplyAsync(nightshift, board, page.MergedPrs, ct);
    }

    private static async Task RunLoopAsync(
        INightshiftClient nightshift,
        IMergedPrSource source,
        AdaptivePoller poller,
        PollingTuning tuning,
        CancellationToken ct)
    {
        var history = new List<DateTimeOffset>();
        string? etag = null;
        DateTimeOffset? since = null;
        double interval = tuning.MinIntervalSeconds;
        string? lastMode = null;

        Console.WriteLine("octoshift: reconciling merged PRs -> nightshift land (Ctrl-C to stop)");

        try
        {
            while (!ct.IsCancellationRequested)
            {
                BoardState board = await nightshift.GetBoardAsync(ct);
                MergedPrPage page = await source.FetchMergedAsync(since, etag, ct);
                etag = page.ETag;

                IReadOnlyList<LandAction> actions = await ApplyAsync(nightshift, board, page.MergedPrs, ct);

                foreach (MergedPr pr in page.MergedPrs)
                {
                    history.Add(pr.MergedAt);
                    if (since is null || pr.MergedAt > since)
                    {
                        since = pr.MergedAt;
                    }
                }

                var outcome = new PollOutcome
                {
                    LandedSomething = actions.Count > 0,
                    RateLimited = page.RateLimited,
                    ProviderMinIntervalSeconds = page.ProviderMinIntervalSeconds,
                    RateLimitResetSeconds = page.RateLimitResetSeconds,
                };

                var state = new PollerState
                {
                    PreviousIntervalSeconds = interval,
                    EstimatedGapSeconds = poller.EstimateGapSeconds(history),
                    BoardHasOutstandingDone = board.HasOutstandingDone,
                    LastPoll = outcome,
                };

                interval = poller.NextIntervalSeconds(state, Random.Shared.NextDouble());
                lastMode = NoteTransition(state, interval, lastMode);

                await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("octoshift: stopped");
        }
    }

    /// <summary>Lands every decided order via <c>nightshift land</c> (idempotent) and echoes one line each.</summary>
    private static async Task<IReadOnlyList<LandAction>> ApplyAsync(
        INightshiftClient nightshift,
        BoardState board,
        IEnumerable<MergedPr> merged,
        CancellationToken ct)
    {
        IReadOnlyList<LandAction> actions = LandDecision.Decide(merged, board);
        foreach (LandAction action in actions)
        {
            if (await nightshift.LandAsync(action.OrderBase, action.Reason, ct))
            {
                Console.WriteLine($"LANDED {action.OrderBase} ({action.Reason})");
            }
        }

        return actions;
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
}
