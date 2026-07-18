namespace Octoshift.GitHub;

/// <summary>
/// The GitHub-visible actor identity octoshift attributes outbound authority to (for example,
/// <c>nightshift-bot[app]</c>).
/// </summary>
internal readonly record struct GitHubActorIdentity
{
    public GitHubActorIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Actor identity must be non-empty.", nameof(value));
        }

        Value = value;
    }

    /// <summary>The immutable actor identifier written to audit records.</summary>
    public string Value { get; }

    public override string ToString() => Value;
}

/// <summary>Whether a newly issued installation token is the initial mint or a refresh.</summary>
internal enum GitHubTokenAuditKind
{
    Minted,
    Refreshed,
}

/// <summary>An audit row emitted whenever octoshift mints or refreshes an installation token.</summary>
internal readonly record struct GitHubTokenAuditRecord(
    GitHubActorIdentity Actor,
    GitHubTokenAuditKind Kind,
    DateTimeOffset MintedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Sink for token-mint audit records. The writer implementation is provided outside this order's scope.
/// </summary>
internal interface IGitHubTokenAuditSink
{
    /// <summary>Records one token mint/refresh event keyed to the actor identity.</summary>
    ValueTask RecordTokenMintAsync(GitHubTokenAuditRecord record, CancellationToken ct);
}

/// <summary>Default no-op token audit sink used until the single-writer ledger hook is wired.</summary>
internal sealed class NullGitHubTokenAuditSink : IGitHubTokenAuditSink
{
    public static NullGitHubTokenAuditSink Instance { get; } = new();

    private NullGitHubTokenAuditSink()
    {
    }

    public ValueTask RecordTokenMintAsync(GitHubTokenAuditRecord record, CancellationToken ct)
        => ValueTask.CompletedTask;
}
