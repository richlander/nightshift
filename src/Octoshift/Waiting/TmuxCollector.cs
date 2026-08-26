namespace Octoshift.Waiting;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>The result of running one collection command.</summary>
internal readonly record struct CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>The program a collection runs, and the arguments it is started with — never the script.</summary>
internal readonly record struct Invocation(string File, IReadOnlyList<string> Arguments);

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
    /// <exception cref="ArgumentException">
    /// The host is empty or option-shaped. Callers validate with <see cref="HostTarget.Validate"/> and
    /// report a usage error; this guard is here so no future caller can construct ssh arguments from a
    /// value ssh would read as an option. Thrown here rather than inside the returned delegate, so a bad
    /// host fails where it is named instead of mid-sweep.
    /// </exception>
    public static Func<string, CancellationToken, Task<CommandResult>> For(string? host)
    {
        Invocation invocation = InvocationFor(host);
        return (script, ct) => RunAsync(invocation.File, invocation.Arguments, script, ct);
    }

    /// <summary>
    /// The program and arguments a collection is started with. Pure, so the one property that matters
    /// about them is testable: the script is not among them.
    /// </summary>
    /// <remarks>
    /// Two things are wrong with handing the script to the remote account's login shell as a command
    /// argument, which is what <c>ssh host '&lt;script&gt;'</c> does.
    ///
    /// The first is that the login shell parses it. A POSIX script is not csh, and on a host whose account
    /// shell is csh or tcsh the collection dies on its own syntax — reported as an unreachable host, which
    /// is not what happened. Naming <c>/bin/sh</c> explicitly makes the remote command a single word the
    /// login shell only has to <em>exec</em>, and <c>-s</c> tells that shell to read the script from
    /// standard input.
    ///
    /// The second is that an argument is public. <c>ps</c> shows every argument of every process to every
    /// process of the same user — and the agents being swept are that same user. The script carries this
    /// run's nonce, so a script in argv hands the framing token to exactly the panes the framing exists to
    /// defend against: a pane that reads the nonce can close its own capture early, open a neighbour's,
    /// and write that neighbour's screen. Sending the script on stdin keeps the nonce in a pipe, which is
    /// nobody else's to read. <c>-T</c> makes sure that pipe is a pipe: a host configured with
    /// <c>RequestTTY force</c> would otherwise put a terminal between the script and the shell, which
    /// echoes it back into the output it is meant to be reading.
    /// </remarks>
    internal static Invocation InvocationFor(string? host)
    {
        if (host is not null && HostTarget.Validate(host) is { } error)
        {
            throw new ArgumentException(error, nameof(host));
        }

        return host is null
            ? new Invocation("/bin/sh", ["-s"])

            // BatchMode so a host needing a passphrase fails fast instead of hanging the sweep, and a
            // short connect timeout so one unreachable box cannot stall the others.
            : new Invocation("ssh", ["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "--", host, "/bin/sh", "-s"]);
    }

    /// <summary>
    /// Starts <paramref name="file"/>, writes <paramref name="stdin"/> to it, and returns everything it
    /// said. Both output streams are drained concurrently with the write, because a child that fills its
    /// stdout pipe stops reading its stdin and neither side ever moves again.
    /// </summary>
    internal static async Task<CommandResult> RunAsync(string file, IReadOnlyList<string> args, string? stdin, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new CommandResult(127, string.Empty, ex.Message);
        }

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);
        Task feed = stdin is null ? Task.CompletedTask : FeedAsync(proc, stdin, ct);

        try
        {
            await proc.WaitForExitAsync(ct);
            await feed;
            return new CommandResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process alone leaves the ssh or tmux child running, and an abandoned ssh
            // holds a connection open. Take the tree down before the cancellation propagates.
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
            {
                // Already gone, or the platform will not walk the tree; nothing further to do.
            }

            throw;
        }
    }

    /// <summary>
    /// Writes the script and closes the pipe, because the shell reads to end of file before it runs
    /// anything. A child that exited without reading it is not an error here: its exit code and whatever
    /// it did say are the answer, and a broken pipe is only how that answer arrives.
    /// </summary>
    private static async Task FeedAsync(Process proc, string script, CancellationToken ct)
    {
        try
        {
            await proc.StandardInput.WriteAsync(script.AsMemory(), ct);
            await proc.StandardInput.FlushAsync(ct);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
        finally
        {
            try
            {
                proc.StandardInput.Close();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }
    }
}
