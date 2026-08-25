namespace Octoshift.GitHub;

/// <summary>One check run on a head sha, reduced to the fields a verdict turns on.</summary>
/// <param name="Name">The check's name. Not unique: a rerun adds another run under the same name.</param>
/// <param name="Status">queued, in_progress, or completed.</param>
/// <param name="Conclusion">Set once <paramref name="Status"/> is completed.</param>
/// <param name="StartedAt">
/// When the attempt began, used to pick the newest run per name. Without it a failed attempt that has
/// since been rerun green is still reported red — which is exactly the state agents sit waiting on.
/// </param>
internal sealed record CheckRunFact(string Name, string Status, string? Conclusion, DateTimeOffset? StartedAt = null)
{
    /// <summary>True once the run has reported a conclusion.</summary>
    public bool IsComplete => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the run completed in a state that blocks a merge.</summary>
    public bool IsFailure => IsComplete && Conclusion?.ToLowerInvariant() switch
    {
        "success" or "neutral" or "skipped" => false,
        null => false,
        _ => true,
    };
}

/// <summary>
/// What GitHub currently says about one PR — the half of the picture a stopped agent cannot see.
/// Sourced from REST (<c>pulls/{n}</c> and <c>check-runs</c>) rather than GraphQL: measured on
/// 2026-08-21 the GraphQL budget was exhausted while REST sat nearly untouched, and every REST response
/// carries an ETag so an unchanged PR re-reads for free (see issue #157).
/// </summary>
internal sealed record PrFacts
{
    public required int Number { get; init; }

    /// <summary>Full head sha as GitHub has it.</summary>
    public required string HeadSha { get; init; }

    /// <summary><c>open</c> or <c>closed</c>.</summary>
    public required string State { get; init; }

    public bool Merged { get; init; }

    /// <summary>When it merged, so a finished window can be reported as finished-for-how-long.</summary>
    public DateTimeOffset? MergedAt { get; init; }

    /// <summary>The PR title, for a lookup that has to be recognisable without opening a browser.</summary>
    public string? Title { get; init; }

    /// <summary>GitHub's <c>mergeable_state</c>: <c>clean</c>, <c>dirty</c>, <c>blocked</c>, <c>unstable</c>, <c>behind</c>, <c>unknown</c>.</summary>
    public string? MergeableState { get; init; }

    /// <summary>
    /// Check runs on <see cref="HeadSha"/>, already reduced to the newest attempt per name by
    /// <see cref="LatestPerName"/>.
    /// </summary>
    public IReadOnlyList<CheckRunFact> Checks { get; init; } = [];

    /// <summary>
    /// False when the check-runs read failed or was truncated. An unreadable set and a genuinely empty
    /// one look identical once collapsed to a list, and only one of them means "nothing is failing".
    /// </summary>
    public bool ChecksKnown { get; init; } = true;

    /// <summary>True when GitHub reports the branch cannot merge without integrating a later main.</summary>
    public bool IsConflicting => string.Equals(MergeableState, "dirty", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True only for states that affirmatively say the branch can merge. Everything else — <c>behind</c>,
    /// <c>blocked</c>, <c>draft</c>, <c>unknown</c>, and any value GitHub adds later — fails closed.
    /// <c>unstable</c> counts because it means failing or pending checks on an otherwise mergeable
    /// branch, and CI is not the bar here.
    /// </summary>
    public bool IsMergeable => MergeableState?.ToLowerInvariant() switch
    {
        "clean" or "has_hooks" or "unstable" => true,
        _ => false,
    };

    /// <summary>
    /// False while GitHub still answers <c>unknown</c>. Mergeability is computed lazily: the first read
    /// after a change starts the calculation and returns <c>unknown</c>, and the answer arrives on a later
    /// read. Treating <c>unknown</c> as "not conflicting" is how a conflicted PR reads as ready.
    /// </summary>
    public bool MergeabilityKnown
        => !string.IsNullOrEmpty(MergeableState)
            && !string.Equals(MergeableState, "unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Keeps only the newest attempt for each check name. A rerun leaves the earlier attempt in the API
    /// response, so without this a check that has since gone green still reports its old failure.
    /// </summary>
    public static IReadOnlyList<CheckRunFact> LatestPerName(IEnumerable<CheckRunFact> runs)
        => runs
            .GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.StartedAt ?? DateTimeOffset.MinValue).First())
            .ToArray();

    /// <summary>Finds a check run by name, case-insensitively.</summary>
    public CheckRunFact? FindCheck(string name)
        => Checks.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
