namespace Octoshift.Tests;

using System.Globalization;
using Octoshift.GitHub;
using Xunit;

/// <summary>Transport edge cases for the gh-backed merged-PR source: pagination, errors, and watermark equality.</summary>
public class GhMergedPrSourceTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-07-15T12:00:00Z");

    [Fact]
    public async Task FetchMergedAsync_PaginatesUntilItPassesWatermark()
    {
        var calls = new List<IReadOnlyList<string>>();
        var pages = new Queue<GhResult>([
            Page(PrJson(3, "nightshift/2/op-c", T0.AddMinutes(30))),
            Page(PrJson(2, "nightshift/2/op-b", T0.AddMinutes(20))),
            Page(PrJson(1, "nightshift/2/op-a", T0.AddMinutes(-1))),
        ]);
        var source = new GhMergedPrSource("owner/repo", perPage: 1, (args, _) =>
        {
            calls.Add(args);
            return Task.FromResult(pages.Dequeue());
        });

        MergedPrPage page = await source.FetchMergedAsync(T0, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 3, 2 }, page.MergedPrs.Select(pr => pr.Number).ToArray());
        Assert.Equal(3, calls.Count);
        Assert.Contains("page=2", calls[1][1], StringComparison.Ordinal);
    }

    [Fact]
    public void ParseMerged_IncludesPrAtSameSecondAsWatermark()
    {
        string body = $"[{PrJson(10, "nightshift/2/op-a", T0)},{PrJson(11, "nightshift/2/op-b", T0)}]";

        IReadOnlyList<MergedPr> merged = GhMergedPrSource.ParseMerged(body, T0);

        Assert.Equal(new[] { 10, 11 }, merged.Select(pr => pr.Number).Order().ToArray());
    }

    [Fact]
    public async Task FetchMergedAsync_NonZeroGhExitIsRateLimitedFailure()
    {
        var source = new GhMergedPrSource("owner/repo", perPage: 10, (_, _) =>
            Task.FromResult(new GhResult(1, string.Empty, "network down")));

        MergedPrPage page = await source.FetchMergedAsync(null, null, TestContext.Current.CancellationToken);

        Assert.True(page.RateLimited);
        Assert.Empty(page.MergedPrs);
        Assert.False(page.NotModified);
    }

    private static GhResult Page(string json)
        => new(0, $"HTTP/2.0 200 OK\netag: abc\n\n[{json}]", string.Empty);

    private static string PrJson(int number, string branch, DateTimeOffset mergedAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"number\":{number},\"merged_at\":\"{mergedAt:O}\",\"head\":{{\"ref\":\"{branch}\"}}}}");
}
