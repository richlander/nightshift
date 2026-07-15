namespace Octoshift;

/// <summary>
/// Octoshift's exit-code contract, mirroring Nightshift's discipline: a spawning harness can branch on the
/// process status without parsing stdout. Human-readable tokens (<c>LANDED</c>, the interval notes) are
/// still printed alongside, but the code is the machine signal. Values stay below 126 to avoid the shell's
/// reserved range.
/// </summary>
internal static class ExitCode
{
    /// <summary>Normal success: the sweep completed, or the resident loop exited cleanly on Ctrl-C.</summary>
    public const int Ok = 0;

    /// <summary>Malformed invocation, or a repo scope that could not be resolved.</summary>
    public const int Usage = 2;

    /// <summary>Interrupted by Ctrl-C (128 + SIGINT), the conventional signal exit code.</summary>
    public const int Interrupted = 130;
}
