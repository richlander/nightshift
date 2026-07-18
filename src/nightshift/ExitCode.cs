namespace Nightshift;

/// <summary>
/// The exit-code contract for the agent-facing verbs. A spawned agent's shell loop branches on these
/// WITHOUT parsing stdout: the process status alone tells it whether it got work, should wait, or must
/// stop. Human-readable tokens (WORK, NOWORK, OK, HALT, ...) are still printed alongside, but the code is
/// the machine signal. Values stay below 126 to avoid clashing with the shell's own reserved range.
/// </summary>
internal static class ExitCode
{
    /// <summary>Normal success: work claimed (<c>next</c>), heartbeat fine (<c>check</c>), released, joined, ...</summary>
    public const int Ok = 0;

    /// <summary>Malformed invocation — bad or missing arguments.</summary>
    public const int Usage = 2;

    /// <summary>No active claim: <c>check</c>/<c>release</c> were called with no order in hand.</summary>
    public const int NoClaim = 3;

    /// <summary><c>next</c>: one-shot probe found no claimable order, or a bounded wait timed out.</summary>
    public const int NoWork = 10;

    /// <summary><c>next</c>: the shift is draining — stop asking for work and wind down.</summary>
    public const int Draining = 11;

    /// <summary><c>check</c>: a directive is waiting on the order (printed to stdout) — answer it.</summary>
    public const int Query = 12;

    /// <summary><c>check</c>: an operator halt is in force — stop working and release.</summary>
    public const int Halt = 13;

    /// <summary><c>check</c>: the claim was lost or expired — abandon this order and re-acquire.</summary>
    public const int FenceStale = 14;

    /// <summary><c>coordinate</c>: returned one coordinator-actionable transition (token: <c>COORD</c>).</summary>
    public const int Coordinate = 20;

    /// <summary><c>coordinate</c>: one-shot probe found no actionable transition, or a bounded wait timed out.</summary>
    public const int NoCoordinate = 21;

    /// <summary>Interrupted by Ctrl-C (128 + SIGINT), the conventional signal exit code.</summary>
    public const int Interrupted = 130;
}
