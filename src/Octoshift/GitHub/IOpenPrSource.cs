namespace Octoshift.GitHub;

/// <summary>
/// The injectable seam between octoshift and GitHub for the <b>outbound</b> rework loop (design doc §4.2).
/// The reconcile controller only ever asks "which nightshift order-PRs are open (or closed-unmerged), and
/// what is each one's mergeability and check rollup?"; everything about how that is answered (the <c>gh</c>
/// CLI) lives behind this interface so the pure <see cref="Octoshift.Commands.ReworkDecision"/> can be
/// unit-tested against a fake with no network. The mirror of <see cref="IMergedPrSource"/>.
/// </summary>
internal interface IOpenPrSource
{
    /// <summary>
    /// Fetches the open (and closed-unmerged) nightshift order-PRs, each carrying its mergeability verdict
    /// and status-check rollup. Merged PRs are excluded — they belong to the merge→land reconciler.
    /// </summary>
    Task<IReadOnlyList<OpenPr>> FetchOpenAsync(CancellationToken ct);
}
