namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The generated rename script run for real against a private tmux server. Two properties cannot be
/// asserted structurally and are the whole reason the rename path is shaped the way it is: that no byte of
/// an agent-set window name can execute a command, and that the epoch check and the mutation share one
/// server generation, so a restart that recycles window ids renames nothing. Both are checked here by
/// running the exact bytes <see cref="WindowNaming.BuildRenameScript"/> emits through <c>/bin/sh</c>
/// against a live tmux, and reading the result back through the encoded scanner so a name is compared
/// byte-for-byte. Skipped where tmux is not installed.
/// </summary>
public sealed class TmuxRenameIntegrationTests : IDisposable
{
    private readonly string? _tmux = ResolveTmux();
    private readonly string _socket = "octoshift-rename-" + Guid.NewGuid().ToString("N");
    private readonly string _wrapperDir;
    private readonly string _workDir;
    private readonly string _log;

    public TmuxRenameIntegrationTests()
    {
        _wrapperDir = Path.Combine(Path.GetTempPath(), "octoshift-renamewrap-" + Guid.NewGuid().ToString("N"));
        _workDir = Path.Combine(Path.GetTempPath(), "octoshift-renamework-" + Guid.NewGuid().ToString("N"));
        _log = Path.Combine(_workDir, "tmux-invocations.log");
        Directory.CreateDirectory(_wrapperDir);
        Directory.CreateDirectory(_workDir);
        WriteWrapper(string.Empty);
    }

    // Writes the `tmux` wrapper that pins every call to this test's private server and logs each
    // invocation's subcommand, with an optional extra shell fragment run before the exec — used to force a
    // restart at a chosen point.
    private void WriteWrapper(string beforeExec)
    {
        if (_tmux is null || OperatingSystem.IsWindows())
        {
            return;
        }

        string wrapper = Path.Combine(_wrapperDir, "tmux");
        File.WriteAllText(wrapper, $"#!/bin/sh\nprintf '%s\\n' \"$1\" >> {_log}\n{beforeExec}exec {_tmux} -L {_socket} \"$@\"\n");
        File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose()
    {
        if (_tmux is not null)
        {
            RunTmux("kill-server");
        }

        foreach (string dir in new[] { _wrapperDir, _workDir })
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Theory]
    [InlineData("x'; touch INJECTED; echo 'y")]           // single-quote break + a command
    [InlineData("x$(touch INJECTED)y")]                    // command substitution
    [InlineData("x`touch INJECTED`y")]                     // backticks
    [InlineData("x; touch INJECTED; :")]                   // bare semicolons
    [InlineData("-leadingdash")]                           // an option-looking name
    [InlineData("plain spaces here")]                      // spaces inside the name
    [InlineData("café-日本語-作業")]                        // multibyte / unicode (BMP)
    [InlineData("🚀 ship it")]                              // an astral scalar (needs \\U escaping)
    public async Task Rename_AppliesAnArbitraryNameExactlyAndExecutesNoCommand(string desired)
    {
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "s");
        (string epoch, TmuxPane pane) = await FirstWindowAsync();

        string nonce = Nonce();
        string script = WindowNaming.BuildRenameScript([(pane, desired)], epoch, nonce)!;
        CommandResult result = await RunAsync(script, TestContext.Current.CancellationToken);

        // The name reaches tmux as data: no marker command ran, and the window carries exactly the bytes,
        // read back through the encoded scanner so the comparison is byte-for-byte.
        Assert.Empty(Directory.GetFiles(_workDir, "INJECTED*"));
        Assert.Contains($"{nonce}:ok:{pane.WindowId}", result.Stdout, StringComparison.Ordinal);
        (_, TmuxPane renamed) = await FirstWindowAsync();
        Assert.Equal(desired, renamed.WindowName);
    }

    [Fact]
    public async Task Rename_AbortsWhenTheServerRestartedAndRecycledTheWindowId()
    {
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "before");
        (string staleEpoch, TmuxPane pane) = await FirstWindowAsync();

        // A genuine restart: the socket is the same, so the window id is recycled to name a different
        // window, but the server generation has moved.
        RunTmux("kill-server");
        RunTmux("new-session", "-d", "-s", "after");
        (string freshEpoch, TmuxPane recycled) = await FirstWindowAsync();
        Assert.Equal(pane.WindowId, recycled.WindowId);
        Assert.NotEqual(staleEpoch, freshEpoch);

        // Give the recycled window a stable name: an explicit rename also turns automatic-rename off, so
        // the assertion below is not racing tmux renaming the window as its shell starts.
        RunTmux("rename-window", "-t", recycled.WindowId, "sentinel");

        // The rename was planned under the stale epoch. Its guard compares the live generation and refuses,
        // so the recycled window keeps its name and the batch reports the epoch marker, not a rename.
        string nonce = Nonce();
        string script = WindowNaming.BuildRenameScript([(recycled, "hijacked")], staleEpoch, nonce)!;
        CommandResult result = await RunAsync(script, TestContext.Current.CancellationToken);

        Assert.Contains($"{nonce}:epoch", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain($"{nonce}:ok:", result.Stdout, StringComparison.Ordinal);
        (_, TmuxPane after) = await FirstWindowAsync();
        Assert.Equal("sentinel", after.WindowName);
        Assert.NotEqual("hijacked", after.WindowName);
    }

    [Fact]
    public async Task Rename_TouchesOnlyItsWindowIdLeavingSiblingsAlone()
    {
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "s");
        RunTmux("new-window");
        string epoch = (await FirstWindowAsync()).Epoch;
        IReadOnlyList<TmuxPane> before = await ScanAsync();
        Assert.True(before.Count >= 2);
        TmuxPane target = before[1];
        string otherName = before[0].WindowName;

