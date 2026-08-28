namespace Octoshift.GitHub;

/// <summary>
/// A <c>gh</c> runner bound to a disposable lifetime, handed to the read-only membrane commands
/// (<c>waiting</c>, <c>pr</c>). A command holds the session for the whole span it reads GitHub facts and
/// disposes it on every exit path, so the GitHub App token provider's
/// <see cref="System.Threading.SemaphoreSlim"/> is always released. The ambient and disabled sessions own
/// nothing; disposing them is a no-op.
/// </summary>
internal sealed class GhRunnerSession : IDisposable
{
    // Any nonzero exit is enough: GhPrFactsSource maps `gh.ExitCode != 0` onto an unavailable read, which
    // is the existing path the commands already render (human token / one JSON error / ExitCode.Unavailable).
    private const int AuthFailureExitCode = 1;

    private readonly IDisposable? _owned;

    private GhRunnerSession(Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> run, IDisposable? owned)
    {
        Run = run;
        _owned = owned;
    }

    /// <summary>The runner delegate, shaped exactly like the one <see cref="GhPrFactsSource"/> consumes.</summary>
    public Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> Run { get; }

    public void Dispose() => _owned?.Dispose();

    /// <summary>Unconfigured: ambient <c>gh</c> with the caller's own auth. Owns nothing.</summary>
    internal static GhRunnerSession Ambient(
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync)
        => new((args, ct) => runGhAsync(args, null, ct), owned: null);

    /// <summary>
    /// Configured but unusable (credentials missing, malformed, or mis-permissioned): every call returns a
    /// failed <see cref="GhResult"/> so the read takes its existing unavailable path — never a silent
    /// ambient fallback and never a crash. Owns nothing.
    /// </summary>
    internal static GhRunnerSession Disabled(string reason)
        => new(
            (args, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult(new GhResult(AuthFailureExitCode, string.Empty, reason));
            },
            owned: null);

    /// <summary>
    /// Configured and loaded: authenticate as the App (installation token, spending the App's own
    /// rate-limit bucket). An expected token-mint failure becomes a failed <see cref="GhResult"/> — the same
    /// unavailable path — rather than throwing; a caller cancellation is not caught, so it propagates. Owns
    /// the token provider and disposes it.
    /// </summary>
    internal static GhRunnerSession App(
        IGitHubInstallationTokenProvider tokenProvider,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync)
    {
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> authenticated =
            GhAuthenticatedRunner.Create(tokenProvider, runGhAsync);

        async Task<GhResult> RunAuthenticatedAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            try
            {
                return await authenticated(args, ct);
            }
            catch (InvalidOperationException ex)
            {
                // MintTokenAsync normalises every expected token-exchange failure (a nonzero gh exit, a
                // malformed response, a missing token/expiry) to InvalidOperationException. Surface it as an
                // unavailable GitHub read, not a crash. This catch is scoped to that token-mint/config family
                // only: OperationCanceledException is not an InvalidOperationException, and the runner's
                // cancellation-cleanup failure is a GhProcessCleanupException (deliberately not an
                // InvalidOperationException), so both a caller cancellation and a cleanup failure propagate
                // untouched rather than being masked as an auth failure.
                return new GhResult(AuthFailureExitCode, string.Empty, ex.Message);
            }
        }

        return new GhRunnerSession(RunAuthenticatedAsync, tokenProvider as IDisposable);
    }
}

/// <summary>
/// Resolves the <c>gh</c> runner the read-only commands (<c>waiting</c>, <c>pr</c>) use, honoring the
/// credential-split policy: unconfigured =&gt; ambient <c>gh</c>; configured =&gt; authenticate as the GitHub
/// App so the daemon spends the App's separate rate-limit bucket; configured-but-broken =&gt; visibly
/// unavailable, never ambient.
/// </summary>
/// <remarks>
/// This is the retained wiring for the credential split that outlived the reconcile loop that first built
/// it (configured by the <c>OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH</c> environment variable). Construction is
/// non-throwing: an expected credential failure is turned into a disabled session so the calling command
/// runs its normal unavailable contract instead of crashing before its protected execution begins.
/// </remarks>
internal static class GhRunnerFactory
{
    /// <summary>The production session: App-authenticated when credentials are configured, ambient otherwise.</summary>
    public static GhRunnerSession Create()
        => Create(
            Environment.GetEnvironmentVariable,
            static () => new GitHubAppInstallationTokenProvider(new FileGitHubAppCredentialsSource().Load()),
            GhAuthenticatedRunner.RunGhAsync);

    /// <summary>Testable core: the credentials probe, the App token provider, and the raw <c>gh</c> runner are all injected.</summary>
    internal static GhRunnerSession Create(
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
            return GhRunnerSession.Ambient(runGhAsync);
        }

        // Configured: build the App token provider. FileGitHubAppCredentialsSource.Load normalises every
        // expected credential problem (missing, malformed, mis-permissioned, unreadable) to
        // InvalidOperationException. Translate that into a disabled runner rather than throwing here — this
        // runs outside the command's protected execution, so throwing would crash it with no JSON and no
        // token line. Do not fall back to ambient: the operator explicitly asked for App auth.
        IGitHubInstallationTokenProvider tokenProvider;
        try
        {
            tokenProvider = tokenProviderFactory();
        }
        catch (InvalidOperationException ex)
        {
            return GhRunnerSession.Disabled($"octoshift: GitHub App credentials are configured but unusable: {ex.Message}");
        }

        return GhRunnerSession.App(tokenProvider, runGhAsync);
    }
}
