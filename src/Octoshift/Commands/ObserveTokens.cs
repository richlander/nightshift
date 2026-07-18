namespace Octoshift.Commands;

/// <summary>
/// First-line/event tokens for read-only observation verbs. Tokens are additive and distinct from
/// <c>reconcile</c>'s mutation tokens (<c>LANDED</c>, <c>REWORK</c>, <c>CLOSED</c>).
/// </summary>
internal static class ObserveTokens
{
    /// <summary>An order PR is currently open and mergeable/unknown (non-terminal).</summary>
    public const string Open = "OPEN";

    /// <summary>An order PR is open but merge-conflicting (terminal for <c>wait</c>).</summary>
    public const string Conflict = "CONFLICT";

    /// <summary>An order PR closed without merging (terminal for <c>wait</c>).</summary>
    public const string Closed = "CLOSED";

    /// <summary>An order PR merged (terminal for <c>wait</c>).</summary>
    public const string Merged = "MERGED";

    /// <summary><c>wait --all</c> reached a terminal state for every observed PR in scope.</summary>
    public const string AllResolved = "ALL_RESOLVED";
}
