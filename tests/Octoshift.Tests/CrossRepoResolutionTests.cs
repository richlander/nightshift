namespace Octoshift.Tests;

using Octoshift;
using Octoshift.Commands;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// Resolving a claimed PR against the repos the fleet actually touches rather than the one inferred from
/// the current directory (#178). The load-bearing facts: a hit in any searched repo resolves; every repo
/// answering 404 is an affirmative not-found distinct from an outage; a number that exists in two repos is
/// surfaced as ambiguous rather than awarded to an arbitrary repo; and every outcome names the repos it
/// searched, so a wrong-scope miss reads as "widen the scope" and not "GitHub is down".
/// </summary>
[Collection("ConsoleCapture")]
public class CrossRepoResolutionTests
{
    private const string HeadA = "aaaa11112222333344445555666677778888aaaa";
    private const string HeadB = "bbbb11112222333344445555666677778888bbbb";

    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    private static string Pull(int number, string head)
        => Response(200, "{\"number\":" + number + ",\"state\":\"open\",\"merged\":false,\"mergeable_state\":\"clean\",\"head\":{\"sha\":\"" + head + "\"}}");

    private static string Checks() => Response(200, """{"check_runs":[]}""");

    private static GhFleetPrFactsSource Fleet(FakeGh gh, params string[] repos)
        => new(repos, new FakeCache(), gh.RunAsync);

    // ---- resolver outcomes -------------------------------------------------

    [Fact]
    public async Task Fetch_ResolvesAPrLivingOnlyInTheSecondRepo()
    {
        // The exact shape of the bug: the operator stands in the first repo, but the PR is a second-repo
        // PR. Searching both repos resolves it, and the facts name where it landed.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Found, fetch.Status);
        Assert.NotNull(fetch.Facts);
        Assert.Equal("owner/second", fetch.Facts.Repo);
        Assert.Equal(["owner/first", "owner/second"], fetch.Searched);
        Assert.Equal(["owner/second"], fetch.FoundIn);
    }

    [Fact]
    public async Task Fetch_EverySearchedRepo404IsAnAffirmativeNotFound()
    {
        // Both repos affirmatively 404: no such PR anywhere the tool looked. This is a not-found, never an
        // outage — its remedy is to widen the scope, not to wait for GitHub to come back.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
            ["repos/owner/second/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.NotFound, fetch.Status);
        Assert.Null(fetch.Facts);
        Assert.Equal(["owner/first", "owner/second"], fetch.Searched);
        Assert.Empty(fetch.FoundIn);
    }

    [Fact]
    public async Task Fetch_OneUnavailableRepoAndOne404IsUnavailableNotNotFound()
    {
        // A repo that could not be read cannot prove absence, so even when the other repo affirmatively
        // 404s the whole resolution is unavailable — existence is unknown, never a false not-found.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(500, string.Empty),
            ["repos/owner/second/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Null(fetch.Facts);
        Assert.Equal(["owner/first", "owner/second"], fetch.Searched);
    }

    [Fact]
    public async Task Fetch_ADuplicateNumberInTwoReposIsAmbiguousNotAnArbitraryPick()
    {
        // The same number is a real PR in both repos. Awarding it to whichever repo is searched first would
        // be a silent lie, so it is surfaced as ambiguous with both repos named.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Pull(4623, HeadA),
            [$"repos/owner/first/commits/{HeadA}/check-runs?per_page=100"] = Checks(),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Ambiguous, fetch.Status);
        Assert.Null(fetch.Facts);
        Assert.Equal(["owner/first", "owner/second"], fetch.FoundIn);
    }

    [Fact]
    public async Task Fetch_BudgetIsAggregatedAcrossEveryRepoSource()
    {
        // Per-repo accounting is summed into one budget: the miss in the first repo and the hit in the
        // second both count, so the report's REST-call tally is truthful for the whole sweep.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        GhFleetPrFactsSource fleet = Fleet(gh, "owner/first", "owner/second");
        await fleet.FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        // first repo: one pulls 404; second repo: one pulls 200 + one check-runs 200 = 3 calls total.
        Assert.Equal(3, fleet.Calls);
    }

    // ---- scope resolution --------------------------------------------------

    [Fact]
    public void ResolveAll_ExplicitFlagsAreSearchedInOrderAndDeduplicated()
    {
        Assert.Equal(
            ["owner/one", "owner/two"],
            RepoScope.ResolveAll(["owner/one", "owner/two", "owner/one"]));
    }

    [Fact]
    public void ResolveAll_NoFlagsInfersASingleRepoFromTheRemote()
    {
        // With no --repo the scope is inferred from the current worktree's origin — a single repo, never a
        // set — preserving the single-repo default. Compared against the inference directly so the test is
        // deterministic whether or not a remote is present.
        string? inferred = RepoScope.Resolve(null);
        IReadOnlyList<string> expected = inferred is null ? [] : [inferred];

        Assert.Equal(expected, RepoScope.ResolveAll([]));
    }

    // ---- CLI parsing -------------------------------------------------------

    [Fact]
    public void CreateRootCommand_RepoOptionIsRepeatableOnBothVerbs()
    {
        var waiting = Cli.CreateRootCommand().Parse(["waiting", "--repo", "owner/one", "--repo", "owner/two"]);
        Assert.Empty(waiting.Errors);
        Assert.Equal(["owner/one", "owner/two"], waiting.GetValue<string[]>("--repo")!);

        var pr = Cli.CreateRootCommand().Parse(["pr", "4623", "--repo", "owner/one", "--repo", "owner/two"]);
        Assert.Empty(pr.Errors);
        Assert.Equal(["owner/one", "owner/two"], pr.GetValue<string[]>("--repo")!);
    }

    // ---- waiting rows across repos are covered in WaitingScanTests, whose default (non-ConsoleCapture)
    //      collection serializes the BuildRowsAsync tests that share WaitingCommand's static reporting
    //      fields; see WaitingScanTests.BuildRows_*AcrossRepos*.

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
