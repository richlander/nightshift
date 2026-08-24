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
        string? body = await GetAsync($"repos/{_repo}/pulls/{prNumber}", ct, bypassCache: true);
        PullDetailDto? pull = body is null ? null : Deserialize(body, GhPrFactsJsonContext.Default.PullDetailDto);
        if (pull?.Head?.Sha is not { Length: > 0 } headSha)
        {
            return null;
        }

        Recomputed++;
        return new PrFacts
        {
            Number = pull.Number > 0 ? pull.Number : prNumber,
            HeadSha = headSha,
            State = pull.State ?? "open",
            Merged = pull.Merged ?? false,
            MergeableState = pull.MergeableState,
        };
    }

    /// <summary>Reads a PR's facts, or null when GitHub could not be read.</summary>
    public async Task<PrFacts?> FetchAsync(int prNumber, CancellationToken ct)
    {
        string? pullBody = await GetAsync($"repos/{_repo}/pulls/{prNumber}", ct);
        if (pullBody is null)
        {
            return null;
        }

        PullDetailDto? pull = Deserialize(pullBody, GhPrFactsJsonContext.Default.PullDetailDto);
        if (pull?.Head?.Sha is not { Length: > 0 } headSha)
        {
            return null;
        }

        // Checks are keyed by sha, so this read stays valid until the branch actually moves — which is
        // what lets a rerun on an unchanged head be watched for the price of a 304.
        string? checksBody = await GetAsync($"repos/{_repo}/commits/{headSha}/check-runs?per_page=100", ct);
        CheckRunsDto? checks = checksBody is null ? null : Deserialize(checksBody, GhPrFactsJsonContext.Default.CheckRunsDto);

        // total_count above what one page returned means the rest were never seen, which is the same
        // problem as a failed read: the evidence is incomplete, so it must not read as "nothing failing".
        bool checksKnown = checks is not null
            && (checks.TotalCount is null or 0 || checks.TotalCount <= (checks.CheckRuns?.Length ?? 0));

        return new PrFacts
        {
            ChecksKnown = checksKnown,
            Number = pull.Number > 0 ? pull.Number : prNumber,
            HeadSha = headSha,
            State = pull.State ?? "open",
            Merged = pull.Merged ?? false,
            MergeableState = pull.MergeableState,
            Checks = PrFacts.LatestPerName((checks?.CheckRuns ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .Select(c => new CheckRunFact(
                    c.Name!,
                    c.Status ?? "queued",
                    c.Conclusion,
                    DateTimeOffset.TryParse(c.StartedAt, out DateTimeOffset started) ? started : null))),
        };
    }

    /// <summary>One conditional GET. Returns the body — from the response, or from cache on a 304.</summary>
    private async Task<string?> GetAsync(string path, CancellationToken ct, bool bypassCache = false)
    {
        // Once GitHub has pushed back, further calls cannot succeed and only deepen the hole for every
        // other agent drawing on the same budget.
        if (RateLimited)
        {
            return null;
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

        if (status == 304)
        {
            NotModified++;
            return cached;
        }

        if (status is 403 or 429 || status >= 500 || GhResponse.RateBudgetDepleted(headers))
        {
            RateLimited = true;
            return null;
        }

        if (gh.ExitCode != 0 || status is < 200 or >= 300 || string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        _cache.Put(path, GhResponse.HeaderValue(headers, "etag"), body);
        return body;
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
