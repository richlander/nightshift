namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The abandoned-PR scan's GitHub-facing half (#225): listing every open PR in a repo, paginated via the
/// response's own <c>Link</c> header rather than a fixed page count, and reading one PR's facts from
/// exactly the repo already known to hold it rather than paying a redundant cross-repo search.
/// </summary>
public class GhPrFactsSourceListTests
{
    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    [Fact]
    public async Task ListOpenPrNumbersAsync_ReturnsEveryNumberOnASinglePage()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] =
                Response(200, """[{"number":10},{"number":11}]"""),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        IReadOnlyList<int>? numbers = await source.ListOpenPrNumbersAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(numbers);
        Assert.Equal([10, 11], numbers);
    }

    [Fact]
    public async Task ListOpenPrNumbersAsync_FollowsTheLinkHeaderAcrossPages()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] = Response(
                200,
                """[{"number":10}]""",
                "link: <https://api.github.com/repositories/1/pulls?state=open&per_page=100&page=2>; rel=\"next\""),
            ["https://api.github.com/repositories/1/pulls?state=open&per_page=100&page=2"] =
                Response(200, """[{"number":11}]"""),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        IReadOnlyList<int>? numbers = await source.ListOpenPrNumbersAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(numbers);
        Assert.Equal([10, 11], numbers);
    }

    [Fact]
    public async Task ListOpenPrNumbersAsync_ReturnsNullRatherThanATruncatedListOnFailure()
    {
        // A page that cannot be read must never silently drop out of the result: a truncated open-PR list
        // reads as "nothing else is open", which is a claim the caller must not act on.
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] = Response(
                200,
                """[{"number":10}]""",
                "link: <https://api.github.com/repositories/1/pulls?state=open&per_page=100&page=2>; rel=\"next\""),
            // page 2 deliberately absent from the fake -> 404
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        IReadOnlyList<int>? numbers = await source.ListOpenPrNumbersAsync(TestContext.Current.CancellationToken);

        Assert.Null(numbers);
    }

    [Fact]
    public async Task FleetListOpenPrNumbersAsync_KeysByRepoAndOmitsAnUnreadableRepo()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo-a/pulls?state=open&per_page=100"] =
                Response(200, """[{"number":10}]"""),
            // repo-b deliberately absent from the fake -> 404, i.e. unreadable
        };
        var facts = new GhFleetPrFactsSource(["owner/repo-a", "owner/repo-b"], new FakeCache(), gh.RunAsync);

        IReadOnlyDictionary<string, IReadOnlyList<int>> byRepo =
            await facts.ListOpenPrNumbersAsync(TestContext.Current.CancellationToken);

        Assert.Equal([10], byRepo["owner/repo-a"]);
        Assert.False(byRepo.ContainsKey("owner/repo-b"));
    }

    [Fact]
    public async Task FetchInRepoAsync_ReadsFromExactlyTheNamedRepo()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo-a/pulls/10"] = Response(
                200, """{"number":10,"state":"open","mergeable_state":"clean","head":{"sha":"a1b2c3d"}}"""),
            ["repos/owner/repo-a/commits/a1b2c3d/check-runs?per_page=100"] =
                Response(200, """{"total_count":0,"check_runs":[]}"""),
        };
        var facts = new GhFleetPrFactsSource(["owner/repo-a", "owner/repo-b"], new FakeCache(), gh.RunAsync);

        PrFacts? found = await facts.FetchInRepoAsync("owner/repo-a", 10, TestContext.Current.CancellationToken);

        Assert.NotNull(found);
        Assert.Equal("clean", found.MergeableState);
        // Only repo-a's source was ever asked; a cross-repo search would have also queried repo-b.
        Assert.DoesNotContain(gh.Requests, args => args.Any(a => a.Contains("repo-b", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FetchInRepoAsync_ReturnsNullForARepoNotInScope()
    {
        var gh = new FakeGh();
        var facts = new GhFleetPrFactsSource(["owner/repo-a"], new FakeCache(), gh.RunAsync);

        PrFacts? found = await facts.FetchInRepoAsync("owner/other", 10, TestContext.Current.CancellationToken);

        Assert.Null(found);
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
