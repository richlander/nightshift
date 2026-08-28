namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;

/// <summary>
/// Runs an ambient <c>gh</c> subprocess: the caller's own <c>gh</c> authentication, inherited from the
/// process environment (ambient <c>gh</c> credential storage, or an externally provisioned <c>GH_TOKEN</c>),
/// reaches gh untouched. octoshift owns no credential material and injects none — following Git's credential
/// boundary, authority lives in the already-authenticated <c>gh</c> the host provides.
///
/// What this runner keeps is the hardened process lifecycle. Both output streams are drained concurrently
/// with the wait so a burst larger than a pipe buffer is neither truncated nor able to deadlock the child; on
/// ordinary completion the full stdout/stderr and exit code are returned; on cancellation the launched
/// process's exit is confirmed by a bounded wait (a kill only <em>requests</em> termination) and both reads
/// are unblocked and observed before the cancellation propagates, or the runner fails deterministically with
/// <see cref="GhProcessCleanupException"/> rather than returning while the process may still be alive.
/// Descendant containment is best-effort (a tree kill cannot reach a process that outlived the root) and is
/// tracked separately. The program name is a parameter so these facts can be exercised against a purpose-built
/// child rather than only the real <c>gh</c> binary.
/// </summary>
internal static class GhProcessRunner
{
    /// <summary>
    /// Runs ambient <c>gh</c> with <paramref name="args"/>, inheriting the caller's environment and auth. This
    /// is the runner delegate the read-only membrane commands (<c>waiting</c>, <c>pr</c>) hand to
    /// <see cref="GhPrFactsSource"/>.
    /// </summary>
    public static Task<GhResult> RunGhAsync(IReadOnlyList<string> args, CancellationToken ct)
        => RunProcessAsync("gh", args, ct);

    internal static Task<GhResult> RunProcessAsync(
        string file,
        IReadOnlyList<string> args,
        CancellationToken ct)
        => RunProcessAsync(file, args, TryKill, DefaultTerminationConfirmation, ct);

    /// <summary>
    /// Per-attempt ceiling on confirming the launched process actually exited after a kill was requested.
    /// <see cref="Process.Kill(bool)"/> only <em>requests</em> termination and returns immediately, so exit is
    /// confirmed by a bounded wait on the exit task rather than assumed. A normal kill is reaped in
    /// milliseconds, so this ceiling is only ever reached by a genuinely unkillable process, in which case
    /// cleanup fails deterministically instead of hanging.
    /// </summary>
    private static readonly TimeSpan DefaultTerminationConfirmation = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Seam for tests: <paramref name="requestKill"/> performs a kill request for the given tree/direct scope
    /// (production passes <see cref="TryKill"/>), and <paramref name="terminationConfirmation"/> bounds how
    /// long each request is given to be confirmed by an actual exit. Injecting a request that "succeeds"
    /// without killing, together with a short bound, is how the accepted-but-unconfirmed path is exercised
    /// deterministically — rather than trying to make the OS accept a kill that never takes effect.
    /// </summary>
    internal static async Task<GhResult> RunProcessAsync(
        string file,
        IReadOnlyList<string> args,
        Func<Process, bool, bool> requestKill,
        TimeSpan terminationConfirmation,
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

        // No environment overrides: the child inherits octoshift's environment, so ambient `gh` credential
        // storage or an externally provisioned GH_TOKEN reaches gh unchanged. octoshift neither injects nor
        // unsets authentication.

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
            await TerminateAndDrainAsync(proc, exit, completion, stdout, stderr, requestKill, terminationConfirmation).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<GhResult> CompleteAsync(Process proc, Task exit, Task<string> stdout, Task<string> stderr)
    {
        await exit.ConfigureAwait(false);
        return new GhResult(proc.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false));
    }