        string nonce = Nonce();
        string script = WindowNaming.BuildRenameScript([(target, "renamed-target")], epoch, nonce)!;
        await RunAsync(script, TestContext.Current.CancellationToken);

        IReadOnlyList<TmuxPane> after = await ScanAsync();
        Assert.Equal("renamed-target", after.Single(p => p.WindowId == target.WindowId).WindowName);
        Assert.Equal(otherName, after.Single(p => p.WindowId == before[0].WindowId).WindowName);
    }

    [Fact]
    public async Task Rename_RunsTheMutationInTheGuardsOwnClientWithNoNestedTmux()
    {
        // The boundary regression. The rename must happen inside the epoch guard's own tmux client — the
        // if-shell branch, run in that one server connection — so there is no window between "checked the
        // epoch" and "renamed the id" for a restart to slip into. A design that instead selects a
        // run-shell and shells out to a nested `tmux rename-window` opens exactly that window; the wrapper
        // logs every tmux invocation's subcommand, so a nested rename-window (or a run-shell) would be
        // recorded. It is not: only if-shell is, and the window is still renamed and confirmed, proving
        // the mutation went through the guard's branch and not a reconnecting client.
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "s");
        (string epoch, TmuxPane pane) = await FirstWindowAsync();

        string nonce = Nonce();
        string script = WindowNaming.BuildRenameScript([(pane, "guard-branch")], epoch, nonce)!;
        File.Delete(_log);
        CommandResult result = await RunAsync(script, TestContext.Current.CancellationToken);

        Assert.Contains($"{nonce}:ok:{pane.WindowId}", result.Stdout, StringComparison.Ordinal);
        (_, TmuxPane renamed) = await FirstWindowAsync();
        Assert.Equal("guard-branch", renamed.WindowName);

        string[] subcommands = File.ReadAllLines(_log);
        Assert.Contains("if-shell", subcommands);
        Assert.DoesNotContain("rename-window", subcommands);
        Assert.DoesNotContain("run-shell", subcommands);
    }

    [Fact]
    public async Task Rename_ARestartBetweenWindowsAbortsTheLaterWindowsGuard()
    {
        // Per-window guards, exercised across a restart that lands between windows — the reviewer's
        // kill-at-the-boundary case. The wrapper restarts the server just before the second window's
        // if-shell, so the first window is renamed on the original server while the second's guard, now
        // seeing a fresh generation, refuses. The recycled window id the restart mints is never renamed to
        // the second window's name.
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "s");
        RunTmux("new-window");
        IReadOnlyList<TmuxPane> before = await ScanAsync();
        Assert.True(before.Count >= 2);
        string epoch = before[0].Epoch;
        TmuxPane w1 = before[0];
        TmuxPane w2 = before[1];

        // A wrapper that, on the second if-shell it sees, tears the server down and starts a fresh one
        // before forwarding the call — so that if-shell evaluates against a new generation.
        WriteWrapper(
            $"if [ \"$1\" = if-shell ]; then c=$(cat {_workDir}/n 2>/dev/null || echo 0); c=$((c+1)); echo $c > {_workDir}/n; "
            + $"if [ $c = 2 ]; then {_tmux} -L {_socket} kill-server 2>/dev/null; {_tmux} -L {_socket} new-session -d -s recycled 2>/dev/null; fi; fi\n");

        string nonce = Nonce();
        string script = WindowNaming.BuildRenameScript([(w1, "first-window"), (w2, "second-window")], epoch, nonce)!;
        CommandResult result = await RunAsync(script, TestContext.Current.CancellationToken);

        // The first window was renamed and confirmed on the original server; the second's guard failed
        // against the restarted server and reported the epoch marker instead.
        Assert.Contains($"{nonce}:ok:{w1.WindowId}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains($"{nonce}:epoch", result.Stdout, StringComparison.Ordinal);
        Assert.DoesNotContain($"{nonce}:ok:{w2.WindowId}", result.Stdout, StringComparison.Ordinal);

        // Whatever window ids the restarted server recycled, none carries the second window's name.
        WriteWrapper(string.Empty);
        IReadOnlyList<TmuxPane> after = await ScanAsync();
        Assert.DoesNotContain(after, p => p.WindowName == "second-window");
    }

    private async Task<(string Epoch, TmuxPane Pane)> FirstWindowAsync()
    {
        IReadOnlyList<TmuxPane> panes = await ScanAsync();
        Assert.NotEmpty(panes);
        return (panes[0].Epoch, panes[0]);
    }

    private async Task<IReadOnlyList<TmuxPane>> ScanAsync()
        => await new TmuxScanner(host: null, runAsync: RunAsync).ScanAsync(TestContext.Current.CancellationToken);

    private static string Nonce() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private async Task<CommandResult> RunAsync(string script, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _workDir,
        };
        psi.Environment["PATH"] = _wrapperDir + ":" + Environment.GetEnvironmentVariable("PATH");

        using Process p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script.AsMemory(), ct);
        p.StandardInput.Close();
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, stdout, stderr);
    }

    private void RunTmux(params string[] args)
    {
        if (_tmux is null)
        {
            return;
        }

        var psi = new ProcessStartInfo(_tmux) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        psi.ArgumentList.Add("-L");
        psi.ArgumentList.Add(_socket);
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        try
        {
            using Process p = Process.Start(psi)!;
            p.WaitForExit(5000);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static string? ResolveTmux()
    {
        foreach (string candidate in new[] { "/opt/homebrew/bin/tmux", "/usr/local/bin/tmux", "/usr/bin/tmux", "/bin/tmux" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
