namespace Octoshift.Tests;

using System.Threading;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The GitHub-facing half of #218: resolving one named blocker's open/closed state from
/// <c>issues/{n}</c>, ETag-cached the same way PR facts are, and never guessing "cleared" from a read
/// that did not actually succeed.
/// </summary>
public class GhBlockerFactsSourceTests
{
    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    [Fact]
    public async Task FetchAsync_AnOpenIssueReadsAsOpen()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(200, """{"state":"open","title":"Fix the thing"}"""),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.Found, fetch.Status);
        Assert.True(fetch.Facts!.Value.IsOpen);
        Assert.Equal("Fix the thing", fetch.Facts.Value.Title);
    }

    [Fact]
    public async Task FetchAsync_AClosedIssueReadsAsClosed()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(200, """{"state":"closed","closed_at":"2025-01-02T03:04:05Z"}"""),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.Found, fetch.Status);
        Assert.False(fetch.Facts!.Value.IsOpen);
        Assert.NotNull(fetch.Facts.Value.ClosedAt);
    }

    [Fact]
    public async Task FetchAsync_AMergedPrReadsAsClosedTooBecauseItIsAlsoAnIssue()
    {
        // A merged PR reports "closed" on the issues endpoint exactly like a closed issue — that identity
        // is why one call covers both of #218's release transitions.
        var gh = new FakeGh
        {
            ["repos/o/r/issues/4629"] = Response(200, """{"state":"closed"}"""),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(4629, TestContext.Current.CancellationToken);

        Assert.False(fetch.Facts!.Value.IsOpen);
    }

    [Fact]
    public async Task FetchAsync_A404IsAnAffirmativeNotFoundNotAnAvailabilityFailure()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/9999"] = Response(404, """{"message":"Not Found"}"""),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(9999, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.NotFound, fetch.Status);
        Assert.Null(fetch.Facts);
    }

    [Fact]
    public async Task FetchAsync_A403NeverReadsAsCleared()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(403, """{"message":"rate limited"}"""),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.Unavailable, fetch.Status);
    }

    [Fact]
    public async Task FetchAsync_A5xxNeverReadsAsCleared()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(502, "bad gateway"),
        };

        BlockerFetch fetch = await new GhBlockerFactsSource("o/r", new FakeCache(), gh.RunAsync)
            .FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.Unavailable, fetch.Status);
    }

    [Fact]
    public async Task FetchAsync_A304ServesTheCachedBody()
    {
        var cache = new FakeCache();
        cache.Put("repos/o/r/issues/5835", "\"stale-etag\"", """{"state":"open"}""");

        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(304, string.Empty),
        };

        var source = new GhBlockerFactsSource("o/r", cache, gh.RunAsync);
        BlockerFetch fetch = await source.FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(BlockerFetchStatus.Found, fetch.Status);
        Assert.True(fetch.Facts!.Value.IsOpen);
        Assert.Equal(1, source.NotModified);
    }

    [Fact]
    public async Task FetchAsync_ASecondReadOfTheSameNumberSendsTheCachedEtag()
    {
        var cache = new FakeCache();
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(200, """{"state":"open"}"""),
        };

        var source = new GhBlockerFactsSource("o/r", cache, gh.RunAsync);
        await source.FetchAsync(5835, TestContext.Current.CancellationToken);
        await source.FetchAsync(5835, TestContext.Current.CancellationToken);

        Assert.Equal(2, source.Calls);
        Assert.Contains(gh.Requests[1], a => a is "-H");
        Assert.Contains(gh.Requests[1], a => a.Contains("If-None-Match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FleetSource_TwoDependentsNamingTheSameRepoAndBlockerCostOneCall()
    {
        var gh = new FakeGh
        {
            ["repos/o/r/issues/5835"] = Response(200, """{"state":"open"}"""),
        };

        var fleet = new GhFleetBlockerFactsSource(new FakeCache(), gh.RunAsync);
        await fleet.FetchAsync("o/r", 5835, TestContext.Current.CancellationToken);
        await fleet.FetchAsync("o/r", 5835, TestContext.Current.CancellationToken);

        // The fleet source itself dedupes nothing by number — the caller's own (repo, number) dedup does
        // that (#218) — but it must reuse one repo-scoped source and cache rather than opening a second.
        Assert.Equal(2, gh.Requests.Count);
        Assert.Contains(gh.Requests[1], a => a.Contains("If-None-Match", StringComparison.Ordinal));
    }

    [Fact]
    public async Task FleetSource_TheSameNumberInDifferentReposIsReadSeparately()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo-a/issues/5835"] = Response(200, """{"state":"open"}"""),
            ["repos/owner/repo-b/issues/5835"] = Response(200, """{"state":"closed"}"""),
        };

        var fleet = new GhFleetBlockerFactsSource(new FakeCache(), gh.RunAsync);
        BlockerFetch a = await fleet.FetchAsync("owner/repo-a", 5835, TestContext.Current.CancellationToken);
        BlockerFetch b = await fleet.FetchAsync("owner/repo-b", 5835, TestContext.Current.CancellationToken);

        Assert.True(a.Facts!.Value.IsOpen);
        Assert.False(b.Facts!.Value.IsOpen);
    }

    /// <summary>A gh stand-in that answers by API path and records what it was asked.</summary>
    private sealed class FakeGh : Dictionary<string, string>
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<GhResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Requests.Add(args);
            string path = args.Count > 1 ? args[1] : string.Empty;
            return Task.FromResult(TryGetValue(path, out string? response)
                ? new GhResult(0, response, string.Empty)
                : new GhResult(1, string.Empty, "not found (HTTP 404)"));
        }
    }

    private sealed class FakeCache : IConditionalCache
    {
        private readonly Dictionary<string, (string? ETag, string Body)> _entries = [];

        public (string? ETag, string? Body) Get(string path)
            => _entries.TryGetValue(path, out (string? ETag, string Body) entry) ? (entry.ETag, entry.Body) : (null, null);

        public void Put(string path, string? etag, string body) => _entries[path] = (etag, body);
    }
}
