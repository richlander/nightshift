namespace Octoshift.GitHub;

/// <summary>
/// Outcome of one outbound PR-open attempt.
/// </summary>
internal enum PrOpenOutcomeKind
{
    /// <summary>A new PR was created for the order branch.</summary>
    Opened,

    /// <summary>A PR already exists for the branch (idempotent no-op).</summary>
    AlreadyExists,

    /// <summary>The open attempt failed.</summary>
    Failed,

    /// <summary>Outbound authoring is disabled or unavailable (fail closed).</summary>
    Unavailable,
}

/// <summary>
/// Structured result of one outbound PR-open attempt.
/// </summary>
internal readonly record struct PrOpenOutcome(PrOpenOutcomeKind Kind, int PrNumber = 0);

/// <summary>
/// The injectable seam that performs the authored outbound act of opening a PR.
/// </summary>
internal interface IPrOpenSource
{
    /// <summary>
    /// Opens a PR for <paramref name="headBranch"/> when no PR exists yet.
    /// </summary>
    Task<PrOpenOutcome> OpenAsync(string orderBase, string headBranch, CancellationToken ct);
}
