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
    /// runs, and returns its exit code and captured output. On Unix the child runs inside a new process
    /// group that is torn down — with every descendant it spawned — before this returns, so a cancelled
    /// token exchange never orphans a process holding a token in its environment; see
    /// <see cref="ContainedProcess"/>. On Windows the containment is a best-effort
    /// <see cref="Process.Kill(bool)"/> tree kill. The program name is a parameter so this can be exercised
    /// against a purpose-built child rather than only the real <c>gh</c> binary.
    /// </summary>
    internal static Task<GhResult> RunProcessAsync(
        string file,
        IReadOnlyList<string> args,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        CancellationToken ct)
        => OperatingSystem.IsWindows()
            ? RunWithProcessAsync(file, args, environmentOverrides, ct)
            : ContainedProcess.RunAsync(file, args, environmentOverrides, ct);

    /// <summary>
    /// Windows fallback. There is no process-group boundary here: the tree is taken down with a best-effort
    /// <see cref="Process.Kill(bool)"/> snapshot, which cannot reach a descendant once the root has exited.
    /// The reads stay cancellable so a descendant that inherited a pipe cannot hang the drain.
    /// </summary>
    private static async Task<GhResult> RunWithProcessAsync(
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

        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
            return new GhResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or Win32Exception)
            {
                // Already gone, or the platform will not walk the tree; nothing further to do.
            }

            // Confirm at least the direct process is dead before returning control — #92's minimum contract is
            // that a cancelled runner never leaves the token-bearing gh process itself alive. Descendant
            // containment is best-effort here: the tree kill above is a snapshot that cannot reach a
            // descendant once the root has exited (a durable job-object boundary is tracked separately for
            // Windows).
            await proc.WaitForExitAsync(CancellationToken.None);
            await ObserveDrainAsync(stdout);
            await ObserveDrainAsync(stderr);
            throw;
        }
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
