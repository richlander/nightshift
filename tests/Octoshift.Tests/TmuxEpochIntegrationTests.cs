namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The epoch, exercised against a real tmux. The whole point of it is empirical — a claim about which
/// tmux value survives session churn and which does not — so it is checked against tmux rather than a
/// fixture: the scanned epoch stays fixed when the oldest session is killed, and changes when the server
/// restarts.
/// </summary>
public sealed class TmuxEpochIntegrationTests : IDisposable
{
    private readonly string? _tmux = ResolveTmux();
    private readonly string _socket = "octoshift-epoch-" + Guid.NewGuid().ToString("N");
    private readonly string _wrapperDir;

    public TmuxEpochIntegrationTests()
    {
        _wrapperDir = Path.Combine(Path.GetTempPath(), "octoshift-tmuxwrap-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_wrapperDir);
        if (_tmux is not null && !OperatingSystem.IsWindows())
        {
            // A `tmux` on PATH that pins every call to this test's private server, so the real collection
            // script — which calls bare `tmux` — talks only to it and never to a developer's own sessions.
            string wrapper = Path.Combine(_wrapperDir, "tmux");
            File.WriteAllText(wrapper, $"#!/bin/sh\nexec {_tmux} -L {_socket} \"$@\"\n");
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    public void Dispose()
    {
        if (_tmux is not null)
        {
            RunTmux("kill-server");
        }

        try
        {
            Directory.Delete(_wrapperDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Epoch_IsStableAcrossSessionChurnAndChangesOnRestart()
    {
        if (_tmux is null)
        {
            Assert.Skip("tmux is not installed");
        }

        RunTmux("new-session", "-d", "-s", "first");
        RunTmux("new-session", "-d", "-s", "second");

        string epoch1 = await ScanEpochAsync();

        // Kill the oldest session: its creation time changes the oldest-session heuristic the epoch used to
        // rely on, but the server's pid and start time are unchanged, so the epoch must not move.
        RunTmux("kill-session", "-t", "first");
        string epoch2 = await ScanEpochAsync();
        Assert.Equal(epoch1, epoch2);

        // A genuine restart is a new server: a different start time (and pid), so the epoch changes and the
        // rename epoch guard would correctly refuse to touch recycled ids.
        RunTmux("kill-server");
        RunTmux("new-session", "-d", "-s", "afterrestart");
        string epoch3 = await ScanEpochAsync();
        Assert.NotEqual(epoch1, epoch3);
    }

    private async Task<string> ScanEpochAsync()
    {
        var scanner = new TmuxScanner(host: null, runAsync: RunViaWrapperAsync);
        IReadOnlyList<TmuxPane> panes = await scanner.ScanAsync(TestContext.Current.CancellationToken);
        Assert.NotEmpty(panes);
        string epoch = panes[0].Epoch;
        Assert.All(panes, p => Assert.Equal(epoch, p.Epoch));
        Assert.NotEmpty(epoch);
        return epoch;
    }

    private async Task<CommandResult> RunViaWrapperAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
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
