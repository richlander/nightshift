namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// Shared role-wait engine: block on scoped watch edges, reconcile by reading values, and return once a
/// role predicate matches. Non-matching edges re-arm internally; callers never parse the raw stream.
/// </summary>
internal static class FilteredWaitEngine
{
    internal static TimeSpan KeepAliveCadence { get; set; } = TimeSpan.FromMinutes(5);
    internal static TimeSpan NoneRearmDelay { get; set; } = TimeSpan.FromMilliseconds(100);

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
        Func<CancellationToken, Task<T?>> reconcileSnapshotAsync,
        CancellationToken ct) where T : class
    {
        var floors = scopes.ToDictionary(s => s.Name, _ => fromRevision, StringComparer.Ordinal);
        DateTime nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);

        while (true)
        {
            DateTime now = DateTime.UtcNow;
            if (deadline is { } d && now >= d)
            {
                return WaitResult<T>.Timeout(MaxFloor(floors));
            }

            TimeSpan budget = NextCommand.ComputeWaitBudget(now, deadline, nextKeepAliveAt);
            long previousFloor = MaxFloor(floors);
            ScopedWaitResult wake = await WaitForScopedSignalCoreAsync(scopes, floors, currentRevision, budget, ct);
            if (wake.Scope is { } scope)
            {
                floors[scope] = wake.Revision;
            }

            bool advanced = MaxFloor(floors) > previousFloor;

            if (wake.Source == ScopedWakeSource.Signal
                && wake.Edge is { } edge
                && await reconcileAsync(edge, ct) is { } match)
            {
                return WaitResult<T>.Matched(MaxFloor(floors), match);
            }

            if (wake.Source == ScopedWakeSource.Compacted
                && await reconcileSnapshotAsync(ct) is { } snapshotMatch)
            {
                return WaitResult<T>.Matched(MaxFloor(floors), snapshotMatch);
            }

            if (wake.Source == ScopedWakeSource.None)
            {
                TimeSpan rearmDelay = ComputeNoneRearmDelay(DateTime.UtcNow, deadline);
                if (rearmDelay > TimeSpan.Zero)
                {
                    await Task.Delay(rearmDelay, ct);
                }
            }

            bool shouldKeepAlive = wake.Source switch
            {
                ScopedWakeSource.Signal => true,
                ScopedWakeSource.Compacted => true,
                _ => DateTime.UtcNow >= nextKeepAliveAt,
            };

            if (shouldKeepAlive)
            {
                await keepAliveAsync(ct);
                nextKeepAliveAt = DateTime.UtcNow.Add(KeepAliveCadence);
            }
        }
    }

    internal static async Task<ScopedWaitResult> WaitForScopedSignalCoreAsync(
        IReadOnlyList<WatchScope> scopes,
        IReadOnlyDictionary<string, long> floors,
        Func<CancellationToken, Task<long>> currentRevision,
        TimeSpan budget,
        CancellationToken ct)
    {
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one watch scope is required.", nameof(scopes));
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ScopedWatcher[] watchers = scopes
            .Select(scope => new ScopedWatcher(scope.Name, WaitForPrefixChangeCoreAsync(scope.Watch, currentRevision, floors[scope.Name], linked.Token)))
            .ToArray();

        Task timeoutTask = budget == Timeout.InfiniteTimeSpan
            ? Task.Delay(Timeout.InfiniteTimeSpan, linked.Token)
            : Task.Delay(budget, linked.Token);

        Task winner = await Task.WhenAny([.. watchers.Select(w => w.Task), timeoutTask]);
        if (winner == timeoutTask)
        {
            linked.Cancel();
            await ObserveCanceledWatchersAsync(watchers.Select(w => w.Task), ct);
            return new ScopedWaitResult(MaxFloor(floors), ScopedWakeSource.Timeout, Scope: null, Edge: null);
        }

        int index = Array.FindIndex(watchers, w => ReferenceEquals(w.Task, winner));
        WatchOutcome outcome = await watchers[index].Task;
        linked.Cancel();
        await ObserveCanceledWatchersAsync(watchers.Select(w => w.Task), ct);

        return outcome.Kind switch
        {
            WatchOutcomeKind.Signal => new ScopedWaitResult(
                outcome.Revision,
                ScopedWakeSource.Signal,
                watchers[index].Name,
                new WatchEdge(watchers[index].Name, outcome.Signal!)),
            WatchOutcomeKind.Compacted => new ScopedWaitResult(
                outcome.Revision,
                ScopedWakeSource.Compacted,
                watchers[index].Name,
                Edge: null),
            _ => new ScopedWaitResult(
                outcome.Revision,
                ScopedWakeSource.None,
                watchers[index].Name,
                Edge: null),
        };
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
                return new WatchOutcome(signal.Revision, WatchOutcomeKind.Signal, signal);
            }
        }
        catch (WatchCompactedException)
        {
            return new WatchOutcome(await currentRevision(ct), WatchOutcomeKind.Compacted, Signal: null);
        }

        return new WatchOutcome(fromRevision, WatchOutcomeKind.None, Signal: null);
    }

    private static long MaxFloor(IReadOnlyDictionary<string, long> floors)
        => floors.Count == 0 ? 0 : floors.Values.Max();

    private static TimeSpan ComputeNoneRearmDelay(DateTime now, DateTime? deadline)
    {
        if (NoneRearmDelay <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        if (deadline is not { } d)
        {
            return NoneRearmDelay;
        }

        TimeSpan remaining = d - now;
        if (remaining <= TimeSpan.Zero)
        {
            return TimeSpan.Zero;
        }

        return remaining < NoneRearmDelay ? remaining : NoneRearmDelay;
    }

    internal sealed record ScopedWaitResult(long Revision, ScopedWakeSource Source, string? Scope, WatchEdge? Edge);

    internal enum ScopedWakeSource
    {
        Timeout,
        Signal,
        Compacted,
        None,
    }

    private sealed record WatchOutcome(long Revision, WatchOutcomeKind Kind, WatchSignal? Signal);

    private enum WatchOutcomeKind
    {
        Signal,
        Compacted,
        None,
    }

    private sealed record ScopedWatcher(string Name, Task<WatchOutcome> Task);
}
