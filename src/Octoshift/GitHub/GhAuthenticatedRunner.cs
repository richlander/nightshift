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
        Task<string> stdout = proc.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = proc.StandardError.ReadToEndAsync(ct);

        try
        {
            await proc.WaitForExitAsync(ct);
            return new GhResult(proc.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // Disposing the Process alone leaves the gh child — and anything it spawned — running. Take the
            // tree down before the cancellation propagates, so a cancelled token exchange never orphans a
            // process holding a token in its environment.
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
}

/// <summary>The raw result of running <c>gh</c>: its exit code and captured stdout/stderr.</summary>
internal readonly record struct GhResult(int ExitCode, string Stdout, string Stderr);
