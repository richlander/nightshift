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

    // Logical disposal, checked before the lock-free fast path and again after synchronization. A warmed
    // provider must not keep serving its cached token after Dispose, and a Dispose that races an in-flight
    // refresh must not turn the finally-Release into a throw. See Dispose for why the semaphore itself is not
    // physically disposed.
    private volatile bool _disposed;

    // A single reference published through a volatile field, rather than a Nullable<GitHubInstallationToken>
    // read outside the lock. The nullable struct is three fields (has-value, the token reference, the expiry)
    // whose stores are not atomic together, so the fast-path reader could observe a new token string beside a
    // stale expiry — a torn read. A CachedToken is immutable and fully constructed before it is published, and
    // a reference store is atomic; the volatile write/read pair orders that publication so the fast path only
    // ever sees a wholly initialized token or none at all.
    private volatile CachedToken? _cached;

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
        ObjectDisposedException.ThrowIf(_disposed, this);

        DateTimeOffset now = _clock();
        if (_cached is { } cached && !NeedsRefresh(cached.Token, now))
        {
            return cached.Token;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            // A Dispose may have landed while this caller waited for the lock; refuse to mint past it.
            ObjectDisposedException.ThrowIf(_disposed, this);

            now = _clock();
            if (_cached is { } current && !NeedsRefresh(current.Token, now))
            {
                return current.Token;
            }

            bool refreshed = _cached is not null;
            GitHubInstallationToken minted = await MintTokenAsync(refreshed, ct);
            _cached = new CachedToken(minted);

            // Publish, then re-check disposal. If Dispose landed while this refresh was in flight, its own
            // clear may have run before this publish — so clear again here. Between the two, whichever store
            // to _cached happens last leaves it null whenever _disposed is set, so no refresh can repopulate
            // the cache after disposal. The in-flight caller still returns the token it fetched.
            if (_disposed)
            {
                _cached = null;
            }

            return minted;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    /// <summary>Test-only view of whether a token is currently cached, so disposal-clearing is observable
    /// without reflection.</summary>
    internal bool HasCachedToken => _cached is not null;

    public void Dispose()
    {
        // Logical disposal only. The SemaphoreSlim is intentionally not disposed: it needs disposal solely to
        // release the ManualResetEvent behind AvailableWaitHandle, which this type never touches, so skipping
        // it leaks nothing. Not disposing it is what lets an in-flight refresh's finally-Release complete
        // without racing a disposed handle; the _disposed flag enforces disposal for every future entry.
        //
        // Order matters: mark disposed before clearing the cache so a concurrent refresh that observes the
        // flag after its own publish will clear too, and no interleaving leaves a token cached past Dispose.
        _disposed = true;
        _cached = null;
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
