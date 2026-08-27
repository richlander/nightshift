namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// How a <c>gh</c> subprocess is run. The runner drains both output streams concurrently with the wait so a
/// burst larger than a pipe buffer is neither truncated nor able to deadlock the child, returns the full
/// output and exit code on ordinary completion, and on cancellation confirms the launched process itself has
/// exited and unblocks both reads before the cancellation propagates. Descendant containment is best-effort
/// (a tree kill that cannot reach a process that outlived the root), and is not claimed here. The program name
/// is a seam so these facts are provable against a purpose-built child rather than only the real binary.
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
    public async Task RunProcessAsync_CancellationConfirmsTheLaunchedProcessIsDeadBeforeReturning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The launched process would otherwise run for a minute. The runner must not merely stop waiting on
        // cancellation: it must confirm the token-bearing process it started has actually exited before
        // handing control back. `exec sleep` replaces the shell in place, so the pid the shell recorded is the
        // very process the runner is waiting on.
        string dir = Path.Combine(AppContext.BaseDirectory, $"gh-runner-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string script = "echo $$ > \"$0/pid\"; exec sleep 60";

            using var cts = new CancellationTokenSource();
            Task<GhResult> run = GhAuthenticatedRunner.RunProcessAsync("/bin/sh", ["-c", script, dir], null, cts.Token);

            int pid = await WaitForPidAsync(Path.Combine(dir, "pid"), TestContext.Current.CancellationToken);
            Assert.True(IsAlive(pid), "launched process was not running before cancellation");
            Assert.False(run.IsCompleted, "runner completed while the launched process was still alive");

            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

            // The runner returned only after confirming exit, so the process is gone immediately — no
            // post-return wait.
            Assert.False(IsAlive(pid), "launched process was still alive after the runner completed cancellation");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task RunProcessAsync_CancellationUnblocksWhenARootExitedDescendantHoldsThePipe()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The root exits but leaves a descendant that inherited the stdout pipe and idles, so the output drain
        // can never reach EOF on its own — the completion would hang forever. Cancellation must still be
        // prompt: the runner closes the read streams to unblock the drains, observes them, and propagates the
        // cancellation rather than leaving an orphaned read task running. Descendant containment is
        // best-effort, so this test kills the known descendant itself rather than asserting the runner did.
        string dir = Path.Combine(AppContext.BaseDirectory, $"gh-runner-inherit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        int descPid = 0;
        try
        {
            string script =
                "d=\"$0\"; " +
                "sh -c 'echo $$ > \"$0/desc.pid\"; echo r > \"$0/ready\"; while : ; do sleep 0.1; done' \"$d\" & " +
                "while [ ! -f \"$d/ready\" ]; do sleep 0.01; done; " +
                "exit 0";

            using var cts = new CancellationTokenSource();
            Task<GhResult> run = GhAuthenticatedRunner.RunProcessAsync("/bin/sh", ["-c", script, dir], null, cts.Token);

            descPid = await WaitForPidAsync(Path.Combine(dir, "desc.pid"), TestContext.Current.CancellationToken);
            await WaitForFileAsync(Path.Combine(dir, "ready"), TestContext.Current.CancellationToken);

            await cts.CancelAsync();

            // If the runner did not unblock the drain, its completion (and this await) would hang until the
            // timeout turned it into a TimeoutException — which is not an OperationCanceledException, so this
            // assertion is exactly the "prompt cancellation, no orphaned drain" check.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            KillIfAlive(descPid);
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

    private static void KillIfAlive(int pid)
    {
        if (pid <= 0)
        {
            return;
        }

        try
        {
            using Process process = Process.GetProcessById(pid);
            process.Kill();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or not ours to signal; nothing to clean up.
        }
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
