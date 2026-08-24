namespace Octoshift.GitHub;

/// <summary>One check run on a head sha, reduced to the three fields a verdict turns on.</summary>
internal sealed record CheckRunFact(string Name, string Status, string? Conclusion)
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

    /// <summary>GitHub's <c>mergeable_state</c>: <c>clean</c>, <c>dirty</c>, <c>blocked</c>, <c>unstable</c>, <c>behind</c>, <c>unknown</c>.</summary>
    public string? MergeableState { get; init; }

    /// <summary>Check runs on <see cref="HeadSha"/>.</summary>
    public IReadOnlyList<CheckRunFact> Checks { get; init; } = [];

    /// <summary>True when GitHub reports the branch cannot merge without integrating a later main.</summary>
    public bool IsConflicting => string.Equals(MergeableState, "dirty", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// False while GitHub still answers <c>unknown</c>. Mergeability is computed lazily: the first read
    /// after a change starts the calculation and returns <c>unknown</c>, and the answer arrives on a later
    /// read. Treating <c>unknown</c> as "not conflicting" is how a conflicted PR reads as ready.
    /// </summary>
    public bool MergeabilityKnown
        => !string.IsNullOrEmpty(MergeableState)
            && !string.Equals(MergeableState, "unknown", StringComparison.OrdinalIgnoreCase);

    /// <summary>Finds a check run by name, case-insensitively.</summary>
    public CheckRunFact? FindCheck(string name)
        => Checks.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));
}
