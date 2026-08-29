namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// How an ambient <c>gh</c> subprocess is run. The runner injects no credentials of its own — the child
/// inherits octoshift's environment, so the caller's own <c>gh</c> auth (ambient credential storage or an
/// externally supplied <c>GH_TOKEN</c>) reaches gh as the host provides it; octoshift does not inspect, parse,
/// log, mint, unset, or modify authentication. It does override gh's <em>non-auth</em> execution controls
/// (<c>GH_TELEMETRY=false</c>, <c>GH_PAGER=cat</c>, and removing <c>GH_FORCE_TTY</c>) so <c>gh api</c> is a
/// plain noninteractive machine transport and the known persistent/detached and pager paths that spawn a
/// token-bearing process outliving the root — v2.97.0's detached telemetry child, a configured pager — are
/// prevented at the source. Beyond that it drains both output streams concurrently with the wait so a burst
/// larger than a pipe buffer is neither truncated nor able to deadlock the child, returns the full output and
/// exit code on ordinary completion, and on cancellation confirms the launched process itself has exited and
/// unblocks both reads before the cancellation propagates. Descendant containment is prevention, not a
/// sandbox: preventing those paths does not make the root gh's only process (a synchronous helper such as
/// macOS keyring's <c>/usr/bin/security</c> or Windows <c>tzutil</c> may still run), and such helpers remain
/// covered only by the best-effort tree kill — no hostile-descendant sandbox is claimed here. The program name
/// is a seam so these facts are provable against a purpose-built child rather than only the real binary.
///
/// Joins the non-parallel <c>ConsoleCapture</c> collection: the environment tests mutate process-wide
/// variables (<c>GH_TOKEN</c>, <c>GH_TELEMETRY</c>, <c>GH_PAGER</c>, <c>GH_FORCE_TTY</c>), so they must not run
/// alongside any other test that reads or redirects process-global state.
/// </summary>
[Collection("ConsoleCapture")]
public class GhProcessRunnerTests
{
    [Fact]
    public async Task RunProcessAsync_ReturnsExitCodeAndBothStreams()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        GhResult result = await GhProcessRunner.RunProcessAsync(
            "/bin/sh",
            ["-c", "printf 'to-out\\n'; printf 'to-err\\n' >&2; exit 7"],
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
        GhResult result = await GhProcessRunner.RunProcessAsync(
            "/bin/sh",
            ["-c", "i=0; while [ $i -lt 5000 ]; do printf 'out %s\\n' \"$i\"; printf 'err %s\\n' \"$i\" >&2; i=$((i+1)); done"],
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out 4999", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("err 4999", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunProcessAsync_InheritsAmbientAuthentication_AndInjectsNoTokenOfItsOwn()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The credential boundary: octoshift owns no token and injects none. An externally provisioned
        // GH_TOKEN in octoshift's own environment must reach the child unchanged (ambient auth is preserved,
        // never unset or replaced) — and with none provisioned, the child must see none, proving octoshift
        // adds nothing of its own.
        const string ambientToken = "externally-provisioned-token";
        string? previous = Environment.GetEnvironmentVariable("GH_TOKEN");
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", ambientToken);
            GhResult inherited = await GhProcessRunner.RunProcessAsync(
                "/bin/sh",
                ["-c", "printf 'token=%s\\n' \"$GH_TOKEN\""],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, inherited.ExitCode);
            Assert.Contains($"token={ambientToken}", inherited.Stdout, StringComparison.Ordinal);

            Environment.SetEnvironmentVariable("GH_TOKEN", null);
            GhResult none = await GhProcessRunner.RunProcessAsync(
                "/bin/sh",
                ["-c", "printf 'token=%s\\n' \"$GH_TOKEN\""],
                TestContext.Current.CancellationToken);
            Assert.Equal(0, none.ExitCode);
            Assert.Contains("token=", none.Stdout, StringComparison.Ordinal);
            Assert.DoesNotContain(ambientToken, none.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", previous);
        }
    }

    [Fact]
    public async Task RunProcessAsync_OverridesGhSideEffectControls_WhilePreservingAuthAndUnrelatedEnvironment()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The runner turns `gh api` into a plain machine transport by overriding gh's non-auth side-effect
        // controls, without touching the credential boundary. Seed octoshift's own environment with an
        // ambient token *and* hostile inherited values for the controls the runner is responsible for, plus an
        // unrelated variable, then prove at the process boundary that:
        //   * GH_TOKEN reaches the child unchanged (auth is inherited, never owned);
        //   * GH_TELEMETRY is exactly false (no sampled detached `gh send-telemetry` child);
        //   * GH_PAGER is exactly cat (no external pager inheriting the token and redirected pipes);
        //   * GH_FORCE_TTY is absent (the forced-TTY interactive path cannot reach this transport);
        //   * an unrelated inherited variable is preserved (the runner overrides only what it must).
        const string ambientToken = "externally-provisioned-token";
        const string unrelatedName = "OCTOSHIFT_TEST_UNRELATED_ENV";
        const string unrelatedValue = "unrelated-inherited-value";

        string? prevToken = Environment.GetEnvironmentVariable("GH_TOKEN");
        string? prevTelemetry = Environment.GetEnvironmentVariable("GH_TELEMETRY");
        string? prevPager = Environment.GetEnvironmentVariable("GH_PAGER");
        string? prevForceTty = Environment.GetEnvironmentVariable("GH_FORCE_TTY");
        string? prevUnrelated = Environment.GetEnvironmentVariable(unrelatedName);
        try
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", ambientToken);
            Environment.SetEnvironmentVariable("GH_TELEMETRY", "true");
            Environment.SetEnvironmentVariable("GH_PAGER", "less");
            Environment.SetEnvironmentVariable("GH_FORCE_TTY", "80");
            Environment.SetEnvironmentVariable(unrelatedName, unrelatedValue);

            GhResult result = await GhProcessRunner.RunProcessAsync(
                "/bin/sh",
                [
                    "-c",
                    "printf 'token=[%s]\\n' \"$GH_TOKEN\"; " +
                    "printf 'telemetry=[%s]\\n' \"$GH_TELEMETRY\"; " +
                    "printf 'pager=[%s]\\n' \"$GH_PAGER\"; " +
                    "printf 'forcetty=[%s]\\n' \"$GH_FORCE_TTY\"; " +
                    "printf 'unrelated=[%s]\\n' \"$OCTOSHIFT_TEST_UNRELATED_ENV\"",
                ],
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Contains($"token=[{ambientToken}]", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("telemetry=[false]", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("pager=[cat]", result.Stdout, StringComparison.Ordinal);
            Assert.Contains("forcetty=[]", result.Stdout, StringComparison.Ordinal);
            Assert.Contains($"unrelated=[{unrelatedValue}]", result.Stdout, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("GH_TOKEN", prevToken);
            Environment.SetEnvironmentVariable("GH_TELEMETRY", prevTelemetry);
            Environment.SetEnvironmentVariable("GH_PAGER", prevPager);
            Environment.SetEnvironmentVariable("GH_FORCE_TTY", prevForceTty);
            Environment.SetEnvironmentVariable(unrelatedName, prevUnrelated);
        }
    }

    [Fact]
    public async Task RunProcessAsync_CancellationConfirmsTheLaunchedProcessIsDeadBeforeReturning()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The launched process would otherwise run for a minute. The runner must not merely stop waiting on
        // cancellation: it must confirm the process it started has actually exited before
        // handing control back. `exec sleep` replaces the shell in place, so the pid the shell recorded is the
        // very process the runner is waiting on.
        string dir = Path.Combine(AppContext.BaseDirectory, $"gh-runner-direct-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            string script = "echo $$ > \"$0/pid\"; exec sleep 60";

            using var cts = new CancellationTokenSource();
            Task<GhResult> run = GhProcessRunner.RunProcessAsync("/bin/sh", ["-c", script, dir], cts.Token);

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
            Task<GhResult> run = GhProcessRunner.RunProcessAsync("/bin/sh", ["-c", script, dir], cts.Token);

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

    [Theory]
    // Kill "accepted" but the process never dies (the reviewer's accepted-but-unconfirmed defect): a bounded
    // confirmation must not turn into an unbounded await.
    [InlineData(true)]
    // Kill could not even be requested: the same deterministic cleanup failure.
    [InlineData(false)]
    public async Task RunProcessAsync_WhenExitCannotBeConfirmed_FailsPromptlyWithoutHanging(bool killRequestSucceeds)
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The gh runner path starts a POSIX child here; there is nothing to start on Windows.");

        // The injected kill request reports its result but never actually kills, and the launched process is a
        // genuinely live sleep still holding its output pipes — so the exit task never completes. Because a
        // kill only requests termination (it returns before the process is signalled), cleanup must confirm
        // exit with a bounded wait: with a short bound it must give up and fail promptly with
        // GhProcessCleanupException rather than await the exit (or completion, which begins by awaiting it)
        // forever. A regression of the unbounded await would hang until the outer 30s timeout turned into a
        // TimeoutException — which is not a GhProcessCleanupException — so this assertion is exactly the
        // promptness check. The still-live process is not killed on this path, so the test cleans it up itself.
        string dir = Path.Combine(AppContext.BaseDirectory, $"gh-runner-noconfirm-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        int pid = 0;
        try
        {
            string script = "echo $$ > \"$0/pid\"; exec sleep 60";

            using var cts = new CancellationTokenSource();
            Task<GhResult> run = GhProcessRunner.RunProcessAsync(
                "/bin/sh",
                ["-c", script, dir],
                (proc, tree) => killRequestSucceeds,
                TimeSpan.FromMilliseconds(150),
                cts.Token);

            pid = await WaitForPidAsync(Path.Combine(dir, "pid"), TestContext.Current.CancellationToken);
            Assert.True(IsAlive(pid), "launched process was not running before cancellation");

            await cts.CancelAsync();

            await Assert.ThrowsAsync<GhProcessCleanupException>(
                () => run.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        }
        finally
        {
            KillIfAlive(pid);
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
        GhResult result = await GhProcessRunner.RunProcessAsync(
            "octoshift-no-such-gh", [], TestContext.Current.CancellationToken);

        Assert.Equal(127, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }
}
