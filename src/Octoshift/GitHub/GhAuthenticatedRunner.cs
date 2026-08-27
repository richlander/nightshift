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
    /// Starts <paramref name="file"/> with <paramref name="args"/>, drains both output streams while it
    /// runs, and returns its exit code and captured output. On cancellation the whole process tree is taken
    /// down before the cancellation propagates. The program name is a parameter so this can be exercised
    /// against a purpose-built child rather than only the real <c>gh</c> binary.
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

        // Drain both streams concurrently with the wait, because a child that fills its stdout pipe stops
        // making progress — and reading either stream only after exit truncates whatever a burst wrote past
        // the buffer. Starting the reads before the wait is what the event-based BeginOutputReadLine path
        // could not guarantee: WaitForExitAsync can return before the last data-received callback fires.
        //
        // The drains are started without the caller's token on purpose. Cancelling them would abandon the
        // pipes mid-read, and the point of cancellation here is the opposite: to bring the process fully down
        // and account for its output before returning control, never to walk away from a live child.
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(CancellationToken.None);

        try
        {
            await proc.WaitForExitAsync(ct);
            return new GhResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process alone leaves the gh child — and anything it spawned — running, holding an
            // installation token in its environment. Take the whole tree down, then wait (uncancellable) for
            // it to actually terminate and drain its pipes, so the caller only regains control once nothing
            // is left alive. The original cancellation is what propagates.
            await TerminateAndDrainAsync(proc, stdout, stderr);
            throw;
        }
    }

    /// <summary>
    /// Kills the process tree and does not return until it is confirmed dead and both output pipes have
    /// drained. The wait is uncancellable by design: a cancelled caller must not leave a token-bearing tree
    /// running behind it.
    /// </summary>
    private static async Task TerminateAndDrainAsync(Process proc, Task stdout, Task stderr)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The only race we treat as benign: the process exited between the wait unblocking and this
            // kill, so there is nothing left to signal. Any other kill failure (for example a Win32Exception)
            // means the tree may still be alive, and it is allowed to surface rather than be swallowed — the
            // contract is never to return control while a live process remains.
        }

        // Confirmed termination before control returns. If Kill genuinely failed to bring the tree down this
        // does not complete, which is the correct outcome: silently returning past a live child is the bug.
        await proc.WaitForExitAsync(CancellationToken.None);

        // Observe the drains so neither task is left faulted-and-forgotten. Their content is discarded on the
        // cancellation path; a broken pipe from the kill is an expected way for them to end.
        await ObserveDrainAsync(stdout);
        await ObserveDrainAsync(stderr);
    }

    private static async Task ObserveDrainAsync(Task drain)
    {
        try
        {
            await drain;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // Reading a killed process's pipe can end with a broken-pipe or disposed-stream error; on the
            // cancellation path the captured output is discarded anyway, so observe and move on.
        }
    }
}

/// <summary>The raw result of running <c>gh</c>: its exit code and captured stdout/stderr.</summary>
internal readonly record struct GhResult(int ExitCode, string Stdout, string Stderr);
