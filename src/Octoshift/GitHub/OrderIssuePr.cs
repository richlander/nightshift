namespace Octoshift.GitHub;

/// <summary>The lifecycle state of an order PR as returned by GitHub.</summary>
internal enum OrderPrState
{
    Open,
    Closed,
    Merged,
}

/// <summary>
/// One nightshift order PR with the issue numbers GitHub says it closes (<c>closingIssuesReferences</c>).
/// The branch remains the order join key via <see cref="Octoshift.Coordination.OrderRef.FromBranch"/>.
/// </summary>
internal readonly record struct OrderIssuePr(
    int Number,
    string HeadBranch,
    OrderPrState State,
    IReadOnlyList<int> ClosingIssueNumbers);
