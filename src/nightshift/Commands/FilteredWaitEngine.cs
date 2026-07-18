namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// Shared role-wait engine: block on scoped watch edges, reconcile by reading values, and return once a
/// role predicate matches. Non-matching edges re-arm internally; callers never parse the raw stream.
/// </summary>
internal static class FilteredWaitEngine
{
    internal static TimeSpan KeepAliveCadence { get; set; } = TimeSpan.FromMinutes(5);

    internal sealed record WatchScope(string Name, Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> Watch);

    internal sealed record WatchEdge(string Scope, WatchSignal Signal);

    internal sealed record WaitResult<T>(long Revision, bool TimedOut, T? Match) where T : class
    {
        public static WaitResult<T> Timeout(long revision) => new(revision, TimedOut: true, Match: null);

        public static WaitResult<T> Matched(long revision, T match) => new(revision, TimedOut: false, match);
    }

    internal static async Task<WaitResult<T>> WaitForMatchAsync<T>(
        IReadOnlyList<WatchScope> scopes,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        DateTime? deadline,
        Func<CancellationToken, Task> keepAliveAsync,
        Func<WatchEdge, CancellationToken, Task<T?>> reconcileAsync,
        CancellationToken ct) where T : class
    {
        long revision = fromRevision;
        DateTime nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);

        while (true)
        {
            DateTime now = DateTime.UtcNow;
            if (deadline is { } d && now >= d)
            {
                return WaitResult<T>.Timeout(revision);
            }

            TimeSpan budget = NextCommand.ComputeWaitBudget(now, deadline, nextKeepAliveAt);
            ScopedWaitResult wake = await WaitForScopedSignalCoreAsync(scopes, currentRevision, revision, budget, ct);
            revision = wake.Revision;

            if (wake.Edge is { } edge && await reconcileAsync(edge, ct) is { } match)
            {
                return WaitResult<T>.Matched(revision, match);
            }

            if (wake.Source != ScopedWakeSource.Timeout || DateTime.UtcNow >= nextKeepAliveAt)
            {
                await keepAliveAsync(ct);
                nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);
            }
        }
    }

    internal static async Task<ScopedWaitResult> WaitForScopedSignalCoreAsync(
        IReadOnlyList<WatchScope> scopes,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        TimeSpan budget,
        CancellationToken ct)
    {
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one watch scope is required.", nameof(scopes));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ScopedWatcher[] watchers = scopes
            .Select(scope => new ScopedWatcher(scope.Name, WaitForPrefixChangeCoreAsync(scope.Watch, currentRevision, fromRevision, linked.Token)))
            .ToArray();

        Task timeoutTask = budget == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
            : Task.Delay(budget, linked.Token);

        Task winner = await Task.WhenAny([.. watchers.Select(w => w.Task), timeoutTask]);
        if (winner == timeoutTask)
        {
            linked.Cancel();
            await ObserveCanceledWatchersAsync(watchers.Select(w => w.Task), ct);
            return new ScopedWaitResult(fromRevision, ScopedWakeSource.Timeout, Edge: null);
        }

        int index = Array.FindIndex(watchers, w => ReferenceEquals(w.Task, winner));
        WatchOutcome outcome = await watchers[index].Task;
        linked.Cancel();
        await ObserveCanceledWatchersAsync(watchers.Select(w => w.Task), ct);
        WatchEdge? edge = outcome.Signal is null ? null : new WatchEdge(watchers[index].Name, outcome.Signal);
        return new ScopedWaitResult(outcome.Revision, ScopedWakeSource.Signal, edge);
    }

    private static async Task ObserveCanceledWatchersAsync(IEnumerable<Task<WatchOutcome>> tasks, CancellationToken ct)
    {
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The losing watchers are canceled after one scope wins — expected.
        }
    }

    private static async Task<WatchOutcome> WaitForPrefixChangeCoreAsync(
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watch,
        Func<CancellationToken, Task<long>> currentRevision,
        long fromRevision,
        CancellationToken ct)
    {
        try
        {
            await foreach (WatchSignal signal in watch(fromRevision, ct))
            {
                return new WatchOutcome(signal.Revision, signal);
            }
        }
        catch (WatchCompactedException)
        {
            return new WatchOutcome(await currentRevision(ct), Signal: null);
        }

        return new WatchOutcome(fromRevision, Signal: null);
    }

    internal sealed record ScopedWaitResult(long Revision, ScopedWakeSource Source, WatchEdge? Edge);

    internal enum ScopedWakeSource
    {
        Timeout,
        Signal,
    }

    private sealed record WatchOutcome(long Revision, WatchSignal? Signal);

    private sealed record ScopedWatcher(string Name, Task<WatchOutcome> Task);
}
