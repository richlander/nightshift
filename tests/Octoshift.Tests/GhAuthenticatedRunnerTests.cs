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
    public async Task RunProcessAsync_CancellationDoesNotCompleteUntilTheWholeTreeIsDead()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The child forks a descendant, both record their pids, and both sleep indefinitely. The runner must
        // not complete the cancellation until the entire tree is down: an implementation that merely stopped
        // waiting, or that signalled a kill without confirming it, would hand control back with a
        // token-bearing process still alive. Synchronization is deterministic — the test waits for the two
        // pid files rather than for a fixed delay — and the assertion is on the processes themselves.
        string dir = Path.Combine(AppContext.BaseDirectory, $"gh-runner-tree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // $0 is the directory. The descendant writes its own pid and idles; the child writes its pid,
            // waits for the descendant's pid to exist, signals ready, then idles. SIGKILL from a tree kill
            // cannot be trapped, so nothing here can survive it.
            string script =
                "d=\"$0\"; " +
                "sh -c 'echo $$ > \"$0/desc.pid\"; while : ; do sleep 0.1; done' \"$d\" & " +
                "echo $$ > \"$d/child.pid\"; " +
                "while [ ! -s \"$d/desc.pid\" ]; do sleep 0.01; done; " +
                "echo ready > \"$d/ready\"; " +
                "wait";

            using var cts = new CancellationTokenSource();
            Task<GhResult> run = GhAuthenticatedRunner.RunProcessAsync("/bin/sh", ["-c", script, dir], null, cts.Token);

            int childPid = await WaitForPidAsync(Path.Combine(dir, "child.pid"), TestContext.Current.CancellationToken);
            int descPid = await WaitForPidAsync(Path.Combine(dir, "desc.pid"), TestContext.Current.CancellationToken);
            await WaitForFileAsync(Path.Combine(dir, "ready"), TestContext.Current.CancellationToken);

            // Both processes are up and the runner is still blocked on the tree — it has not returned control.
            Assert.True(IsAlive(childPid), "child was not running before cancellation");
            Assert.True(IsAlive(descPid), "descendant was not running before cancellation");
            Assert.False(run.IsCompleted, "runner completed while the tree was still alive");

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);

            // The runner has returned only now. The direct child is awaited to exit before it returns, so it
            // is dead the instant the await unblocks; the descendant is SIGKILL'd as part of the tree, and a
            // bounded wait-for-death (not a fixed sleep) absorbs only the kernel's reap latency. Neither is
            // left running behind a completed runner.
            Assert.False(IsAlive(childPid), "child was still alive after the runner completed cancellation");
            await AssertDiesAsync(descPid, TestContext.Current.CancellationToken);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    private static async Task<int> WaitForPidAsync(string path, CancellationToken ct)
    {
        await WaitForFileAsync(path, ct);
        for (int i = 0; i < 500; i++)
        {
            string text = File.ReadAllText(path).Trim();
            if (text.Length > 0 && int.TryParse(text, out int pid))
            {
                return pid;
            }

            await Task.Delay(10, ct);
        }

        throw new InvalidOperationException($"pid never appeared in {path}");
    }

    private static async Task WaitForFileAsync(string path, CancellationToken ct)
    {
        for (int i = 0; i < 500; i++)
        {
            if (File.Exists(path) && new FileInfo(path).Length > 0)
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        throw new InvalidOperationException($"file never appeared: {path}");
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task AssertDiesAsync(int pid, CancellationToken ct)
    {
        for (int i = 0; i < 500; i++)
        {
            if (!IsAlive(pid))
            {
                return;
            }

            await Task.Delay(10, ct);
        }

        Assert.Fail($"process {pid} was still alive after the runner completed cancellation");
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
