namespace Octoshift.GitHub;

/// <summary>
/// Builds the <c>gh</c> runner that the read-only membrane commands (<c>waiting</c>, <c>pr</c>) use to
/// reach GitHub.
/// </summary>
/// <remarks>
/// This is the retained wiring for the credential split that outlived the reconcile loop that first built
/// it. When GitHub App credentials are configured — the
/// <c>OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH</c> environment variable points at a credentials file — the
/// runner injects an installation token so <c>gh</c> spends the App's <em>separate</em> rate-limit bucket,
/// keeping the daemon off the agents' user-PAT budget. When they are not configured, it falls back to
/// ambient <c>gh</c> (the caller's own auth), which is how local-dev has always run and what the commands
/// did unconditionally before this helper existed.
/// </remarks>
internal static class GhRunnerFactory
{
    /// <summary>
    /// The production runner: App-authenticated when credentials are configured, ambient <c>gh</c> otherwise.
    /// </summary>
    public static Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> Create()
        => Create(
            Environment.GetEnvironmentVariable,
            static () => new GitHubAppInstallationTokenProvider(new FileGitHubAppCredentialsSource().Load()),
            GhAuthenticatedRunner.RunGhAsync);

    /// <summary>Testable core: the credentials probe, the App token provider, and the raw <c>gh</c> runner are all injected.</summary>
    internal static Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> Create(
        Func<string, string?> getEnvironmentVariable,
        Func<IGitHubInstallationTokenProvider> tokenProviderFactory,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);
        ArgumentNullException.ThrowIfNull(tokenProviderFactory);
        ArgumentNullException.ThrowIfNull(runGhAsync);

        string? configuredPath = getEnvironmentVariable(FileGitHubAppCredentialsSource.CredentialsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            // Not configured: ambient gh, exactly as the commands ran before — no token, no App bucket.
            return (args, ct) => runGhAsync(args, null, ct);
        }

        // Configured: authenticate as the App so the daemon spends the App's own rate-limit bucket. A
        // credentials file that is present but malformed surfaces as an error here rather than silently
        // degrading to ambient auth — the operator asked for App auth, so a broken setup must be visible.
        IGitHubInstallationTokenProvider tokenProvider = tokenProviderFactory();
        return GhAuthenticatedRunner.Create(tokenProvider, runGhAsync);
    }
}
