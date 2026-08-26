namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// A host with no running tmux server is a success — a machine observed to hold no windows — while a
/// missing or broken tmux stays fail-closed as unreachable. The distinction is made in the collection
/// script, so it is exercised by running that script: against a real tmux with no server (the socket does
/// not exist), and against a fake <c>tmux</c> that reproduces each failure signature exactly.
/// </summary>
public sealed class TmuxNoServerTests : IDisposable
{
    private readonly string _dir;

    public TmuxNoServerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "octoshift-noserver-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task NoServer_RealTmuxWithADeadSocketIsAnEmptySuccess()
    {
        string? tmux = ResolveTmux();
        if (tmux is null || OperatingSystem.IsWindows())
        {
            Assert.Skip("tmux is not installed");
        }

        // A private socket with no server ever started: `tmux list-windows` fails with the real
        // connection error, which the script must read as "no windows here", not "host unreachable".
        string socket = "octoshift-dead-" + Guid.NewGuid().ToString("N");
        Wrapper($"exec {tmux} -L {socket} \"$@\"\n");

        IReadOnlyList<TmuxPane> panes = await Scan();
        Assert.Empty(panes);
    }

    [Theory]
    [InlineData("no server running on /tmp/tmux-501/default")]
    [InlineData("error connecting to /tmp/tmux-501/default (No such file or directory)")]
    public async Task NoServer_KnownSignaturesAreAnEmptySuccess(string message)
    {
        Wrapper($"printf '%s\\n' '{message}' >&2\nexit 1\n");

        IReadOnlyList<TmuxPane> panes = await Scan();
        Assert.Empty(panes);
    }

    [Theory]
    [InlineData(127, "sh: tmux: command not found")]                              // a missing binary
    [InlineData(1, "permission denied")]                                          // a locked-down socket
    [InlineData(1, "error connecting to /tmp/tmux-501/default (Permission denied)")] // connect error, but denied
    [InlineData(1, "open terminal failed: No such file or directory")]            // No-such-file, but not the connect signature
    [InlineData(1, "some other tmux failure")]                                    // anything unrecognised
    public async Task BrokenTmux_StaysUnavailable(int code, string message)
    {
        Wrapper($"printf '%s\\n' '{message}' >&2\nexit {code}\n");

        await Assert.ThrowsAsync<TmuxUnavailableException>(Scan);
    }

    private async Task<IReadOnlyList<TmuxPane>> Scan()
        => await new TmuxScanner(host: null, runAsync: RunAsync).ScanAsync(TestContext.Current.CancellationToken);

    private void Wrapper(string body)
    {
        string wrapper = Path.Combine(_dir, "tmux");
        File.WriteAllText(wrapper, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    private async Task<CommandResult> RunAsync(string script, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir,
        };
        psi.Environment["PATH"] = _dir + ":" + Environment.GetEnvironmentVariable("PATH");

        using Process p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script.AsMemory(), ct);
        p.StandardInput.Close();
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, stdout, stderr);
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
