namespace Turnstile.Tests;

using System.Diagnostics;

/// <summary>
/// Runs the built <c>turnstile</c> product CLI as a genuinely separate OS process and captures its outcome —
/// exit code, stdout, stderr. Two suites need a real process rather than an in-process call: one to commit to
/// a shared SQLite file from outside this process (proving library-mode writes cross the process boundary),
/// and one to assert the load-bearing CLI contract — the exit code and the first-line <c>turnstile:</c> error —
/// end to end. Driving the real binary keeps this process's global <see cref="Console"/> and
/// <see cref="Environment"/> untouched, so nothing here is unsafe under parallel xUnit.
///
/// <para>The product assembly (<c>turnstile.dll</c>) is copied next to the test assembly by the project
/// reference, and is launched through the same dotnet host running the tests (<c>dotnet exec turnstile.dll</c>),
/// so no build layout or configuration is hard-coded.</para>
/// </summary>
internal static class CliProcess
{
    private static readonly string TurnstileDll = Path.Combine(AppContext.BaseDirectory, "turnstile.dll");

    public static async Task<CliResult> RunAsync(
        IReadOnlyDictionary<string, string>? environment,
        CancellationToken ct,
        params string[] args)
    {
        // The dotnet muxer: DOTNET_HOST_PATH is set for child processes of the SDK/test host; fall back to the
        // name on PATH. Re-invoking `dotnet exec <dll>` runs the product entry point regardless of how the test
        // host itself was launched.
        string dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is string host && host.Length > 0
            ? host
            : "dotnet";

        var psi = new ProcessStartInfo(dotnet)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add(TurnstileDll);
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                psi.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = psi };
        process.Start();

        try
        {
            // Read both streams before waiting for exit so a full pipe can never deadlock the child.
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
            Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);

            return new CliResult(process.ExitCode, await stdout, await stderr);
        }
        catch (OperationCanceledException)
        {
            // The test was cancelled while the child was still running: kill it rather than leave an orphaned
            // process behind.
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The process may have exited between the check and the kill; nothing to clean up then.
            }

            throw;
        }
    }
}

/// <summary>The outcome of a <see cref="CliProcess"/> run.</summary>
internal sealed record CliResult(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The first line of stderr, the position the CLI contract's human-readable token occupies.</summary>
    public string FirstStdErrLine => StdErr.Split('\n', 2)[0].TrimEnd('\r');
}
