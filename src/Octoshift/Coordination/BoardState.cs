namespace Octoshift.Coordination;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// A read-only view of the coordinator board as octoshift sees it — the parsed rows of
/// <c>nightshift where --output json</c>. Octoshift reads status this way (a subprocess call), never by
/// touching Turnstile, so it links nothing. Used for two things: the idempotent check-before-land (an order
/// already <c>landed</c> is a no-op) and the board-aware fast path (an order at <c>done</c> means a merge is
/// imminent, so the poller pins to the floor while awaiting merge proof from the PR feed).
/// </summary>
internal sealed class BoardState
{
    private const string LandedStatus = "landed";
    private const string DoneStatus = "done";

    private readonly Dictionary<string, string> _statuses;

    private BoardState(Dictionary<string, string> statuses) => _statuses = statuses;

    /// <summary>An empty board — the shape when <c>where</c> reports no orders yet.</summary>
    public static BoardState Empty { get; } = new(new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>Builds a board from parsed <c>where</c> rows, indexing each order base to its status.</summary>
    public static BoardState FromRows(IEnumerable<BoardRow> rows)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (BoardRow row in rows)
        {
            if (!string.IsNullOrEmpty(row.OrderBase) && !string.IsNullOrEmpty(row.Status))
            {
                statuses[row.OrderBase] = row.Status;
            }
        }

        return new BoardState(statuses);
    }

    /// <summary>Parses the JSON array emitted by <c>nightshift where --output json</c>; empty on any malformed input.</summary>
    public static BoardState Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            BoardRow[]? rows = JsonSerializer.Deserialize(json, BoardJsonContext.Default.BoardRowArray);
            return rows is null ? Empty : FromRows(rows);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>True when the order is already <c>landed</c> — landing it again is a no-op.</summary>
    public bool IsLanded(string orderBase)
        => _statuses.TryGetValue(orderBase, out string? status) && status == LandedStatus;

    /// <summary>
    /// True when the order is at <c>done</c> — submitted and awaiting merge. The rework sweep's idempotency
    /// gate: only a <c>done</c> order is eligible to bounce, so an already-<c>changes-requested</c> or
    /// <c>landed</c> order is never re-bounced.
    /// </summary>
    public bool IsDone(string orderBase)
        => _statuses.TryGetValue(orderBase, out string? status) && status == DoneStatus;

    /// <summary>
    /// True when some order is at <c>done</c> (a worker submitted a PR, awaiting merge): this only tightens poll cadence.
    /// </summary>
    public bool HasOutstandingDone => _statuses.Values.Any(status => status == DoneStatus);

    /// <summary>
    /// The order bases currently at <c>done</c>, sorted ordinally for deterministic routing.
    /// </summary>
    public IReadOnlyList<string> GetDoneOrderBases()
    {
        var done = new List<string>();
        foreach ((string orderBase, string status) in _statuses)
        {
            if (status == DoneStatus)
            {
                done.Add(orderBase);
            }
        }

        done.Sort(StringComparer.Ordinal);
        return done;
    }
}

/// <summary>One row of the <c>where</c> board: the order base, its status, and its branch (snake_case JSON).</summary>
internal sealed record BoardRow
{
    [JsonPropertyName("order_base")]
    public string? OrderBase { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(BoardRow[]))]
internal partial class BoardJsonContext : JsonSerializerContext
{
}
