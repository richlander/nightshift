namespace Octoshift.GitHub;

/// <summary>
/// The injectable seam between octoshift and GitHub. The reconcile loop only ever asks "which nightshift
/// branches merged since this watermark?"; everything about how that is answered (the <c>gh</c> CLI,
/// conditional requests, rate-limit headers) lives behind this interface so the land decision and the
/// pacing controller can be unit-tested against a fake with no network.
/// </summary>
internal interface IMergedPrSource
{
    /// <summary>
    /// Fetches merged nightshift PRs not older than <paramref name="since"/> (null = the initial sweep),
    /// replaying <paramref name="etag"/> as a conditional request so an unchanged result comes back as a
    /// cheap 304. The returned page also surfaces GitHub's poll-interval and rate-limit signals.
    /// </summary>
    Task<MergedPrPage> FetchMergedAsync(DateTimeOffset? since, string? etag, CancellationToken ct);
}
