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

    /// <summary>
    /// Bounces an order back for rework via <c>nightshift rework &lt;base&gt; --reason &lt;directive&gt;</c> — the
    /// pure Turnstile write that returns a <c>done</c> order to the pool as <c>changes-requested</c>, keeping
    /// its branch and claim so the re-claiming worker continues the existing branch. Returns true when the
    /// rework succeeded (or was already a no-op). The symmetric sibling of <see cref="LandAsync"/>.
    /// </summary>
    Task<bool> ReworkAsync(string orderBase, string directive, CancellationToken ct);
}
