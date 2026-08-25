namespace Octoshift.Waiting;

/// <summary>Every window across the named hosts, plus the hosts that could not be reached.</summary>
/// <param name="Panes">Windows collected, in host order.</param>
/// <param name="Unreachable">One message per host that failed, already naming the host.</param>
/// <param name="Attempted">How many hosts were asked, so total failure can be told from partial.</param>
internal readonly record struct FleetScan(
    IReadOnlyList<TmuxPane> Panes,
    IReadOnlyList<string> Unreachable,
    int Attempted)
{
    /// <summary>True when nothing at all could be collected. Not the same as an idle fleet.</summary>
    public bool TotalFailure => Panes.Count == 0 && Unreachable.Count == Attempted && Attempted > 0;

    /// <summary>
    /// Collects from every host at once. Concurrent because the hosts are independent and each costs an
    /// ssh round trip: run in sequence, a three-host sweep pays all three latencies before it can answer
    /// a question as simple as "which machine is PR 1234 on".
    /// </summary>
    public static async Task<FleetScan> CollectAsync(IReadOnlyList<string> hosts, CancellationToken ct)
    {
        // No hosts named means this machine.
        IReadOnlyList<string?> targets = hosts.Count > 0 ? [.. hosts] : [null];

        IReadOnlyList<(TmuxPane[]? Panes, string? Failure)> results = await Task.WhenAll(
            targets.Select(async host =>
            {
                try
                {
                    return ((TmuxPane[]?)[.. await new TmuxScanner(host).ScanAsync(ct)], (string?)null);
                }
                catch (TmuxUnavailableException ex)
                {
                    // One unreachable host must not hide the others, and must not be silently absorbed
                    // either — a fleet that is partly invisible looks exactly like a fleet that is quiet.
                    return (null, ex.Message);
                }
            }));

        return new FleetScan(
            [.. results.SelectMany(r => r.Panes ?? [])],
            [.. results.Select(r => r.Failure).OfType<string>()],
            targets.Count);
    }
}
