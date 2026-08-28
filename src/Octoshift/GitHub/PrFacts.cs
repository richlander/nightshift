namespace Octoshift.GitHub;

/// <summary>
/// The distinguishable outcomes of resolving one PR number across the repos the fleet touches. Kept apart
/// because collapsing them is exactly the defect: an affirmative <see cref="NotFound"/> (every searched
/// repo answered 404, so no such PR exists anywhere the tool looked) must never be confused with an
/// <see cref="Unavailable"/> read (auth, rate limit, transport, a 5xx, a nonzero <c>gh</c> exit, or a body
/// that cannot be parsed), where the PR's existence is simply unknown — nor with an
/// <see cref="Ambiguous"/> resolution, where the same number exists in more than one searched repo and no
/// single repo can be chosen without inventing one.
/// </summary>
internal enum PrFetchStatus
{
    /// <summary>Exactly one searched repo has the PR; the facts (carrying that repo) are attached.</summary>
    Found,

    /// <summary>Every searched repo answered 404: the PR affirmatively does not exist in any of them.</summary>
    NotFound,

    /// <summary>At least one searched repo could not be read, and none affirmatively found the PR.</summary>
    Unavailable,

    /// <summary>More than one searched repo has this PR number, so no single repo can be chosen truthfully.</summary>
    Ambiguous,
}

/// <summary>
/// One PR resolution reduced to its outcome, the facts when <see cref="PrFetchStatus.Found"/>, and the
/// producer-owned repo labels. These are kept apart so a report can be truthful about what it did versus
/// what it was asked to do: <see cref="Configured"/> is the full <c>--repo</c> (or inferred) scope;
/// <see cref="Searched"/> is the subset actually queried — which is smaller than <see cref="Configured"/>
/// when the search stopped early on a proven collision or an exhausted shared budget; and
/// <see cref="FoundIn"/> is, of the searched repos, those the PR was found in. The single-repo primitive
/// leaves all three empty; the fleet resolver fills them.
/// </summary>
internal readonly record struct PrFetch(
    PrFetchStatus Status,
    PrFacts? Facts,
    IReadOnlyList<string> Searched,
    IReadOnlyList<string> FoundIn,
    IReadOnlyList<string> Configured)
{
    public PrFetch(PrFetchStatus status, PrFacts? facts)
        : this(status, facts, [], [], [])
    {
    }

    public static readonly PrFetch NotFound = new(PrFetchStatus.NotFound, null);

    public static readonly PrFetch Unavailable = new(PrFetchStatus.Unavailable, null);

    public static PrFetch Found(PrFacts facts) => new(PrFetchStatus.Found, facts);

    /// <summary>The configured repos that were not actually queried — the search stopped before them.</summary>
    public IReadOnlyList<string> Unsearched
    {
        get
        {
            IReadOnlyList<string> searched = Searched;
            return [.. Configured.Where(repo => !searched.Contains(repo, StringComparer.OrdinalIgnoreCase))];
        }
    }

    /// <summary>
    /// Attaches the repo labels the fleet resolver owns, when every configured repo was searched: the
    /// searched set doubles as the configured scope. Used by the outcomes that reached the end of the
    /// scope (an affirmative not-found, a whole-scope hit).
    /// </summary>
    public PrFetch WithRepos(IReadOnlyList<string> searched, IReadOnlyList<string> foundIn)
        => this with { Searched = searched, FoundIn = foundIn, Configured = searched };

    /// <summary>
    /// Attaches the repo labels when the searched subset may be narrower than the configured scope — the
    /// search stopped early on a proven collision or an exhausted shared budget, so the report must not
    /// relabel unqueried repos as searched.
    /// </summary>
    public PrFetch WithRepos(IReadOnlyList<string> searched, IReadOnlyList<string> foundIn, IReadOnlyList<string> configured)
        => this with { Searched = searched, FoundIn = foundIn, Configured = configured };
}

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

    /// <summary>
    /// The <c>owner/name</c> repo these facts were read from. Stamped by the source that read them so a
    /// cross-repo report can name where a PR resolved; null when a single-repo caller did not set it.
    /// </summary>
    public string? Repo { get; init; }

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

    /// <summary>
    /// The one fail-closed head-alignment policy shared by <c>waiting</c> and <c>pr</c> so both report the
    /// same head and the same confidence after a mergeability re-read. <paramref name="current"/> is the
    /// complete facts from the initial detailed read; <paramref name="refreshed"/> is the second, mergeability
    /// re-read of the same PR (which may observe a moved head, and may itself still answer <c>unknown</c>
    /// mergeability — normal immediately after a push). Every non-null re-read is reconciled here; the caller
    /// does not pre-screen on <see cref="MergeabilityKnown"/>.
    ///
    /// When the head moved between the two reads, the newer head is adopted — continuing to report the older
    /// head would be a known-stale lie — together with the re-read's current-status fields
    /// (<see cref="MergeableState"/>, <see cref="State"/>, <see cref="Merged"/>, <see cref="MergedAt"/>). The
    /// check runs in hand were gathered on the <em>old</em> head, so they are dropped and their confidence is
    /// cleared (<see cref="Checks"/> empty, <see cref="ChecksKnown"/> = false) — a moved head must never stay
    /// actionable on the previous head's checks, even when the re-read's own mergeability is still unknown. The
    /// resolved repo and the head-independent PR title are carried across so a moved head does not blank them.
    ///
    /// When the head is unchanged, the complete facts are kept — including the title and the check runs, which
    /// remain valid on the same head — while the current-status fields that the re-read can legitimately have
    /// moved (mergeability, and an open→merged/closed transition with its merge time) are grafted from it.
    /// </summary>
    public static PrFacts AlignToRefreshedMergeability(PrFacts current, PrFacts refreshed)
        => string.Equals(current.HeadSha, refreshed.HeadSha, StringComparison.OrdinalIgnoreCase)
            ? current with
            {
                MergeableState = refreshed.MergeableState,
                State = refreshed.State,
                Merged = refreshed.Merged,
                MergedAt = refreshed.MergedAt,
            }
            : refreshed with
            {
                Checks = [],
                ChecksKnown = false,
                Title = current.Title,
                Repo = current.Repo,
            };
}
