namespace Octoshift.GitHub;

/// <summary>
/// Snapshot of known order-PR branches that already have an OPEN or MERGED PR.
/// </summary>
internal readonly record struct ExistingOrderPrsSnapshot(bool Success, IReadOnlySet<string> OpenOrMergedHeadBranches);

/// <summary>
/// The injectable seam used by the outbound PR-open pass to ask GitHub which order branches already have
/// PRs, so opening can stay idempotent.
/// </summary>
internal interface IExistingOrderPrSource
{
    /// <summary>
    /// Fetches the set of order head branches that already have OPEN or MERGED PRs.
    /// <see cref="ExistingOrderPrsSnapshot.Success"/> false means the fetch was incomplete and the caller
    /// must fail closed (no outbound PR creation this poll).
    /// </summary>
    Task<ExistingOrderPrsSnapshot> FetchOpenOrMergedAsync(CancellationToken ct);
}
