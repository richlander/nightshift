namespace Octoshift.Coordination;

/// <summary>
/// The injectable seam over the <c>nightshift</c> CLI. Octoshift composes nightshift as a subprocess — it
/// reads the board with <c>where</c> and advances the DAG with <c>land</c> — and never links its assembly.
/// Behind this interface so the reconcile logic can be unit-tested against a fake with no live daemon.
/// </summary>
internal interface INightshiftClient
{
    /// <summary>Reads the coordinator board via <c>nightshift where --output json</c>.</summary>
    Task<BoardState> GetBoardAsync(CancellationToken ct);

    /// <summary>
    /// Lands an order via <c>nightshift land &lt;base&gt; --reason &lt;reason&gt;</c> — the pure Turnstile write
    /// that wakes the plan controller. Returns true when the land succeeded (or was already a no-op).
    /// </summary>
    Task<bool> LandAsync(string orderBase, string reason, CancellationToken ct);
}
