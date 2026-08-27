namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Builds <c>gh</c> runner delegates that inject an installation token as <c>GH_TOKEN</c>.
/// </summary>
internal static class GhAuthenticatedRunner
{
    public static Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> Create(
        IGitHubInstallationTokenProvider tokenProvider)
        => Create(tokenProvider, RunGhAsync);

    internal static Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> Create(
        IGitHubInstallationTokenProvider tokenProvider,
        Func<IReadOnlyList<string>, IReadOnlyDictionary<string, string?>?, CancellationToken, Task<GhResult>> runGhAsync)
    {
        ArgumentNullException.ThrowIfNull(tokenProvider);
        ArgumentNullException.ThrowIfNull(runGhAsync);

        return async (args, ct) =>
        {
            GitHubInstallationToken token = await tokenProvider.GetTokenAsync(ct);
            var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["GH_TOKEN"] = token.Token,
            };

            return await runGhAsync(args, environment, ct);
        };
    }

    internal static Task<GhResult> RunGhAsync(
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken ct)
        => RunProcessAsync("gh", args, environmentOverrides, ct);

    /// <summary>
    /// Starts <paramref name="file"/> with <paramref name="args"/> and returns its exit code and captured
    /// stdout/stderr. On ordinary completion the full output and exit code are returned. On cancellation the
    /// contract is scoped to the process we launch: the tree is asked to die (best-effort
    /// <see cref="Process.Kill(bool)"/>, so a descendant that outlived the root may not be reached — durable
    /// descendant containment is tracked separately), but the direct token-bearing process is always
    /// confirmed exited and both output reads are unblocked and observed before the cancellation propagates,
    /// so the runner never returns with the launched process alive nor leaves a drain task running. The
    /// program name is a parameter so this can be exercised against a purpose-built child rather than only the
    /// real <c>gh</c> binary.
    /// </summary>
    internal static async Task<GhResult> RunProcessAsync(
        string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken ct)
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

        if (environmentOverrides is not null)
        {
            foreach ((string key, string? value) in environmentOverrides)
            {
                if (value is null)
                {
                    psi.Environment.Remove(key);
                }
                else
                {
                    psi.Environment[key] = value;
                }
            }
        }

        using var proc = new Process { StartInfo = psi };

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GhResult(127, string.Empty, ex.Message);
        }

        // Read both streams without the caller's token: cancelling a read would abandon the pipe mid-drain,
        // and the point of cancellation here is the opposite — to bring the process down and account for its
        // output before returning. A child that fills its stdout pipe stops making progress, so both are read
        // concurrently with the wait.
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        // A single uncancellable exit task, shared by ordinary completion and cancellation cleanup so there is
        // never a second concurrent WaitForExitAsync on the same process.
        Task exit = proc.WaitForExitAsync(CancellationToken.None);

        // One completion task: the direct process exits and both drains finish. Awaiting it through the
        // caller's token means cancellation still interrupts the case where the root exited but a descendant
        // that inherited a pipe keeps the drain from ever reaching EOF.
        Task<GhResult> completion = CompleteAsync(proc, exit, stdout, stderr);

        try
        {
            return await completion.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(proc, exit, completion, stdout, stderr).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<GhResult> CompleteAsync(Process proc, Task exit, Task<string> stdout, Task<string> stderr)
    {
        await exit.ConfigureAwait(false);
        return new GhResult(proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// Cancellation cleanup. Requests termination of the tree (falling back to the direct process); if that
    /// cannot be done it records a controlled cleanup failure instead of waiting on an exit that may never
    /// come. Either way it closes the read streams to unblock an inherited-pipe drain and observes every task,
    /// so no read is left faulted-and-forgotten when the process handles are disposed. A recorded cleanup
    /// failure is thrown only after that observation.
    /// </summary>
    private static async Task TerminateAndDrainAsync(Process proc, Task exit, Task completion, Task stdout, Task stderr)
    {
        Exception? cleanupFailure = null;
        try
        {
            if (RequestTermination(proc))
            {
                // Uncancellable: the launched process must be confirmed gone before control returns.
                await exit.ConfigureAwait(false);
            }
            else
            {
                // Termination could neither be requested nor its exit confirmed. Do not wait on an exit that
                // may never come; record a deterministic cleanup failure and still drain below.
                cleanupFailure = new GhProcessCleanupException(
                    "octoshift: could not terminate the gh process on cancellation; refusing to wait on a process that may still be alive.");
            }
        }
        finally
        {
            // The tree kill cannot reach a descendant that outlived the root, and such a descendant may still
            // hold a write end, so a drain could hang forever on EOF. Closing the read streams forces them to
            // end; their captured output is discarded on this path anyway. Observe every task so nothing is
            // left faulted-and-forgotten when Process.Dispose closes the handles.
            CloseStreams(proc);
            await ObserveAsync(completion).ConfigureAwait(false);
            await ObserveAsync(stdout).ConfigureAwait(false);
            await ObserveAsync(stderr).ConfigureAwait(false);
        }

        if (cleanupFailure is not null)
        {
            throw cleanupFailure;
        }
    }

    /// <summary>
    /// Asks the process tree to die, then the direct process, returning true once termination is requested or
    /// the process is confirmed gone, and false only when termination could not be requested and exit could
    /// not be confirmed. Every <see cref="Process.HasExited"/> probe is guarded, so an indeterminate result
    /// after one kill drives the fallback rather than escaping.
    /// </summary>
    private static bool RequestTermination(Process proc)
    {
        if (TryKill(proc, entireProcessTree: true))
        {
            return true;
        }

        if (IsConfirmedExited(proc))
        {
            return true;
        }

        if (TryKill(proc, entireProcessTree: false))
        {
            return true;
        }

        return IsConfirmedExited(proc);
    }

    private static bool TryKill(Process proc, bool entireProcessTree)
    {
        try
        {
            proc.Kill(entireProcessTree);
            return true;
        }
        catch (Exception ex) when (ex is AggregateException or Win32Exception or NotSupportedException or InvalidOperationException)
        {
            // A tree kill can fault (AggregateException), the platform may refuse to walk the tree, or the
            // process may already have exited (InvalidOperationException). Report success only if exit can be
            // positively confirmed, so an indeterminate probe falls through to the fallback instead.
            return IsConfirmedExited(proc);
        }
    }

    /// <summary>
    /// Guarded <see cref="Process.HasExited"/>: true only when exit is positively observed. A probe that
    /// itself throws is treated as "not confirmed", never allowed to escape and abort the cleanup path.
    /// </summary>
    private static bool IsConfirmedExited(Process proc)
    {
        try
        {
            return proc.HasExited;
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            return false;
        }
    }

    private static void CloseStreams(Process proc)
    {
        try
        {
            proc.StandardOutput.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        try
        {
            proc.StandardError.Close();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Reading a killed process's pipe, or a stream closed to unblock it, ends in a broken-pipe or
            // disposed-stream error; the captured output is discarded on the cancellation path, so observe it.
        }
    }
}

/// <summary>The raw result of running <c>gh</c>: its exit code and captured stdout/stderr.</summary>
internal readonly record struct GhResult(int ExitCode, string Stdout, string Stderr);

/// <summary>
/// Signals that the runner could not confirm the launched process was terminated during cancellation cleanup.
/// It is deliberately NOT an <see cref="InvalidOperationException"/>, so the App auth wrapper — which
/// normalises token-mint/config <see cref="InvalidOperationException"/>s to an unavailable read — cannot
/// swallow it. A cleanup failure, like cancellation, must reach the caller untouched.
/// </summary>
internal sealed class GhProcessCleanupException(string message) : Exception(message);