    /// <summary>
    /// Cancellation cleanup, split by whether the launched process's exit could actually be <em>confirmed</em>
    /// after a kill was requested — not merely whether a kill was requested, since
    /// <see cref="Process.Kill(bool)"/> returns before the process is signalled.
    ///
    /// When exit is confirmed: <paramref name="completion"/> (which begins by awaiting the same exit task) will
    /// finish, so it closes the read streams to unblock any inherited-pipe drain and observes completion and
    /// both drains.
    ///
    /// When exit cannot be confirmed within the bound: the exit task may never finish, so awaiting it — or
    /// completion, which starts by awaiting it — would hang forever and the failure would never surface.
    /// Instead it closes the streams, observes the two drain tasks synchronously (they finish once their
    /// streams are closed), attaches fault-observing continuations to exit and completion so a later fault (for
    /// example when <see cref="Process.Dispose"/> tears the handle down) is never left unobserved, and throws
    /// <see cref="GhProcessCleanupException"/> promptly.
    /// </summary>
    private static async Task TerminateAndDrainAsync(
        Process proc,
        Task exit,
        Task completion,
        Task stdout,
        Task stderr,
        Func<Process, bool, bool> requestKill,
        TimeSpan terminationConfirmation)
    {
        if (await TryConfirmTerminationAsync(proc, exit, requestKill, terminationConfirmation).ConfigureAwait(false))
        {
            // A descendant that outlived the root may still hold a write end, so a drain could hang on EOF;
            // closing the read streams forces them to end. Their captured output is discarded on this path.
            CloseStreams(proc);
            await ObserveAsync(completion).ConfigureAwait(false);
            await ObserveAsync(stdout).ConfigureAwait(false);
            await ObserveAsync(stderr).ConfigureAwait(false);
            return;
        }

        // Exit could not be confirmed within the bound. The exit task (and completion, which awaits it first)
        // may never finish, so we must not await either or cleanup would deadlock and the failure would never
        // surface.
        CloseStreams(proc);
        await ObserveAsync(stdout).ConfigureAwait(false);
        await ObserveAsync(stderr).ConfigureAwait(false);
        ObserveEventually(exit);
        ObserveEventually(completion);

        throw new GhProcessCleanupException(
            "octoshift: could not confirm the gh process exited on cancellation; refusing to wait on a process that may still be alive.");
    }

    /// <summary>
    /// Requests a tree kill and then, if exit is not confirmed within the bound, a direct kill, confirming
    /// each by a bounded wait on the exit task. Returns true only once the process is observed to have exited,
    /// and false when neither request produced a confirmed exit in time — so a kill that is accepted but never
    /// takes effect drives the deterministic cleanup failure rather than an unbounded wait.
    /// </summary>
    private static async Task<bool> TryConfirmTerminationAsync(
        Process proc,
        Task exit,
        Func<Process, bool, bool> requestKill,
        TimeSpan terminationConfirmation)
    {
        _ = requestKill(proc, true);
        if (await ConfirmExitedAsync(exit, terminationConfirmation).ConfigureAwait(false))
        {
            return true;
        }

        _ = requestKill(proc, false);
        return await ConfirmExitedAsync(exit, terminationConfirmation).ConfigureAwait(false);
    }

    /// <summary>
    /// Bounded confirmation that the process exited, by observing the shared exit task with a timeout.
    /// <see cref="Task.WaitAsync(TimeSpan)"/> is a BCL primitive — cross-platform and NativeAOT-safe, no
    /// P/Invoke or reflection — that waits on the task with a ceiling without cancelling or disturbing the
    /// underlying uncancellable wait. A timeout means "not yet confirmed", never an error.
    /// </summary>
    private static async Task<bool> ConfirmExitedAsync(Task exit, TimeSpan timeout)
    {
        try
        {
            await exit.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    /// <summary>
    /// Marks a task's eventual fault as observed without blocking on it, so a task that may complete much later
    /// (or never) cannot surface as an unobserved task exception — including a fault raised when the process
    /// handle is disposed.
    /// </summary>
    private static void ObserveEventually(Task task)
        => task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Requests termination of the process (tree or direct). Returns true when the request was accepted or the
    /// process is already confirmed gone; the caller confirms actual exit separately, so the return value only
    /// reflects that a request could be made, never that the process has died.
    /// </summary>
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
            // positively confirmed, so an indeterminate probe still lets the bounded confirmation decide.
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
