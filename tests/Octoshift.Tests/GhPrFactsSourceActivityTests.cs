namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The GitHub-facing half of the stalled-agent join (#228): a claimed PR's newest commit and comment
/// times, and whether the tail of its commit history is just <c>main</c> being re-integrated.
/// </summary>
public class GhPrFactsSourceActivityTests
{
    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    private static string Commit(string date, int parentCount)
    {
        string parents = string.Join(',', Enumerable.Range(0, parentCount).Select(i => "{\"sha\":\"p" + i + "\"}"));
        return "{\"commit\":{\"committer\":{\"date\":\"" + date + "\"}},\"parents\":[" + parents + "]}";
    }

    [Fact]
    public async Task FetchActivityAsync_ReportsTheNewestCommitAndComment()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/10/commits?per_page=100"] = Response(
                200, $"[{Commit("2026-09-01T00:00:00Z", 1)},{Commit("2026-09-02T00:00:00Z", 1)}]"),
            ["repos/owner/repo/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(
                200, """[{"created_at":"2026-09-03T00:00:00Z"}]"""),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.Equal(DateTimeOffset.Parse("2026-09-02T00:00:00Z"), activity.LastCommitAt);
        Assert.Equal(DateTimeOffset.Parse("2026-09-03T00:00:00Z"), activity.LastCommentAt);
    }

    [Fact]
    public async Task FetchActivityAsync_ChasingMainWhenTheTailIsAllMergeCommits()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/10/commits?per_page=100"] = Response(
                200, $"[{Commit("2026-09-01T00:00:00Z", 2)}]"),
            ["repos/owner/repo/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(200, "[]"),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        // Only one commit in the tail and it is a merge (2 parents): chasing main is true.
        Assert.True(activity.ChasingMain);
        // No comments at all is not a failed read — it is "no comment activity known".
        Assert.Null(activity.LastCommentAt);
    }

    [Fact]
    public async Task FetchActivityAsync_NotChasingMainWhenARealCommitIsInTheTail()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/10/commits?per_page=100"] = Response(
                200, $"[{Commit("2026-09-01T00:00:00Z", 2)},{Commit("2026-09-02T00:00:00Z", 1)}]"),
            ["repos/owner/repo/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(200, "[]"),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.False(activity.ChasingMain);
    }

    [Fact]
    public async Task FetchActivityAsync_FollowsTheLinkHeaderAcrossCommitPages()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/10/commits?per_page=100"] = Response(
                200, $"[{Commit("2026-09-01T00:00:00Z", 1)}]",
                "link: <https://api.github.com/repositories/1/pulls/10/commits?per_page=100&page=2>; rel=\"next\""),
            ["https://api.github.com/repositories/1/pulls/10/commits?per_page=100&page=2"] = Response(
                200, $"[{Commit("2026-09-05T00:00:00Z", 1)}]"),
            ["repos/owner/repo/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(200, "[]"),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        // The newest commit is on the second page, so the tail computation must have followed the link
        // rather than stopping at whatever fit on the first page.
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T00:00:00Z"), activity.LastCommitAt);
    }

    [Fact]
    public async Task FetchActivityAsync_ReturnsNullWhenTheCommitsReadFails()
    {
        // A truncated commit read must never silently answer "chasing main" off partial evidence.
        var gh = new FakeGh
        {
            // commits path deliberately absent -> 404
            ["repos/owner/repo/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(200, "[]"),
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.Null(activity);
    }

    [Fact]
    public async Task FetchActivityAsync_AFailedCommentsReadIsNotFatal()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/10/commits?per_page=100"] = Response(200, $"[{Commit("2026-09-01T00:00:00Z", 1)}]"),
            // comments path deliberately absent -> 404
        };
        var source = new GhPrFactsSource("owner/repo", new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await source.FetchActivityAsync(10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.NotNull(activity.LastCommitAt);
        Assert.Null(activity.LastCommentAt);
    }

    [Fact]
    public async Task FleetFetchActivityInRepoAsync_ReadsFromExactlyTheNamedRepo()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo-a/pulls/10/commits?per_page=100"] = Response(200, $"[{Commit("2026-09-01T00:00:00Z", 1)}]"),
            ["repos/owner/repo-a/issues/10/comments?per_page=1&sort=created&direction=desc"] = Response(200, "[]"),
        };
        var facts = new GhFleetPrFactsSource(["owner/repo-a", "owner/repo-b"], new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await facts.FetchActivityInRepoAsync("owner/repo-a", 10, TestContext.Current.CancellationToken);

        Assert.NotNull(activity);
        Assert.DoesNotContain(gh.Requests, args => args.Any(a => a.Contains("repo-b", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task FleetFetchActivityInRepoAsync_ReturnsNullForARepoNotInScope()
    {
        var gh = new FakeGh();
        var facts = new GhFleetPrFactsSource(["owner/repo-a"], new FakeCache(), gh.RunAsync);

        PrActivityFacts? activity = await facts.FetchActivityInRepoAsync("owner/other", 10, TestContext.Current.CancellationToken);

        Assert.Null(activity);
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
