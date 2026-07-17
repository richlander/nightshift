namespace Octoshift.GitHub;

/// <summary>GitHub's <c>mergeable</c> verdict for a PR, normalized to the three states octoshift routes on.</summary>
internal enum Mergeability
{
    /// <summary>GitHub has not computed mergeability yet (async, transient) — never treated as a conflict.</summary>
    Unknown,

    /// <summary>The PR merges cleanly — no rework needed on this axis.</summary>
    Mergeable,

    /// <summary>The PR conflicts with its base — the order must rebase onto main.</summary>
    Conflicting,
}

/// <summary>The lifecycle state octoshift routes on. <c>MERGED</c> PRs are the merge→land reconciler's job and are dropped here.</summary>
internal enum PrLifecycle
{
    /// <summary>Open and awaiting merge — the axis on which conflict/CI rework decisions are made.</summary>
    Open,

    /// <summary>Closed without merging — a deliberate human act routed by the §4.3 closed-unmerged policy.</summary>
    Closed,
}

/// <summary>
/// One entry of a PR's <c>statusCheckRollup</c>, normalized across the two shapes GitHub returns: a
/// <c>CheckRun</c> (carrying <see cref="Conclusion"/> + <see cref="Status"/>) and a <c>StatusContext</c>
/// (carrying <see cref="State"/>). The pure <see cref="Octoshift.Commands.ReworkDecision"/> owns the
/// failed/pending classification, so this record stays a faithful, un-interpreted copy of both shapes.
/// </summary>
internal readonly record struct PrCheck(string Name, string? Conclusion, string? State, string? Status, string? Link);

/// <summary>
/// One open (or closed-unmerged) pull request as octoshift sees it: the number, its head branch (the join
/// key back to an order via <see cref="Octoshift.Coordination.OrderRef.FromBranch"/>), its lifecycle, its
/// mergeability verdict, and its check rollup. The bounce decision is derived purely from this by
/// <see cref="Octoshift.Commands.ReworkDecision"/>, isolated from gh and the nightshift subprocess.
/// </summary>
internal readonly record struct OpenPr(
    int Number,
    string HeadBranch,
    PrLifecycle State,
    Mergeability Mergeable,
    IReadOnlyList<PrCheck> Checks);
