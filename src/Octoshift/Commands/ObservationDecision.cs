namespace Octoshift.Commands;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>The normalized lifecycle octoshift observes for one in-scope order PR.</summary>
internal enum ObservationState
{
    /// <summary>Open and not currently conflicting (mergeable or unknown).</summary>
    Open,

    /// <summary>Open and conflicting — terminal for read-only <c>wait</c>.</summary>
    Conflicting,

    /// <summary>Closed without merge — terminal.</summary>
    Closed,

    /// <summary>Merged — terminal.</summary>
    Merged,
}

/// <summary>The normalized observed state of one in-scope order PR.</summary>
internal readonly record struct PrObservation(int Number, string HeadBranch, string OrderBase, ObservationState State);

/// <summary>A state transition for one observed PR between polls.</summary>
internal readonly record struct PrTransition(PrObservation? Previous, PrObservation Current)
{
    /// <summary>The machine-readable token for <see cref="Current"/>.</summary>
    public string Token => ObservationDecision.TokenOf(Current.State);

    /// <summary>True when the transition ends in a terminal resolution state.</summary>
    public bool IsTerminal => ObservationDecision.IsTerminal(Current.State);
}

/// <summary>
/// Pure scope-filtered observation logic: normalize open+merged feeds into one PR-state snapshot and compute
/// transitions between snapshots. No subprocesses and no side effects.
/// </summary>
internal static class ObservationDecision
{
    /// <summary>
    /// Builds the current scoped snapshot from open+merged feeds. Entries on non-order branches are dropped.
    /// For duplicate PR numbers, merged wins over closed/conflicting, which wins over open.
    /// </summary>
    public static IReadOnlyDictionary<int, PrObservation> BuildSnapshot(
        ObservationScope scope,
        IReadOnlyList<OpenPr> open,
        IReadOnlyList<MergedPr> merged)
    {
        var snapshot = new Dictionary<int, PrObservation>();

        foreach (OpenPr pr in open)
        {
            if (!scope.MatchesBranch(pr.HeadBranch) || OrderRef.FromBranch(pr.HeadBranch) is not { } order)
            {
                continue;
            }

            var candidate = new PrObservation(pr.Number, pr.HeadBranch, order.Base, ClassifyOpen(pr));
            Upsert(snapshot, candidate);
        }

        foreach (MergedPr pr in merged)
        {
            if (!scope.MatchesBranch(pr.HeadBranch) || OrderRef.FromBranch(pr.HeadBranch) is not { } order)
            {
                continue;
            }

            var candidate = new PrObservation(pr.Number, pr.HeadBranch, order.Base, ObservationState.Merged);
            Upsert(snapshot, candidate);
        }

        return snapshot;
    }

    /// <summary>
    /// Finds PRs whose observed state changed between polls (including newly-seen PRs), ordered by PR number.
    /// </summary>
    public static IReadOnlyList<PrTransition> Transitions(
        IReadOnlyDictionary<int, PrObservation> previous,
        IReadOnlyDictionary<int, PrObservation> current)
    {
        var transitions = new List<PrTransition>();
        foreach ((int number, PrObservation now) in current.OrderBy(static kvp => kvp.Key))
        {
            if (!previous.TryGetValue(number, out PrObservation before) || before.State != now.State)
            {
                transitions.Add(new PrTransition(previous.TryGetValue(number, out before) ? before : null, now));
            }
        }

        return transitions;
    }

    /// <summary>Returns terminal PRs in the current snapshot, ordered by PR number.</summary>
    public static IReadOnlyList<PrObservation> TerminalNow(IReadOnlyDictionary<int, PrObservation> snapshot)
        => snapshot.Values
            .Where(static observation => IsTerminal(observation.State))
            .OrderBy(static observation => observation.Number)
            .ToArray();

    /// <summary>True when at least one currently observed PR remains non-terminal.</summary>
    public static bool HasUnresolved(IReadOnlyDictionary<int, PrObservation> snapshot)
        => snapshot.Values.Any(static observation => !IsTerminal(observation.State));

    /// <summary>True for terminal states: merged, closed-unmerged, and conflicting.</summary>
    public static bool IsTerminal(ObservationState state)
        => state is ObservationState.Merged or ObservationState.Closed or ObservationState.Conflicting;

    /// <summary>Maps an observation state to its machine-readable event token.</summary>
    public static string TokenOf(ObservationState state) => state switch
    {
        ObservationState.Open => ObserveTokens.Open,
        ObservationState.Conflicting => ObserveTokens.Conflict,
        ObservationState.Closed => ObserveTokens.Closed,
        ObservationState.Merged => ObserveTokens.Merged,
        _ => ObserveTokens.Open,
    };

    private static ObservationState ClassifyOpen(OpenPr pr)
        => pr.State switch
        {
            PrLifecycle.Closed => ObservationState.Closed,
            _ when pr.Mergeable == Mergeability.Conflicting => ObservationState.Conflicting,
            _ => ObservationState.Open,
        };

    private static void Upsert(Dictionary<int, PrObservation> snapshot, PrObservation candidate)
    {
        if (!snapshot.TryGetValue(candidate.Number, out PrObservation existing)
            || Rank(candidate.State) > Rank(existing.State))
        {
            snapshot[candidate.Number] = candidate;
        }
    }

    private static int Rank(ObservationState state) => state switch
    {
        ObservationState.Merged => 3,
        ObservationState.Closed => 2,
        ObservationState.Conflicting => 2,
        _ => 1,
    };
}
