namespace Octoshift.GitHub;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>A GitHub installation access token and its absolute expiration instant.</summary>
internal readonly record struct GitHubInstallationToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Injectable provider for obtaining a valid GitHub installation token.
/// </summary>
internal interface IGitHubInstallationTokenProvider
{
    /// <summary>Returns a valid installation token, refreshing when absent or near expiry.</summary>
    Task<GitHubInstallationToken> GetTokenAsync(CancellationToken ct);
}

/// <summary>
/// Live installation-token provider backed by GitHub App credentials and <c>gh api</c> token exchange.
/// </summary>
internal sealed class GitHubAppInstallationTokenProvider : IGitHubInstallationTokenProvider, IDisposable
{
    private readonly GitHubAppCredentials _credentials;
    private readonly GitHubAppJwtFactory _jwtFactory;
    private readonly Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> _runGhAsync;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _refreshSkew;
    private readonly IGitHubTokenAuditSink _auditSink;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    // Disposal and the cached token are the mutable shared state; a plain lock guards both. It is only ever
    // held for a few synchronous field reads/writes (never across an await or the mint), so there is no torn
    // publication and no memory-model reasoning to get wrong. The refresh SemaphoreSlim, separately, still
    // serialises the token exchange itself.
    private readonly object _stateLock = new();
    private bool _disposed;
    private CachedToken? _cached;

    public GitHubAppInstallationTokenProvider(GitHubAppCredentials credentials)
        : this(
            credentials,
            new GitHubAppJwtFactory(),
            GhAuthenticatedRunner.RunGhAsync,
            () => DateTimeOffset.UtcNow,
            TimeSpan.FromMinutes(3),
            NullGitHubTokenAuditSink.Instance)
    {
    }

    internal GitHubAppInstallationTokenProvider(
        GitHubAppCredentials credentials,
        GitHubAppJwtFactory jwtFactory,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync,
        Func<DateTimeOffset> clock,
        TimeSpan refreshSkew,
        IGitHubTokenAuditSink auditSink)
    {
        if (refreshSkew < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(refreshSkew), "Refresh skew must be non-negative.");
        }

        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _jwtFactory = jwtFactory ?? throw new ArgumentNullException(nameof(jwtFactory));
        _runGhAsync = runGhAsync ?? throw new ArgumentNullException(nameof(runGhAsync));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _refreshSkew = refreshSkew;
        _auditSink = auditSink ?? throw new ArgumentNullException(nameof(auditSink));
    }

    public async Task<GitHubInstallationToken> GetTokenAsync(CancellationToken ct)
    {
        DateTimeOffset now = _clock();
        if (TryGetCached(now, out GitHubInstallationToken fastPath))
        {
            return fastPath;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = _clock();
            if (TryGetCached(now, out GitHubInstallationToken current))
            {
                return current;
            }

            bool refreshed = HasCached();

            // Mint outside the state lock (it must not be held across the await), while the refresh semaphore
            // keeps this the only exchange in flight.
            GitHubInstallationToken minted = await MintTokenAsync(refreshed, ct);

            lock (_stateLock)
            {
                // Cache only if disposal did not land during the mint. Either way this caller returns the
                // token it fetched; the cache simply is not repopulated past a Dispose.
                if (!_disposed)
                {
                    _cached = new CachedToken(minted);
                }
            }

            return minted;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>
    /// Returns a still-valid cached token under the state lock, throwing if the provider has been disposed.
    /// The disposed check lives here so it guards both the fast path and the post-semaphore recheck.
    /// </summary>
    private bool TryGetCached(DateTimeOffset now, out GitHubInstallationToken token)
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_cached is { } cached && !NeedsRefresh(cached.Token, now))
            {
                token = cached.Token;
                return true;
            }
        }

        token = default;
        return false;
    }

    private bool HasCached()
    {
        lock (_stateLock)
        {
            return _cached is not null;
        }
    }

    public void Dispose()
    {
        // Logical disposal under the state lock: mark disposed and drop the cached token so a live credential
        // is not retained in memory. The SemaphoreSlim is intentionally not disposed — it needs disposal only
        // to release the ManualResetEvent behind AvailableWaitHandle, which this type never touches — so an
        // in-flight refresh's finally-Release can never race a disposed handle.
        lock (_stateLock)
        {
            _disposed = true;
            _cached = null;
        }
    }

    private bool NeedsRefresh(GitHubInstallationToken token, DateTimeOffset now)
        => token.ExpiresAt <= now.Add(_refreshSkew);

    private async Task<GitHubInstallationToken> MintTokenAsync(bool refreshed, CancellationToken ct)
    {
        GitHubAppJwt jwt = _jwtFactory.CreateJwt(_credentials);

        var args = new List<string>
        {
            "api",
            $"/app/installations/{_credentials.InstallationId.ToString(CultureInfo.InvariantCulture)}/access_tokens",
            "--method",
            "POST",
        };

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GH_TOKEN"] = jwt.Token,
        };

        GhResult gh = await _runGhAsync(args, environment, ct);
        if (gh.ExitCode != 0)
        {
            string detail = gh.Stderr.Trim();
            throw new InvalidOperationException(
                $"octoshift: gh api installation token exchange failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        }

        GitHubInstallationTokenResponseDto? response;
        try
        {
            response = JsonSerializer.Deserialize(gh.Stdout, GitHubAppTokenJsonContext.Default.GitHubInstallationTokenResponseDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("octoshift: installation token response was not valid JSON.", ex);
        }

        if (response?.Token is not { Length: > 0 } token)
        {
            throw new InvalidOperationException("octoshift: installation token response did not include token.");
        }

        if (!DateTimeOffset.TryParse(
            response.ExpiresAt,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset expiresAt))
        {
            throw new InvalidOperationException("octoshift: installation token response did not include a valid expires_at.");
        }

        DateTimeOffset mintedAt = _clock();
        var minted = new GitHubInstallationToken(token, expiresAt);
        var auditRecord = new GitHubTokenAuditRecord(
            _credentials.Actor,
            refreshed ? GitHubTokenAuditKind.Refreshed : GitHubTokenAuditKind.Minted,
            mintedAt,
            expiresAt);
        await _auditSink.RecordTokenMintAsync(auditRecord, ct);

        return minted;
    }
}

/// <summary>
/// Immutable holder that lets the cached token be published as one atomic reference store. Its single field
/// is set before the holder is assigned to a volatile field, so a reader either sees the whole token or none.
/// </summary>
internal sealed record CachedToken(GitHubInstallationToken Token);

internal sealed record GitHubInstallationTokenResponseDto
{
    [JsonPropertyName("token")]
    public string? Token { get; init; }

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubInstallationTokenResponseDto))]
internal partial class GitHubAppTokenJsonContext : JsonSerializerContext
{
}
