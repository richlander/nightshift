namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The retained credential-split wiring: <c>waiting</c> and <c>pr</c> reach GitHub through
/// <see cref="GhRunnerFactory"/>. Configured credentials authenticate as the GitHub App (spending its
/// separate rate-limit bucket); unconfigured falls back to ambient <c>gh</c>; configured-but-broken becomes
/// a visibly failed read rather than a crash or a silent ambient fallback. The App token provider is owned
/// by the session and disposed with it.
/// </summary>
public class GhRunnerFactoryTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";
    private const string ConfiguredPath = "/secure/creds.json";

    private sealed class RecordingGh
    {
        public int Calls { get; private set; }
        public IReadOnlyDictionary<string, string?>? LastEnvironment { get; private set; }
        public bool LastEnvironmentWasNull { get; private set; } = true;

        public Task<GhResult> RunAsync(
            IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string?>? environmentOverrides,
            CancellationToken ct)
        {
            Calls++;
            LastEnvironment = environmentOverrides;
            LastEnvironmentWasNull = environmentOverrides is null;
            return Task.FromResult(new GhResult(0, "{}", string.Empty));
        }
    }

    private sealed class FakeTokenProvider : IGitHubInstallationTokenProvider, IDisposable
    {
        private readonly Func<CancellationToken, Task<GitHubInstallationToken>> _getToken;

        public FakeTokenProvider(Func<CancellationToken, Task<GitHubInstallationToken>>? getToken = null)
            => _getToken = getToken ?? (_ => Task.FromResult(
                new GitHubInstallationToken("ghs_app_installation_token", DateTimeOffset.UtcNow.AddMinutes(30))));

        public int TokenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public Task<GitHubInstallationToken> GetTokenAsync(CancellationToken ct)
        {
            TokenCalls++;
            return _getToken(ct);
        }

        public void Dispose() => DisposeCalls++;
    }

    [Fact]
    public async Task Configured_InjectsInstallationTokenViaAuthenticatedRunner()
    {
        var gh = new RecordingGh();
        var provider = new FakeTokenProvider();

        using GhRunnerSession session = GhRunnerFactory.Create(
            name => name == CredentialsPathVariable ? ConfiguredPath : null,
            () => provider,
            gh.RunAsync);

        await session.Run(["api", "repos/o/r/pulls/1"], CancellationToken.None);

        Assert.Equal(1, provider.TokenCalls);
        Assert.NotNull(gh.LastEnvironment);
        Assert.True(gh.LastEnvironment!.TryGetValue("GH_TOKEN", out string? token));
        Assert.Equal("ghs_app_installation_token", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Unconfigured_FallsBackToAmbientGh(string? configuredPath)
    {
        var gh = new RecordingGh();

        using GhRunnerSession session = GhRunnerFactory.Create(
            _ => configuredPath,
            () => throw new Xunit.Sdk.XunitException("token provider must not be built when App credentials are unconfigured"),
            gh.RunAsync);

        await session.Run(["api", "user"], CancellationToken.None);

        // Ambient gh: no environment override, so gh uses the caller's own auth exactly as before.
        Assert.Equal(1, gh.Calls);
        Assert.True(gh.LastEnvironmentWasNull);
        Assert.Null(gh.LastEnvironment);
    }

    [Fact]
    public async Task ConfiguredButBrokenCredentials_ReturnsFailedResult_NeverThrows_NeverAmbient()
    {
        var gh = new RecordingGh();

        // Load() normalises missing/malformed/mis-permissioned credentials to InvalidOperationException.
        using GhRunnerSession session = GhRunnerFactory.Create(
            _ => ConfiguredPath,
            () => throw new InvalidOperationException("octoshift: credentials file '/x' does not exist."),
            gh.RunAsync);

        GhResult result = await session.Run(["api", "repos/o/r/pulls/1"], CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);                                    // a failed read -> unavailable path
        Assert.Contains("unusable", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, gh.Calls);                                             // never ambient: raw gh not invoked
    }

    [Fact]
    public async Task ConfiguredTokenMintFailure_ReturnsFailedResult()
    {
        var gh = new RecordingGh();
        var provider = new FakeTokenProvider(
            _ => throw new InvalidOperationException("octoshift: gh api installation token exchange failed (exit 1)"));

        using GhRunnerSession session = GhRunnerFactory.Create(_ => ConfiguredPath, () => provider, gh.RunAsync);

        GhResult result = await session.Run(["api", "repos/o/r/pulls/1"], CancellationToken.None);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("token exchange failed", result.Stderr, StringComparison.Ordinal);
        Assert.Equal(0, gh.Calls);                                             // token never minted -> gh never runs
    }

    [Fact]
    public async Task ConfiguredTokenMintCancellation_Propagates()
    {
        var gh = new RecordingGh();
        var provider = new FakeTokenProvider(ct =>
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new GitHubInstallationToken("unused", DateTimeOffset.UtcNow.AddMinutes(30)));
        });

        using GhRunnerSession session = GhRunnerFactory.Create(_ => ConfiguredPath, () => provider, gh.RunAsync);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await session.Run(["api", "x"], new CancellationToken(canceled: true)));
    }

    [Fact]
    public void Configured_DisposesTokenProviderWithTheSession()
    {
        var gh = new RecordingGh();
        var provider = new FakeTokenProvider();

        GhRunnerSession session = GhRunnerFactory.Create(_ => ConfiguredPath, () => provider, gh.RunAsync);
        Assert.Equal(0, provider.DisposeCalls);

        session.Dispose();

        Assert.Equal(1, provider.DisposeCalls);
    }

    [Fact]
    public void Unconfigured_DisposeIsANoOp()
    {
        var gh = new RecordingGh();

        GhRunnerSession session = GhRunnerFactory.Create(
            _ => null,
            () => throw new Xunit.Sdk.XunitException("token provider must not be built when unconfigured"),
            gh.RunAsync);

        session.Dispose();          // owns nothing; must not throw
    }
}
