namespace Octoshift.GitHub;

/// <summary>
/// Optional outbound PR metadata applied at create time. The metadata source is intentionally injected: the
/// plan-metadata reader surface is a follow-up outside this order's file scope.
/// </summary>
internal readonly record struct PrOpenMetadata(
    IReadOnlyList<string> Labels,
    IReadOnlyList<string> Reviewers,
    string? Milestone)
{
    /// <summary>The empty/default outbound metadata shape.</summary>
    public static PrOpenMetadata Empty { get; } = new([], [], null);
}

/// <summary>
/// Injectable provider for outbound PR metadata (labels/reviewers/milestone).
/// </summary>
internal interface IPrOpenMetadataProvider
{
    /// <summary>Returns metadata for one order, or <see cref="PrOpenMetadata.Empty"/> when none is available.</summary>
    ValueTask<PrOpenMetadata> GetMetadataAsync(string orderBase, CancellationToken ct);
}

/// <summary>
/// Default no-op metadata provider until plan metadata is surfaced to Octoshift.
/// </summary>
internal sealed class NullPrOpenMetadataProvider : IPrOpenMetadataProvider
{
    public static NullPrOpenMetadataProvider Instance { get; } = new();

    private NullPrOpenMetadataProvider()
    {
    }

    public ValueTask<PrOpenMetadata> GetMetadataAsync(string orderBase, CancellationToken ct)
        => ValueTask.FromResult(PrOpenMetadata.Empty);
}
