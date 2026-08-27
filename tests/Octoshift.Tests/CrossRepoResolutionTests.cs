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

    [Fact]
    public async Task Fetch_OneHitBesideAnUnreadableRepoIsUnavailableNotAFalseUnique()
    {
        // #178 round 1 / item 1: one repo has the PR, another could not be read (a 200 whose body cannot be
        // parsed) — that unread repo may hold the same number, so a single hit cannot claim to be unique.
        // The outcome is unavailable, with the found repo preserved for diagnosis but no facts to act on.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Pull(4623, HeadA),
            [$"repos/owner/first/commits/{HeadA}/check-runs?per_page=100"] = Checks(),
            ["repos/owner/second/pulls/4623"] = Response(200, "{ this is not json"),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Null(fetch.Facts);
        Assert.Equal(["owner/first"], fetch.FoundIn);
        Assert.Equal(["owner/first", "owner/second"], fetch.Searched);
    }

    [Fact]
    public async Task Fetch_AHitBesideAServerErrorRepoIsUnavailableNotAFalseUnique()
    {
        // The same rule for a 5xx: a repo behind a server error cannot be shown not to hold the number, so
        // a hit in another repo is unavailable rather than a false unique. The 5xx is pushback, so the
        // unread repo is not searched — but even had it been, one hit could not prove uniqueness.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(500, string.Empty),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        PrFetch fetch = await Fleet(gh, "owner/first", "owner/second")
            .FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Null(fetch.Facts);
    }

    // ---- one shared budget: stop on exhaustion, spend nothing after ---------

    [Fact]
    public async Task Fetch_ExhaustionMidScopeStopsFurtherReadsAndIsUnavailable()
    {
        // #178 item 4: all repos share one credential and one budget. A valid 200 that spends the last unit
        // (X-RateLimit-Remaining: 0) still answers, but the fleet must not read the next repo — that call is
        // doomed — so the scope is cut short and one hit beside an unread repo is unavailable, not a unique.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(200,
                "{\"number\":4623,\"state\":\"open\",\"mergeable_state\":\"clean\",\"head\":{\"sha\":\"" + HeadA + "\"}}",
                "x-ratelimit-remaining: 0"),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        GhFleetPrFactsSource fleet = Fleet(gh, "owner/first", "owner/second");
        PrFetch fetch = await fleet.FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Equal(["owner/first"], fetch.FoundIn);

        // The first repo spent exactly one call (its check-runs read is refused once the budget is zero);
        // the second repo was never touched.
        Assert.Single(gh.Requests);
        Assert.All(gh.Requests, args => Assert.Contains("repos/owner/first/pulls/4623", args));

        // A subsequent PR on the exhausted shared budget makes no calls at all.
        int spent = gh.Requests.Count;
        PrFetch again = await fleet.FetchDetailedAsync(4999, TestContext.Current.CancellationToken);
        Assert.Equal(PrFetchStatus.Unavailable, again.Status);
        Assert.Equal(spent, gh.Requests.Count);
    }

    [Fact]
    public async Task Fetch_PushbackOnTheFirstRepoStopsBeforeTheSecond()
    {
        // A 403/5xx is pushback on the one shared budget: the second repo is not read, and the outcome is
        // unavailable — the scope was never fully searched, so absence cannot be claimed either.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(403, string.Empty, "x-ratelimit-remaining: 0"),
            ["repos/owner/second/pulls/4623"] = Pull(4623, HeadB),
            [$"repos/owner/second/commits/{HeadB}/check-runs?per_page=100"] = Checks(),
        };

        GhFleetPrFactsSource fleet = Fleet(gh, "owner/first", "owner/second");
        PrFetch fetch = await fleet.FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        Assert.Equal(PrFetchStatus.Unavailable, fetch.Status);
        Assert.Single(gh.Requests);
    }

    [Fact]
    public async Task Refresh_MakesNoCallOnceAnotherRepoExhaustedTheSharedBudget()
    {
        // #178 round 2: the PR resolves uniquely to the first repo with mergeability still unknown, but the
        // second repo's affirmative 404 spent the last unit of the one shared credential budget. A
        // second-pass mergeability re-read must not spend another request against the first repo just
        // because that repo's own per-source flag never saw the exhaustion — the budget is shared, so the
        // fleet refuses outright and makes zero additional calls.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(200,
                "{\"number\":4623,\"state\":\"open\",\"mergeable_state\":\"unknown\",\"head\":{\"sha\":\"" + HeadA + "\"}}"),
            [$"repos/owner/first/commits/{HeadA}/check-runs?per_page=100"] = Checks(),
            ["repos/owner/second/pulls/4623"] = Response(404, """{"message":"Not Found"}""", "x-ratelimit-remaining: 0"),
        };

        GhFleetPrFactsSource fleet = Fleet(gh, "owner/first", "owner/second");
        PrFetch fetch = await fleet.FetchDetailedAsync(4623, TestContext.Current.CancellationToken);

        // A proven unique resolution (the other repo affirmatively 404'd) whose mergeability is still
        // unknown — exactly the shape the second pass exists to refresh — with the budget now spent.
        Assert.Equal(PrFetchStatus.Found, fetch.Status);
        Assert.Equal("owner/first", fetch.Facts!.Repo);
        Assert.False(fetch.Facts.MergeabilityKnown);
        Assert.True(fleet.RateLimited);

        int spent = gh.Requests.Count;
        PrFacts? refreshed = await fleet.RefreshMergeabilityAsync(4623, TestContext.Current.CancellationToken);

        Assert.Null(refreshed);
        Assert.Equal(spent, gh.Requests.Count);
    }

    [Fact]
    public async Task Refresh_MakesNoCallAfterALaterPrExhaustsTheSharedBudget()
    {
        // The same guard across PRs: one PR resolves cleanly, then a later PR's read exhausts the shared
        // budget. A refresh of the earlier PR must still spend nothing, since the credential is spent
        // whichever PR emptied it.
        var gh = new FakeGh
        {
            ["repos/owner/first/pulls/4623"] = Response(200,
                "{\"number\":4623,\"state\":\"open\",\"mergeable_state\":\"unknown\",\"head\":{\"sha\":\"" + HeadA + "\"}}"),
            [$"repos/owner/first/commits/{HeadA}/check-runs?per_page=100"] = Checks(),
            ["repos/owner/second/pulls/4623"] = Response(404, """{"message":"Not Found"}"""),
            ["repos/owner/first/pulls/4999"] = Response(404, """{"message":"Not Found"}""", "x-ratelimit-remaining: 0"),
        };

        GhFleetPrFactsSource fleet = Fleet(gh, "owner/first", "owner/second");
        await fleet.FetchDetailedAsync(4623, TestContext.Current.CancellationToken);
        await fleet.FetchDetailedAsync(4999, TestContext.Current.CancellationToken);

        Assert.True(fleet.RateLimited);

        int spent = gh.Requests.Count;
        Assert.Null(await fleet.RefreshMergeabilityAsync(4623, TestContext.Current.CancellationToken));
        Assert.Equal(spent, gh.Requests.Count);
    }

    // ---- scope resolution --------------------------------------------------

    [Fact]
    public void Resolve_ExplicitFlagsAreSearchedInOrderAndDeduplicated()
    {
        RepoScope.Resolution resolution = RepoScope.Resolve(["owner/one", "owner/two", "owner/one"]);

        Assert.Null(resolution.Error);
        Assert.Equal(["owner/one", "owner/two"], resolution.Repos);
    }

    [Fact]
    public void Resolve_NoFlagsInfersASingleRepoFromTheRemote()
    {
        // With no --repo the scope is inferred from the current worktree's origin — a single repo, never a
        // set — preserving the single-repo default. Compared against the inference directly so the test is
        // deterministic whether or not a remote is present.
        string? inferred = RepoScope.Resolve((string?)null);
        IReadOnlyList<string> expected = inferred is null ? [] : [inferred];

        RepoScope.Resolution resolution = RepoScope.Resolve([]);
        Assert.Null(resolution.Error);
        Assert.Equal(expected, resolution.Repos);
    }

    [Fact]
    public void Resolve_AMalformedExplicitFlagFailsTheWholeInvocation()
    {
        // #178 item 2: a valid flag beside a malformed one must not silently narrow the scope to the valid
        // one — that could turn a real collision into a false unique. The whole resolution fails with a
        // usage error and no repos, so the caller neither infers nor proceeds.
        RepoScope.Resolution resolution = RepoScope.Resolve(["owner/one", "not a repo"]);

        Assert.NotNull(resolution.Error);
        Assert.Empty(resolution.Repos);
    }

    [Theory]
    [InlineData("owner")]              // no name segment
    [InlineData("owner/name/extra")]   // too many segments
    [InlineData("ow ner/name")]        // whitespace
    [InlineData("owner/na me")]
    [InlineData("owner/..")]           // relative path name
    [InlineData("owner/na?me")]        // query metacharacter
    [InlineData("owner/na#me")]
    [InlineData("owner/na%2fme")]      // encoded slash
    [InlineData("owner/na\u0000me")]   // control character
    public void Validate_RejectsAnythingThatCannotFormASafeOwnerName(string value)
        => Assert.NotNull(RepoScope.Validate(value));

    [Theory]
    [InlineData("owner/name")]
    [InlineData("Owner-1/repo.name_2")]
    [InlineData("owner/name.git")]     // the .git suffix is stripped, not rejected
    public void Validate_AcceptsAWellFormedOwnerName(string value)
        => Assert.Null(RepoScope.Validate(value));

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

    [Fact]
    public void CreateRootCommand_RejectsAMalformedRepoValue()
    {
        // A malformed --repo is a usage error at the parser, the same as an option-shaped --host — even
        // when a valid one accompanies it.
        var waiting = Cli.CreateRootCommand().Parse(["waiting", "--repo", "not a repo"]);
        Assert.NotEmpty(waiting.Errors);
        Assert.Contains(waiting.Errors, e => e.Message.Contains("--repo", StringComparison.Ordinal));

        var pr = Cli.CreateRootCommand().Parse(["pr", "4623", "--repo", "owner/one", "--repo", "bad slug"]);
        Assert.NotEmpty(pr.Errors);
    }

    [Fact]
    public async Task RunAsync_AMalformedRepoIsAUsageErrorAndNeverReadsGitHub()
    {
        // Defence in depth at the command layer: even reached directly, a malformed --repo fails Usage
        // before any fleet source or GitHub read is constructed.
        Assert.Equal(ExitCode.Usage, await WaitingCommand.RunAsync(["not a repo"], [], all: false, json: false, TestContext.Current.CancellationToken));
        Assert.Equal(ExitCode.Usage, await PrCommand.RunAsync(4623, ["owner/one", "bad slug"], [], json: false, TestContext.Current.CancellationToken));
    }

    // ---- waiting rows across repos are covered in WaitingScanTests, whose non-parallel ConsoleCapture
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
