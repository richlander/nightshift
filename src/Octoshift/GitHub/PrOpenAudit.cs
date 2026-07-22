namespace Octoshift.GitHub;

/// <summary>
/// One outbound PR-open audit row.
/// </summary>
internal readonly record struct PrOpenedAuditRecord(
    GitHubActorIdentity Actor,
    string OrderBase,
    string HeadBranch,
    int PrNumber,
    DateTimeOffset OpenedAt);

/// <summary>
/// Sink for outbound PR-open audit rows. The durable single-writer ledger wiring is out of scope here.
/// </summary>
internal interface IPrOpenAuditSink
{
    /// <summary>Records one successful outbound PR-open action.</summary>
    ValueTask RecordPrOpenedAsync(PrOpenedAuditRecord record, CancellationToken ct);
}

/// <summary>
/// Default no-op outbound PR-open audit sink.
/// </summary>
internal sealed class NullPrOpenAuditSink : IPrOpenAuditSink
{
    public static NullPrOpenAuditSink Instance { get; } = new();

    private NullPrOpenAuditSink()
    {
    }

    public ValueTask RecordPrOpenedAsync(PrOpenedAuditRecord record, CancellationToken ct)
        => ValueTask.CompletedTask;
}
