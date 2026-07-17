namespace Octoshift.GitHub;

/// <summary>
/// The injectable seam that returns nightshift order PRs plus GitHub's machine-readable issue bindings
/// (<c>closingIssuesReferences</c>) so octoshift can decide fan-out issue closing (§4.3) without linking any
/// GitHub SDK.
/// </summary>
internal interface IOrderIssuePrSource
{
    /// <summary>
    /// Fetches all nightshift order PRs (open, closed-unmerged, and merged) with their issue bindings.
    /// </summary>
    Task<IReadOnlyList<OrderIssuePr>> FetchOrderIssuePrsAsync(CancellationToken ct);
}
