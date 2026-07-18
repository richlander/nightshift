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

    private GitHubInstallationToken? _cached;

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
        if (_cached is { } cached && !NeedsRefresh(cached, now))
        {
            return cached;
        }

        await _refreshLock.WaitAsync(ct);
        try
        {
            now = _clock();
            if (_cached is { } current && !NeedsRefresh(current, now))
            {
                return current;
            }

            bool refreshed = _cached is not null;
            GitHubInstallationToken minted = await MintTokenAsync(refreshed, ct);
            _cached = minted;
            return minted;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _refreshLock.Dispose();
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
