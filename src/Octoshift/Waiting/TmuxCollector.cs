namespace Octoshift.Waiting;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;

/// <summary>The result of running one collection command.</summary>
internal readonly record struct CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Runs one shell script, locally or on another host.
/// </summary>
/// <remarks>
/// Transport is deliberately left to <c>ssh</c> and the user's <c>~/.ssh/config</c> rather than
/// reimplemented here: ProxyJump, control sockets and per-host fallbacks already live there, and a tool
/// that re-specified them would drift from the config every other tool on the machine obeys.
/// </remarks>
internal static class ShellRunner
{
    /// <summary>Builds a runner for a host, or for this machine when <paramref name="host"/> is null.</summary>
    public static Func<string, CancellationToken, Task<CommandResult>> For(string? host)
        => host is null
            ? (script, ct) => RunAsync("/bin/sh", ["-c", script], ct)

            // BatchMode so a host needing a passphrase fails fast instead of hanging the sweep, and a
            // short connect timeout so one unreachable box cannot stall the others.
            : (script, ct) => RunAsync("ssh", ["-o", "BatchMode=yes", "-o", "ConnectTimeout=10", host, script], ct);

    internal static async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new CommandResult(127, string.Empty, ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return new CommandResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
