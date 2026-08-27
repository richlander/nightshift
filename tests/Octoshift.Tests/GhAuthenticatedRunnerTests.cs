namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// How a <c>gh</c> subprocess is run. The retained runner has to take its whole process tree down when the
/// caller cancels — an abandoned <c>gh</c> holds a token in its environment — and it has to drain both output
/// streams concurrently with the wait so a burst larger than a pipe buffer is neither truncated nor able to
/// deadlock the child. The program name is a seam so these facts are provable against a purpose-built child
/// rather than only the real binary.
/// </summary>
public class GhAuthenticatedRunnerTests
{
    [Fact]
    public async Task RunProcessAsync_ReturnsExitCodeAndBothStreams()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        GhResult result = await GhAuthenticatedRunner.RunProcessAsync(
            "/bin/sh",
            ["-c", "printf 'to-out\\n'; printf 'to-err\\n' >&2; exit 7"],
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(7, result.ExitCode);
        Assert.Contains("to-out", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("to-err", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProcessAsync_DrainsLargeBurstsOnBothStreamsWithoutTruncation()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // A busy child writes far more than one pipe buffer to both streams. Reading either stream only after
        // exit would truncate it, and a child whose stdout pipe fills stops making progress — so the reads
        // have to run concurrently with the wait. The last line of each stream is the witness that they did.
        GhResult result = await GhAuthenticatedRunner.RunProcessAsync(
            "/bin/sh",
            ["-c", "i=0; while [ $i -lt 5000 ]; do printf 'out %s\\n' \"$i\"; printf 'err %s\\n' \"$i\" >&2; i=$((i+1)); done"],
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out 4999", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("err 4999", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProcessAsync_AppliesEnvironmentOverridesToTheChild()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal) { ["GH_TOKEN"] = "injected-token" };

        GhResult result = await GhAuthenticatedRunner.RunProcessAsync(
            "/bin/sh",
            ["-c", "printf 'token=%s\\n' \"$GH_TOKEN\""],
            environment,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("token=injected-token", result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProcessAsync_CancellationKillsTheChildRatherThanAbandoningIt()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // A child that is merely abandoned keeps running: it would finish its sleep and create the marker.
        // A child whose tree is killed never gets there. The marker is written under the test's own output
        // directory, and the observation window is longer than the child's sleep, so its continued absence
        // can only mean the process was terminated — not that we simply stopped waiting on it.
        string marker = Path.Combine(AppContext.BaseDirectory, $"gh-runner-kill-{Guid.NewGuid():N}.marker");
        Assert.False(File.Exists(marker));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => GhAuthenticatedRunner.RunProcessAsync(
                "/bin/sh",
                ["-c", "sleep 2; : > \"$0\"", marker],
                null,
                cts.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {elapsed.Elapsed}");

        // Wait well past the child's own sleep. An abandoned child would have created the marker by now.
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(File.Exists(marker), "the cancelled gh child was abandoned rather than killed");
    }

    [Fact]
    public async Task RunProcessAsync_AProgramThatIsNotThereIsReportedRatherThanThrown()
    {
        GhResult result = await GhAuthenticatedRunner.RunProcessAsync(
            "octoshift-no-such-gh", [], null, TestContext.Current.CancellationToken);

        Assert.Equal(127, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }

    [Fact]
    public async Task Create_InjectsTheProviderTokenAsGhTokenForEveryInvocation()
    {
        IReadOnlyDictionary<string, string?>? capturedEnvironment = null;
        var provider = new FixedTokenProvider(new GitHubInstallationToken("secret-token", DateTimeOffset.UtcNow.AddHours(1)));

        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> run = GhAuthenticatedRunner.Create(
            provider,
            (args, environment, ct) =>
            {
                capturedEnvironment = environment;
                return Task.FromResult(new GhResult(0, string.Empty, string.Empty));
            });

        await run(["api", "/rate_limit"], TestContext.Current.CancellationToken);

        Assert.NotNull(capturedEnvironment);
        Assert.True(capturedEnvironment!.TryGetValue("GH_TOKEN", out string? value));
        Assert.Equal("secret-token", value);
    }

    private sealed class FixedTokenProvider(GitHubInstallationToken token) : IGitHubInstallationTokenProvider
    {
        public Task<GitHubInstallationToken> GetTokenAsync(CancellationToken ct) => Task.FromResult(token);
    }
}
