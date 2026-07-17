namespace Octoshift.GitHub;

internal enum IssueState
{
    Unknown,
    Open,
    Closed,
}

internal enum IssueCloseOutcome
{
    Failed,
    Closed,
    AlreadyClosed,
}

/// <summary>
/// The injectable seam over GitHub issue reads/writes needed for §4.3 fan-out close.
/// </summary>
internal interface IIssueClient
{
    /// <summary>Reads an issue's current state.</summary>
    Task<IssueState> GetIssueStateAsync(int issueNumber, CancellationToken ct);

    /// <summary>Closes an issue with a short pointer comment to the orders that fulfilled it.</summary>
    Task<IssueCloseOutcome> CloseIssueAsync(int issueNumber, string comment, CancellationToken ct);
}
