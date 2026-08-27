namespace Octoshift;

/// <summary>
/// Octoshift's exit-code contract: a spawning harness can branch on the process status without parsing
/// stdout. Human-readable tokens (<c>FLEET</c>, and the waiting/pr report lines) are still printed
/// alongside, but the code is the machine signal. Values stay below 126 to avoid the shell's reserved
/// range.
/// </summary>
internal static class ExitCode
{
    /// <summary>Normal success: the command completed, or a resident view exited cleanly on Ctrl-C.</summary>
    public const int Ok = 0;

    /// <summary>Malformed invocation, or a repo scope that could not be resolved.</summary>
    public const int Usage = 2;

    /// <summary>A dependency the command needs — tmux, a socket — could not be reached.</summary>
    public const int Unavailable = 3;

    /// <summary>Interrupted by Ctrl-C (128 + SIGINT), the conventional signal exit code.</summary>
    public const int Interrupted = 130;
}
