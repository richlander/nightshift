namespace Octoshift.GitHub;

/// <summary>
/// One merged pull request as octoshift sees it: the PR number and its head branch (the join key that maps
/// back to an order via <see cref="Octoshift.Coordination.OrderRef.FromBranch"/>) plus the merge instant,
/// which feeds the cadence estimator.
/// </summary>
internal readonly record struct MergedPr(int Number, string HeadBranch, DateTimeOffset MergedAt);

/// <summary>
/// The result of one poll for merged PRs: the payload plus the transport signals the poller paces itself
/// with. <see cref="NotModified"/> is a conditional-request 304 (nothing changed, no rate cost); the header
/// fields let the controller honor GitHub's own back-pressure.
/// </summary>
internal readonly record struct MergedPrPage
{
    /// <summary>Merged nightshift PRs newer than the watermark, newest first. Empty on 304 or error.</summary>
    public IReadOnlyList<MergedPr> MergedPrs { get; init; }

    /// <summary>The response ETag to replay as <c>If-None-Match</c> on the next poll, or null.</summary>
    public string? ETag { get; init; }

    /// <summary>True when the conditional request returned 304 Not Modified (idle, no rate cost).</summary>
    public bool NotModified { get; init; }

    /// <summary>GitHub's <c>X-Poll-Interval</c> floor in seconds (0 when absent). Never poll below it.</summary>
    public int ProviderMinIntervalSeconds { get; init; }

    /// <summary>True on 403/429/5xx or a depleted rate budget — the signal to back off to the ceiling.</summary>
    public bool RateLimited { get; init; }

    /// <summary>Seconds until <c>X-RateLimit-Reset</c> (0 when absent); honored as a hard lower bound.</summary>
    public int RateLimitResetSeconds { get; init; }

    /// <summary>An empty, non-modified page — the shape of an idle 304, carrying the ETag forward.</summary>
    public static MergedPrPage NotModifiedWith(string? etag) => new()
    {
        MergedPrs = [],
        ETag = etag,
        NotModified = true,
    };
}
