namespace Octoshift.Tests;

using System.Globalization;
using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Octoshift.Polling;
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
            Page(
                PrJson(3, "nightshift/2/op-c", T0.AddMinutes(30)),
                PrJson(2, "nightshift/2/op-b", T0.AddMinutes(20))),
            Page(PrJson(1, "nightshift/2/op-a", T0.AddMinutes(-1))),
        ]);
        var source = new GhMergedPrSource("owner/repo", perPage: 2, (args, _) =>
        {
            calls.Add(args);
            return Task.FromResult(pages.Dequeue());
        });

        MergedPrPage page = await source.FetchMergedAsync(T0, null, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { 3, 2 }, page.MergedPrs.Select(pr => pr.Number).ToArray());
        Assert.Equal(2, calls.Count);
        Assert.Contains("page=2", calls[1][1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task FetchMergedAsync_UpdatedSortedStaleMergeDoesNotStopPagination()
    {
        var pages = new Queue<GhResult>([
            Page(PrJson(1, "nightshift/2/op-old", T0.AddDays(-7))),
            Page(PrJson(2, "nightshift/2/op-b", T0.AddMinutes(20))),
            Page(),
        ]);
        var source = new GhMergedPrSource("owner/repo", perPage: 1, (args, _) =>
            Task.FromResult(pages.Dequeue()));

        var nightshift = new FakeNightshiftClient(BoardState.Empty);
        var state = new ReconcileCommand.ReconcileState { Since = T0, IntervalSeconds = 60 };

        await ReconcileCommand.PollOnceAsync(
            nightshift,
            source,
            state,
            new AdaptivePoller(PollingTuning.Default),
            PollingTuning.Default,
            TestContext.Current.CancellationToken);

        Assert.Equal([("/plan/2/order/op-b", "merged #2")], nightshift.Lands);
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

    [Fact]
    public async Task FetchMergedAsync_GhExitOneHttp304IsNotModified()
    {
        var source = new GhMergedPrSource("owner/repo", perPage: 10, (_, _) =>
            Task.FromResult(new GhResult(1, "HTTP/2.0 304 Not Modified\netag: abc\nx-poll-interval: 42\n\n", "gh: HTTP 304")));

        MergedPrPage page = await source.FetchMergedAsync(null, "old", TestContext.Current.CancellationToken);

        Assert.True(page.NotModified);
        Assert.False(page.RateLimited);
        Assert.Equal("abc", page.ETag);
        Assert.Equal(42, page.ProviderMinIntervalSeconds);
        Assert.Empty(page.MergedPrs);
    }

    private static GhResult Page(params string[] json)
        => new(0, $"HTTP/2.0 200 OK\netag: abc\n\n[{string.Join(',', json)}]", string.Empty);

    private static string PrJson(int number, string branch, DateTimeOffset mergedAt)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{{\"number\":{number},\"merged_at\":\"{mergedAt:O}\",\"head\":{{\"ref\":\"{branch}\"}}}}");
}
