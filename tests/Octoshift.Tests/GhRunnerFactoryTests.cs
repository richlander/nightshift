namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// The retained credential-split wiring: <c>waiting</c> and <c>pr</c> reach GitHub through
/// <see cref="GhRunnerFactory"/>, which authenticates as the GitHub App when credentials are configured
/// (spending the App's separate rate-limit bucket) and falls back to ambient <c>gh</c> otherwise.
/// </summary>
public class GhRunnerFactoryTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";

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

    private sealed class FakeTokenProvider : IGitHubInstallationTokenProvider
    {
        public int Calls { get; private set; }

        public Task<GitHubInstallationToken> GetTokenAsync(CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new GitHubInstallationToken("ghs_app_installation_token", DateTimeOffset.UtcNow.AddMinutes(30)));
        }
    }

    [Fact]
    public async Task Create_WhenAppCredentialsConfigured_InjectsInstallationTokenViaAuthenticatedRunner()
    {
        var gh = new RecordingGh();
        var tokenProvider = new FakeTokenProvider();

        var runner = GhRunnerFactory.Create(
            name => name == CredentialsPathVariable ? "/secure/creds.json" : null,
            () => tokenProvider,
            gh.RunAsync);

        await runner(["api", "repos/o/r/pulls/1"], CancellationToken.None);

        // The authenticated path mints an App installation token and injects it as GH_TOKEN, so gh spends
        // the App's own rate-limit bucket rather than the caller's user PAT.
        Assert.Equal(1, tokenProvider.Calls);
        Assert.NotNull(gh.LastEnvironment);
        Assert.True(gh.LastEnvironment!.TryGetValue("GH_TOKEN", out string? token));
        Assert.Equal("ghs_app_installation_token", token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_WhenAppCredentialsNotConfigured_FallsBackToAmbientGh(string? configuredPath)
    {
        var gh = new RecordingGh();

        var runner = GhRunnerFactory.Create(
            _ => configuredPath,
            () => throw new InvalidOperationException("token provider must not be built when App credentials are unconfigured"),
            gh.RunAsync);

        await runner(["api", "user"], CancellationToken.None);

        // Ambient gh: no environment override, so gh uses the caller's own auth exactly as before.
        Assert.Equal(1, gh.Calls);
        Assert.True(gh.LastEnvironmentWasNull);
        Assert.Null(gh.LastEnvironment);
    }
}
