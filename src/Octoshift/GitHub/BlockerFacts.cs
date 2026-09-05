namespace Octoshift.GitHub;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The distinguishable outcomes of reading one named blocker (#218).</summary>
internal enum BlockerFetchStatus
{
    /// <summary>The blocker was read; its open/closed state is attached.</summary>
    Found,

    /// <summary>GitHub affirmatively has no such issue or PR number in the repo searched.</summary>
    NotFound,

    /// <summary>The read failed or cannot be trusted — auth, rate limit, transport, a 5xx, a malformed body.</summary>
    Unavailable,
}

/// <summary>
/// What GitHub currently says about one named blocker — an issue or PR number a dependent published in
/// <c>blocked=</c>. Read from <c>issues/{n}</c> rather than <c>pulls/{n}</c> deliberately: GitHub
/// represents every PR as an issue too, and an issue's <c>closed</c> state is true both for a closed issue
/// and for a PR that merged or closed without merging — exactly the two transitions that release a
/// dependent (#218's "issue closes or a PR merges/closes"), so one cheap, ETag-cached read covers both
/// without a second call to <c>pulls/{n}</c> for the merge flag specifically.
/// </summary>
internal readonly record struct BlockerFacts(int Number, string? Repo, bool IsOpen, string? Title, DateTimeOffset? ClosedAt);

/// <summary>One blocker resolution reduced to its outcome and, when <see cref="BlockerFetchStatus.Found"/>, its facts.</summary>
internal readonly record struct BlockerFetch(BlockerFetchStatus Status, BlockerFacts? Facts)
{
    public static readonly BlockerFetch NotFound = new(BlockerFetchStatus.NotFound, null);

    public static readonly BlockerFetch Unavailable = new(BlockerFetchStatus.Unavailable, null);

    public static BlockerFetch Found(BlockerFacts facts) => new(BlockerFetchStatus.Found, facts);
}

/// <summary>
/// Reads one named blocker's open/closed state from one repo, ETag-cached under the same budget
/// discipline as PR facts (#157, #218): a blocker that has not changed since the last sweep answers 304
/// and costs nothing, which is what makes resolving every dependent's blocker every sweep cheap rather
/// than a second polling loop layered on top of the first.
/// </summary>
internal sealed class GhBlockerFactsSource
{
    private readonly string _repo;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;
    private readonly IConditionalCache _cache;

    public GhBlockerFactsSource(
        string repo,
        IConditionalCache cache,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(runGhAsync);

        _repo = repo;
        _cache = cache;
        _runGhAsync = runGhAsync;
    }

    /// <summary>The <c>owner/name</c> repo this source reads.</summary>
    public string Repo => _repo;

    /// <summary>Calls observed so far this run, and how many of those were free 304s.</summary>
    public int Calls { get; private set; }

    public int NotModified { get; private set; }

    /// <summary>Remaining REST budget from the last response's <c>X-RateLimit-Remaining</c>, or null.</summary>
    public int? RateLimitRemaining { get; private set; }

    /// <summary>True once a response reported the budget spent or GitHub pushed back.</summary>
    public bool RateLimited { get; private set; }

    /// <summary>
    /// Reads one blocker's state, or an affirmative not-found, or unavailable when GitHub could not be
    /// read — the same three-way split PR resolution uses, so a blocker that cannot be read is never
    /// silently treated as cleared.
    /// </summary>
    public async Task<BlockerFetch> FetchAsync(int number, CancellationToken ct)
    {
        // Once GitHub has pushed back, further calls cannot succeed and only deepen the hole for every
        // other agent drawing on the same budget.
        if (RateLimited)
        {
            return BlockerFetch.Unavailable;
        }

        string path = $"repos/{_repo}/issues/{number}";
        (string? etag, string? cached) = _cache.Get(path);

        var args = new List<string> { "api", path, "-i" };
        if (!string.IsNullOrEmpty(etag))
        {
            args.Add("-H");
            args.Add($"If-None-Match: {etag}");
        }

        Calls++;
        GhResult gh = await _runGhAsync(args, ct);
        (string headers, string body) = GhResponse.SplitHeadersAndBody(gh.Stdout);
        int status = GhResponse.StatusCode(headers, gh.Stderr);

        if (GhResponse.HeaderValue(headers, "x-ratelimit-remaining") is { } remaining
            && int.TryParse(remaining, out int left))
        {
            RateLimitRemaining = left;
        }

        if (GhResponse.RateBudgetDepleted(headers))
        {
            RateLimited = true;
        }

        string? effectiveBody;
        if (status == 304)
        {
            NotModified++;
            // A 304 says the cached body is still current; without a cached body there is nothing to
            // serve, which is an unavailable read, not an affirmative not-found.
            effectiveBody = cached;
        }
        else if (status is 403 or 429 || status >= 500)
        {
            RateLimited = true;
            return BlockerFetch.Unavailable;
        }
        else if (status == 404)
        {
            return BlockerFetch.NotFound;
        }
        else if (gh.ExitCode != 0 || status is < 200 or >= 300 || string.IsNullOrWhiteSpace(body))
        {
            return BlockerFetch.Unavailable;
        }
        else
        {
            _cache.Put(path, GhResponse.HeaderValue(headers, "etag"), body);
            effectiveBody = body;
        }

        if (effectiveBody is null)
        {
            return BlockerFetch.Unavailable;
        }

        IssueStateDto? issue;
        try
        {
            issue = JsonSerializer.Deserialize(effectiveBody, GhBlockerFactsJsonContext.Default.IssueStateDto);
        }
        catch (JsonException)
        {
            return BlockerFetch.Unavailable;
        }

        if (issue?.State is not { Length: > 0 } state)
        {
            // A 200 we could not parse is a read we cannot trust — unavailable, never an affirmative open
            // or closed, since either would be a guess dressed as an answer.
            return BlockerFetch.Unavailable;
        }

        bool isOpen = string.Equals(state, "open", StringComparison.OrdinalIgnoreCase);
        DateTimeOffset? closedAt = DateTimeOffset.TryParse(issue.ClosedAt, out DateTimeOffset at) ? at : null;
        return BlockerFetch.Found(new BlockerFacts(number, _repo, isOpen, issue.Title, closedAt));
    }
}

/// <summary>
/// Resolves a named blocker in the repo its dependent already resolved to (#218: "scope the lookup using
/// the dependent's resolved repository"). Unlike PR resolution, this does not search every fleet repo for
/// a collision — a dependent has already proven which repo it lives in, and a blocker it names is
/// overwhelmingly filed alongside it. One <see cref="GhBlockerFactsSource"/> per repo actually asked,
/// so the ETag cache stays repo-qualified and a blocker named by dependents in different repos is read
/// once per repo, not once per dependent.
/// </summary>
internal sealed class GhFleetBlockerFactsSource
{
    private readonly Dictionary<string, GhBlockerFactsSource> _byRepo = new(StringComparer.OrdinalIgnoreCase);
    private readonly IConditionalCache _cache;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;

    public GhFleetBlockerFactsSource(
        IConditionalCache cache,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(runGhAsync);

        _cache = cache;
        _runGhAsync = runGhAsync;
    }

    /// <summary>REST calls spent across every repo asked this run.</summary>
    public int Calls => _byRepo.Values.Sum(s => s.Calls);

    /// <summary>Free 304s served across every repo asked this run.</summary>
    public int NotModified => _byRepo.Values.Sum(s => s.NotModified);

    /// <summary>The remaining shared REST budget, from whichever repo read it most recently and lowest.</summary>
    public int? RateLimitRemaining => _byRepo.Values.Select(s => s.RateLimitRemaining).Where(r => r is not null).Min();

    /// <summary>True once any repo reported the one shared budget spent or GitHub pushed back.</summary>
    public bool RateLimited => _byRepo.Values.Any(s => s.RateLimited);

    /// <summary>Resolves one blocker number in the named repo, reusing that repo's cache across every dependent that names it.</summary>
    public Task<BlockerFetch> FetchAsync(string repo, int number, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repo);

        if (RateLimited)
        {
            return Task.FromResult(BlockerFetch.Unavailable);
        }

        if (!_byRepo.TryGetValue(repo, out GhBlockerFactsSource? source))
        {
            source = new GhBlockerFactsSource(repo, _cache, _runGhAsync);
            _byRepo[repo] = source;
        }

        return source.FetchAsync(number, ct);
    }
}

internal sealed record IssueStateDto
{
    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("closed_at")]
    public string? ClosedAt { get; init; }
}

[JsonSerializable(typeof(IssueStateDto))]
internal partial class GhBlockerFactsJsonContext : JsonSerializerContext
{
}
