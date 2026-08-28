namespace Octoshift.GitHub;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

/// <summary>A conditional-request cache: the ETag and body last seen for one API path.</summary>
internal interface IConditionalCache
{
    (string? ETag, string? Body) Get(string path);

    void Put(string path, string? etag, string body);
}

/// <summary>
/// Reads one PR's current state from GitHub — head sha, mergeability, and the check runs on that head.
/// </summary>
/// <remarks>
/// REST, deliberately. Measured on 2026-08-21 the GraphQL budget was exhausted (0/5000) while REST sat
/// nearly untouched (4988/5000), and <c>gh pr list --json</c> and <c>statusCheckRollup</c> are the GraphQL
/// half. <c>pulls/{n}</c> and <c>check-runs</c> are the REST half, and both carry an ETag — so a PR that
/// has not moved since the last look answers 304 and costs no budget at all. Re-checking a quiet PR is
/// therefore nearly free, which is what makes holding a wait on an agent's behalf cheaper than letting the
/// agent poll (issue #157).
/// </remarks>
internal sealed class GhPrFactsSource
{
    private readonly string _repo;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;
    private readonly IConditionalCache _cache;

    public GhPrFactsSource(
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

    /// <summary>The <c>owner/name</c> scope this source reads.</summary>
    public string Repo => _repo;

    /// <summary>Calls observed so far this run, and how many of those were free 304s.</summary>
    public int Calls { get; private set; }

    /// <summary>PRs whose mergeability needed a second, unconditional read before it was known.</summary>
    public int Recomputed { get; private set; }

    public int NotModified { get; private set; }

    /// <summary>Remaining REST budget from the last response's <c>X-RateLimit-Remaining</c>, or null.</summary>
    public int? RateLimitRemaining { get; private set; }

    /// <summary>True once a response reported the budget spent or GitHub pushed back.</summary>
    public bool RateLimited { get; private set; }

    /// <summary>
    /// Re-reads one PR unconditionally, for mergeability that was still being computed. Unconditional
    /// because the ETag can be unchanged while the computed field is not, so a conditional request would
    /// be answered 304 with the stale value.
    /// </summary>
    public async Task<PrFacts?> RefreshMergeabilityAsync(int prNumber, CancellationToken ct)
    {
        Read read = await GetAsync($"repos/{_repo}/pulls/{prNumber}", ct, bypassCache: true);
        PullDetailDto? pull = read is { Outcome: ReadOutcome.Ok, Body: { } refreshedBody }
            ? Deserialize(refreshedBody, GhPrFactsJsonContext.Default.PullDetailDto)
            : null;
        if (pull?.Head?.Sha is not { Length: > 0 } headSha)
        {
            return null;
        }

        Recomputed++;
        return new PrFacts
        {
            Number = pull.Number > 0 ? pull.Number : prNumber,
            Repo = _repo,
            HeadSha = headSha,
            State = pull.State ?? "open",
            Merged = pull.Merged ?? false,
            MergedAt = DateTimeOffset.TryParse(pull.MergedAt, out DateTimeOffset mergedAt) ? mergedAt : null,
            MergeableState = pull.MergeableState,
        };
    }

    /// <summary>
    /// Reads a PR's facts as one of three outcomes — <see cref="PrFetchStatus.Found"/> with the facts, an
    /// affirmative <see cref="PrFetchStatus.NotFound"/> on a 404, or <see cref="PrFetchStatus.Unavailable"/>
    /// when GitHub could not be read (auth, rate limit, transport, a 5xx, a nonzero exit) or answered a body
    /// that cannot be trusted as "no such PR". <c>pr</c> needs all three kept apart; the sweep does not, so
    /// it uses the <see cref="FetchAsync"/> shape below.
    /// </summary>
    public async Task<PrFetch> FetchDetailedAsync(int prNumber, CancellationToken ct)
    {
        Read pullRead = await GetAsync($"repos/{_repo}/pulls/{prNumber}", ct);
        if (pullRead.Outcome == ReadOutcome.NotFound)
        {
            return PrFetch.NotFound;
        }

        if (pullRead is not { Outcome: ReadOutcome.Ok, Body: { } pullBody })
        {
            return PrFetch.Unavailable;
        }

        PullDetailDto? pull = Deserialize(pullBody, GhPrFactsJsonContext.Default.PullDetailDto);
        if (pull?.Head?.Sha is not { Length: > 0 } headSha)
        {
            // A 200 we could not parse is a read we cannot trust — unavailable, never an affirmative
            // not-found. Reporting "no such PR" off a malformed body is the same lie as reporting it off an
            // outage.
            return PrFetch.Unavailable;
        }

        // Checks are keyed by sha, so this read stays valid until the branch actually moves — which is
        // what lets a rerun on an unchanged head be watched for the price of a 304.
        Read checksRead = await GetAsync($"repos/{_repo}/commits/{headSha}/check-runs?per_page=100", ct);
        string? checksBody = checksRead.Outcome == ReadOutcome.Ok ? checksRead.Body : null;
        CheckRunsDto? checks = checksBody is null ? null : Deserialize(checksBody, GhPrFactsJsonContext.Default.CheckRunsDto);

        // total_count above what one page returned means the rest were never seen, which is the same
        // problem as a failed read: the evidence is incomplete, so it must not read as "nothing failing".
        bool checksKnown = checks is not null
            && (checks.TotalCount is null or 0 || checks.TotalCount <= (checks.CheckRuns?.Length ?? 0));

        return PrFetch.Found(new PrFacts
        {
            ChecksKnown = checksKnown,
            Number = pull.Number > 0 ? pull.Number : prNumber,
            Repo = _repo,
            HeadSha = headSha,
            State = pull.State ?? "open",
            Merged = pull.Merged ?? false,
            MergedAt = DateTimeOffset.TryParse(pull.MergedAt, out DateTimeOffset mergedAt) ? mergedAt : null,
            Title = pull.Title,
            MergeableState = pull.MergeableState,
            Checks = PrFacts.LatestPerName((checks?.CheckRuns ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new CheckRunFact(
                    c.Name!,
                    c.Status ?? "queued",
                    c.Conclusion,
                    DateTimeOffset.TryParse(c.StartedAt, out DateTimeOffset started) ? started : null))),
        });
    }

    /// <summary>
    /// Reads a PR's facts, or null when GitHub had no such PR <em>or</em> could not be read — the shape the
    /// sweep uses, where a missing row and an unreadable one are handled the same. Callers that must tell a
    /// 404 from an outage (notably <c>pr</c>) use <see cref="FetchDetailedAsync"/>.
    /// </summary>
    public async Task<PrFacts?> FetchAsync(int prNumber, CancellationToken ct)
        => (await FetchDetailedAsync(prNumber, ct)).Facts;

    /// <summary>The classification of one conditional GET, kept apart so a 404 is never told from an outage.</summary>
    private enum ReadOutcome
    {
        /// <summary>A usable body (fresh, or a 304 served from cache).</summary>
        Ok,

        /// <summary>An affirmative 404: the resource does not exist.</summary>
        NotFound,

        /// <summary>The read failed or cannot be trusted — auth, rate limit, transport, 5xx, nonzero exit, empty body.</summary>
        Unavailable,
    }

    /// <summary>One GET reduced to its <see cref="ReadOutcome"/> and, when <see cref="ReadOutcome.Ok"/>, its body.</summary>
    private readonly record struct Read(ReadOutcome Outcome, string? Body)
    {
        public static readonly Read NotFound = new(ReadOutcome.NotFound, null);

        public static readonly Read Unavailable = new(ReadOutcome.Unavailable, null);

        public static Read Ok(string? body) => new(ReadOutcome.Ok, body);
    }

    /// <summary>
    /// One conditional GET, classified into the three outcomes a caller must keep apart: a usable body, an
    /// affirmative 404, or an unavailable read (auth, rate limit, transport, a 5xx, a nonzero gh exit, or a
    /// body that cannot be parsed). Collapsing 404 into "unavailable" — or the reverse — is what let an
    /// outage read as "no such PR".
    /// </summary>
    private async Task<Read> GetAsync(string path, CancellationToken ct, bool bypassCache = false)
    {
        // Once GitHub has pushed back, further calls cannot succeed and only deepen the hole for every
        // other agent drawing on the same budget.
        if (RateLimited)
        {
            return Read.Unavailable;
        }

        (string? etag, string? cached) = bypassCache ? (null, null) : _cache.Get(path);

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

        // `X-RateLimit-Remaining: 0` records EXHAUSTION, not a failure of THIS response. The request that
        // spends the last unit of the budget still returns a real answer — a fresh 200, a 304, or an
        // affirmative 404 — and that answer must be classified truthfully. So the exhaustion is remembered
        // (the guard at the top refuses the NEXT network read) and then classification proceeds; only
        // genuine pushback (403/429/5xx) turns the current read itself into Unavailable. Conflating the two
        // is what let a valid Found/NotFound at the moment the budget hit zero read as an outage.
        if (GhResponse.RateBudgetDepleted(headers))
        {
            RateLimited = true;
        }

        if (status == 304)
        {
            NotModified++;
            // A 304 says the cached body is still current; without a cached body there is nothing to serve,
            // which is an unavailable read, not an affirmative not-found.
            return cached is not null ? Read.Ok(cached) : Read.Unavailable;
        }

        if (status is 403 or 429 || status >= 500)
        {
            RateLimited = true;
            return Read.Unavailable;
        }

        // An affirmative 404 is the one negative answer to trust: GitHub looked and there is no such
        // resource. Checked before the generic-failure bucket (a 404 also carries a nonzero gh exit) so it
        // stays distinct from every read that merely could not be completed.
        if (status == 404)
        {
            return Read.NotFound;
        }

        if (gh.ExitCode != 0 || status is < 200 or >= 300 || string.IsNullOrWhiteSpace(body))
        {
            return Read.Unavailable;
        }

        _cache.Put(path, GhResponse.HeaderValue(headers, "etag"), body);
        return Read.Ok(body);
    }

    private static bool IsUnknownMergeability(string? mergeableState)
        => string.IsNullOrEmpty(mergeableState)
            || string.Equals(mergeableState, "unknown", StringComparison.OrdinalIgnoreCase);

    private static T? Deserialize<T>(string body, JsonTypeInfo<T> typeInfo)
        where T : class
    {
        try
        {
            return JsonSerializer.Deserialize(body, typeInfo);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// Resolves one PR number across the ordered set of repos the fleet touches, so a lookup is not silently
/// scoped to whichever repo the operator's current directory happens to sit in. Each repo has its own
/// <see cref="GhPrFactsSource"/> — hence its own repo-qualified ETag cache path — but all of them draw on
/// one shared <c>gh</c> credential and therefore <em>one</em> REST rate-limit budget; this aggregates the
/// per-repo call accounting and resolves one truthful outcome.
/// </summary>
/// <remarks>
/// A unique resolution has to be <em>proven</em>, not assumed: a single hit only means "this PR lives
/// here and nowhere else" when every other searched repo affirmatively answered 404. If any other repo
/// could not be read — an outage, a 5xx, or a scope left unsearched because the shared budget was already
/// spent — that repo may hold the same number, so one hit is reported <see cref="PrFetchStatus.Unavailable"/>
/// (with the found repo preserved for diagnosis) rather than a false unique <see cref="PrFetchStatus.Found"/>.
/// Two hits is a proven collision, reported <see cref="PrFetchStatus.Ambiguous"/> without picking either.
///
/// Because all repos share one budget, the moment any read observes exhaustion or pushback the fleet stops
/// reading the rest — further calls are doomed and only deepen the hole for every other agent — and the
/// unsearched scope counts as unavailable unless ambiguity is already proven. Subsequent PRs on an
/// exhausted budget make no calls at all. The resolved repo is remembered per PR so a follow-up
/// mergeability re-read is spent only where the PR lives.
/// </remarks>
internal sealed class GhFleetPrFactsSource
{
    private readonly IReadOnlyList<GhPrFactsSource> _sources;
    private readonly IReadOnlyList<string> _repos;
    private readonly Dictionary<int, GhPrFactsSource> _resolved = [];

    public GhFleetPrFactsSource(
        IReadOnlyList<string> repos,
        IConditionalCache cache,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        ArgumentNullException.ThrowIfNull(repos);
        if (repos.Count == 0)
        {
            throw new ArgumentException("at least one repo is required", nameof(repos));
        }

        _repos = repos;
        _sources = [.. repos.Select(repo => new GhPrFactsSource(repo, cache, runGhAsync))];
    }

    /// <summary>The repos this resolver searches, in scope order — the producer-owned label list.</summary>
    public IReadOnlyList<string> Repos => _repos;

    /// <summary>REST calls spent across every repo this run.</summary>
    public int Calls => _sources.Sum(s => s.Calls);

    /// <summary>Free 304s served across every repo this run.</summary>
    public int NotModified => _sources.Sum(s => s.NotModified);

    /// <summary>Mergeability re-reads across every repo this run.</summary>
    public int Recomputed => _sources.Sum(s => s.Recomputed);

    /// <summary>The remaining shared REST budget, from whichever repo read it most recently and lowest.</summary>
    public int? RateLimitRemaining => _sources.Select(s => s.RateLimitRemaining).Where(r => r is not null).Min();

    /// <summary>True once any repo reported the one shared budget spent or GitHub pushed back.</summary>
    public bool RateLimited => _sources.Any(s => s.RateLimited);

    /// <summary>
    /// Resolves a PR across the searched repos into one truthful outcome. Exactly one hit with every other
    /// repo affirmatively 404 and the whole scope searched is <see cref="PrFetchStatus.Found"/>; two hits is
    /// <see cref="PrFetchStatus.Ambiguous"/>; zero hits with the whole scope affirmatively 404 is
    /// <see cref="PrFetchStatus.NotFound"/>; anything else — a repo unread, or the scope cut short by an
    /// exhausted shared budget — is <see cref="PrFetchStatus.Unavailable"/>, since neither uniqueness nor
    /// absence can be proven against a repo that was not truthfully read. Every outcome carries the
    /// searched-repo labels so a report can say where it looked.
    /// </summary>
    public async Task<PrFetch> FetchDetailedAsync(int prNumber, CancellationToken ct)
    {
        var found = new List<GhPrFactsSource>();
        var facts = new List<PrFacts>();
        var attempted = new List<string>();
        bool anyUnavailable = false;

        // Search all repos, but stop the moment the shared budget is spent: with one credential behind
        // every source, a read after exhaustion cannot succeed and only deepens the hole. A scope left
        // unsearched this way is not "absent from it" — it is unread, and folds into anyUnavailable below.
        // Only the repos actually queried are recorded as attempted, so the report never claims to have
        // searched a repo the early exit skipped.
        bool searchedAll = true;
        for (int i = 0; i < _sources.Count; i++)
        {
            if (RateLimited)
            {
                searchedAll = false;
                break;
            }

            GhPrFactsSource source = _sources[i];
            attempted.Add(source.Repo);
            PrFetch read = await source.FetchDetailedAsync(prNumber, ct);
            switch (read.Status)
            {
                case PrFetchStatus.Found when read.Facts is { } hit:
                    found.Add(source);
                    facts.Add(hit);
                    break;
                case PrFetchStatus.Unavailable:
                    anyUnavailable = true;
                    break;
            }

            // A proven collision needs no further reads; the extra repos cannot make two hits fewer.
            if (found.Count > 1)
            {
                searchedAll = false;
                break;
            }
        }

        IReadOnlyList<string> foundIn = [.. found.Select(s => s.Repo)];

        if (found.Count > 1)
        {
            return new PrFetch(PrFetchStatus.Ambiguous, null).WithRepos(attempted, foundIn, _repos);
        }

        // Uniqueness and absence are only provable when the whole scope was read and every repo answered.
        // One hit beside an unread repo cannot claim to be the only one; zero hits beside an unread repo
        // cannot claim the PR does not exist.
        bool wholeScopeAnswered = searchedAll && !anyUnavailable;

        if (found.Count == 1)
        {
            if (wholeScopeAnswered)
            {
                _resolved[prNumber] = found[0];
                return PrFetch.Found(facts[0]).WithRepos(attempted, foundIn, _repos);
            }

            return PrFetch.Unavailable.WithRepos(attempted, foundIn, _repos);
        }

        return (wholeScopeAnswered ? PrFetch.NotFound : PrFetch.Unavailable).WithRepos(attempted, foundIn, _repos);
    }

    /// <summary>The sweep shape: the facts when exactly one repo resolved the PR, otherwise null — a 404,
    /// an outage and an ambiguous collision all collapse here, since none yields facts to act on.</summary>
    public async Task<PrFacts?> FetchAsync(int prNumber, CancellationToken ct)
        => (await FetchDetailedAsync(prNumber, ct)).Facts;

    /// <summary>
    /// Re-reads mergeability in the repo the PR already resolved to, so the second read is spent where the
    /// first found it and per-repo accounting stays honest. Falls back to the first repo that answers when
    /// the PR was not previously resolved here. Refuses outright once the one shared budget is spent — the
    /// resolved repo's own per-source flag may still read false while a <em>different</em> repo exhausted
    /// the credential, and spending another request against the shared budget is exactly what exhaustion
    /// forbids.
    /// </summary>
    public async Task<PrFacts?> RefreshMergeabilityAsync(int prNumber, CancellationToken ct)
    {
        if (RateLimited)
        {
            return null;
        }

        if (_resolved.TryGetValue(prNumber, out GhPrFactsSource? source))
        {
            return await source.RefreshMergeabilityAsync(prNumber, ct);
        }

        foreach (GhPrFactsSource candidate in _sources)
        {
            // Re-check between candidates: a candidate can return null while spending the last unit of the
            // shared budget (a 403/429, or a valid read reporting remaining=0), and calling the next one
            // would spend a request the exhaustion forbids. The entry guard only covers the first candidate.
            if (RateLimited)
            {
                return null;
            }

            if (await candidate.RefreshMergeabilityAsync(prNumber, ct) is { } refreshed)
            {
                return refreshed;
            }
        }

        return null;
    }
}

/// <summary>An ETag/body cache under the user's cache directory, one file per API path.</summary>
internal sealed class FileConditionalCache : IConditionalCache
{
    private readonly string _root;

    public FileConditionalCache(string? root = null)
        => _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "octoshift",
            "waiting");

    public (string? ETag, string? Body) Get(string path)
    {
        try
        {
            string file = FileFor(path);
            if (!File.Exists(file))
            {
                return (null, null);
            }

            CacheEntryDto? entry = JsonSerializer.Deserialize(File.ReadAllText(file), GhPrFactsJsonContext.Default.CacheEntryDto);
            return (entry?.ETag, entry?.Body);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // A cache miss is always safe: it costs one full response instead of a 304.
            return (null, null);
        }
    }

    public void Put(string path, string? etag, string body)
    {
        if (string.IsNullOrEmpty(etag))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(
                FileFor(path),
                JsonSerializer.Serialize(new CacheEntryDto { ETag = etag, Body = body }, GhPrFactsJsonContext.Default.CacheEntryDto));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private string FileFor(string path)
        => Path.Combine(_root, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..32] + ".json");
}

internal sealed record CacheEntryDto
{
    [JsonPropertyName("etag")]
    public string? ETag { get; init; }

    [JsonPropertyName("body")]
    public string? Body { get; init; }
}

internal sealed record PullDetailDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("merged")]
    public bool? Merged { get; init; }

    [JsonPropertyName("merged_at")]
    public string? MergedAt { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("mergeable_state")]
    public string? MergeableState { get; init; }

    [JsonPropertyName("head")]
    public PullHeadDto? Head { get; init; }
}

internal sealed record PullHeadDto
{
    [JsonPropertyName("sha")]
    public string? Sha { get; init; }
}

internal sealed record CheckRunsDto
{
    [JsonPropertyName("total_count")]
    public int? TotalCount { get; init; }

    [JsonPropertyName("check_runs")]
    public CheckRunDto[]? CheckRuns { get; init; }
}

internal sealed record CheckRunDto
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [JsonPropertyName("started_at")]
    public string? StartedAt { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PullDetailDto))]
[JsonSerializable(typeof(CheckRunsDto))]
[JsonSerializable(typeof(CacheEntryDto))]
internal partial class GhPrFactsJsonContext : JsonSerializerContext
{
}
