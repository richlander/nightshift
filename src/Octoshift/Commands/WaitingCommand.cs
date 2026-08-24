namespace Octoshift.Commands;

using System.Text;
using System.Text.Json;
using Octoshift.GitHub;
using Octoshift.Waiting;

/// <summary>One stopped pane, what it said, and what GitHub says about the PR it named.</summary>
internal sealed record WaitingRow
{
    public required TmuxPane Pane { get; init; }

    /// <summary>Null when nothing — neither option nor window name — identified a PR.</summary>
    public AgentState? Record { get; init; }

    public required WaitingVerdict Verdict { get; init; }

    /// <summary>How long the pane has been quiet, from tmux's own activity clock.</summary>
    public TimeSpan? StoppedFor { get; init; }
}

/// <summary>
/// Finds agents that have stopped and tells you what is actually blocking each one.
/// </summary>
/// <remarks>
/// The gap this closes: an agent finishes a round, prints its conclusion, and stops. Everything it knows
/// is now in a terminal pane, and everything it does not know — whether CI went green, whether the branch
/// still merges — is on GitHub. Nobody joins the two until a human opens the window, which can be hours.
/// This reads both and reports the join, and it never sends anything: releasing an agent is a separate,
/// opt-in step.
/// </remarks>
internal static class WaitingCommand
{
    public static async Task<int> RunAsync(string? repoFlag, bool all, bool json, CancellationToken ct)
    {
        string? repo = RepoScope.Resolve(repoFlag);
        if (repo is null)
        {
            Console.Error.WriteLine("octoshift: could not resolve a repo scope; pass --repo owner/name.");
            return ExitCode.Usage;
        }

        var scanner = new TmuxScanner();
        IReadOnlyList<TmuxPane> panes = await scanner.ScanAsync(ct);
        if (panes.Count == 0)
        {
            Console.WriteLine("QUIET no tmux windows found");
            return ExitCode.Ok;
        }

        // The operator's own gh auth, on purpose: this is a hand-run report, and the App installation
        // token is a separate bucket reserved for the resident daemon so it does not compete with agents.
        var facts = new GhPrFactsSource(
            repo,
            new FileConditionalCache(),
            (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));
        IReadOnlyList<WaitingRow> rows = await BuildRowsAsync(panes, facts.FetchAsync, DateTimeOffset.UtcNow, all, ct);

        if (json)
        {
            WriteJson(rows, facts);
        }
        else
        {
            WriteTable(rows, facts);
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// Joins panes with GitHub. Injectable fetch so the whole selection and ordering policy is testable
    /// without tmux or a network.
    /// </summary>
    internal static async Task<IReadOnlyList<WaitingRow>> BuildRowsAsync(
        IReadOnlyList<TmuxPane> panes,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
        DateTimeOffset now,
        bool all,
        CancellationToken ct)
    {
        var rows = new List<WaitingRow>();

        // One fetch per PR, not per pane: #159 measured PRs claimed by two windows at once, and the
        // second window's question has the same answer as the first's.
        var seen = new Dictionary<int, PrFacts?>();

        foreach (TmuxPane pane in panes)
        {
            // A pane mid-turn has not handed anything over; there is nothing to resolve and nothing to do.
            if (pane.Activity == PaneActivity.Working)
            {
                continue;
            }

            AgentState? record = AgentState.Parse(pane.AgentStateOption, pane.WindowName);

            if (pane.Activity == PaneActivity.Blocked)
            {
                // A held-open prompt is answered with a keystroke, not with a GitHub lookup.
                rows.Add(Row(pane, record, new WaitingVerdict(
                    WaitingState.NeedsOperator, RowOwner.Operator, "prompt open; awaiting a keystroke"), now));
                continue;
            }

            if (record is null)
            {
                // Neither a published state nor a pr#### window name. Usually an empty shell, so it is
                // available under --all rather than mixed into the default view.
                if (all)
                {
                    rows.Add(Row(pane, null, new WaitingVerdict(
                        WaitingState.Unknown, RowOwner.Nobody, "no published state and no pr#### window name"), now));
                }

                continue;
            }

            if (!seen.TryGetValue(record.PrNumber, out PrFacts? prFacts))
            {
                prFacts = await fetchAsync(record.PrNumber, ct);
                seen[record.PrNumber] = prFacts;
            }

            rows.Add(Row(pane, record, WaitingVerdict.Resolve(record, prFacts), now));
        }

        // Longest wait first among the rows that need you: coming back after hours away, the thing that
        // has been stuck longest is the thing that cost the most.
        // The operator's queue first, longest wait at the top: after hours away, the row that has been
        // stuck longest is the one that cost the most.
        return rows
            .Where(r => all || r.Verdict.NeedsAttention || r.Record?.Defects.Count > 0)
            .OrderByDescending(r => r.Verdict.NeedsAttention)
            .ThenByDescending(r => r.StoppedFor ?? TimeSpan.Zero)
            .ToArray();
    }

    private static WaitingRow Row(TmuxPane pane, AgentState? record, WaitingVerdict verdict, DateTimeOffset now)
        => new()
        {
            Pane = pane,
            Record = record,
            Verdict = verdict,
            StoppedFor = pane.LastActivity is { } at && at <= now ? now - at : null,
        };

    private static void WriteTable(IReadOnlyList<WaitingRow> rows, GhPrFactsSource facts)
    {
        int attention = rows.Count(r => r.Verdict.NeedsAttention);
        Console.WriteLine(attention > 0
            ? $"ATTENTION {attention} of {rows.Count} window(s) need you"
            : $"QUIET {rows.Count} window(s), none need you");

        if (rows.Count > 0)
        {
            Console.WriteLine();
            // Built by Add: inside a collection initializer, `[...]` parses as an indexed element.
            var table = new List<string[]>();
            table.Add(["WINDOW", "PR", "STATE", "FOR", "DETAIL"]);
            foreach (WaitingRow row in rows)
            {
                table.Add([
                    row.Pane.Target + (row.Pane.WindowName.Length > 0 ? $" {row.Pane.WindowName}" : string.Empty),
                    row.Record is null ? "-" : $"#{row.Record.PrNumber}{(row.Record.Source == StateSource.WindowName ? "~" : string.Empty)}",
                    row.Verdict.State.ToString().ToUpperInvariant(),
                    Duration(row.StoppedFor),
                    Detail(row),
                ]);
            }

            WriteAligned(table);
        }

        Console.WriteLine();
        Console.WriteLine(Budget(facts));
    }

    private static string Detail(WaitingRow row)
    {
        string detail = row.Verdict.Reason;
        if (row.Record?.Defects is { Count: > 0 } defects)
        {
            // Reported, never repaired: a state that contradicts itself is a signal about the agent.
            detail += "  [!] " + string.Join("; ", defects);
        }

        return detail;
    }

    private static string Budget(GhPrFactsSource facts)
    {
        var note = new StringBuilder($"{facts.Calls} REST call(s), {facts.NotModified} free (304)");
        if (facts.Recomputed > 0)
        {
            note.Append($", {facts.Recomputed} mergeability re-read");
        }

        if (facts.RateLimitRemaining is { } remaining)
        {
            note.Append($", {remaining} remaining");
        }

        if (facts.RateLimited)
        {
            note.Append(" — RATE LIMITED");
        }

        return note.ToString();
    }

    private static void WriteAligned(IReadOnlyList<string[]> table)
    {
        int columns = table[0].Length;
        var widths = new int[columns];
        foreach (string[] cells in table)
        {
            for (int i = 0; i < columns; i++)
            {
                widths[i] = Math.Max(widths[i], cells[i].Length);
            }
        }

        foreach (string[] cells in table)
        {
            var line = new StringBuilder();
            for (int i = 0; i < columns; i++)
            {
                // The last column is free-form, so it is never padded.
                line.Append(i == columns - 1 ? cells[i] : cells[i].PadRight(widths[i] + 2));
            }

            Console.WriteLine(line.ToString().TrimEnd());
        }
    }

    private static string Duration(TimeSpan? span) => span switch
    {
        null => "-",
        { TotalMinutes: < 1 } => "<1m",
        { TotalHours: < 1 } value => $"{(int)value.TotalMinutes}m",
        { TotalDays: < 1 } value => $"{(int)value.TotalHours}h{value.Minutes:00}m",
        { } value => $"{(int)value.TotalDays}d{value.Hours:00}h",
    };

    private static void WriteJson(IReadOnlyList<WaitingRow> rows, GhPrFactsSource facts)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("calls", facts.Calls);
        writer.WriteNumber("notModified", facts.NotModified);
        if (facts.RateLimitRemaining is { } remaining)
        {
            writer.WriteNumber("rateLimitRemaining", remaining);
        }

        writer.WriteStartArray("rows");
        foreach (WaitingRow row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("target", row.Pane.Target);
            writer.WriteString("window", row.Pane.WindowName);
            writer.WriteBoolean("attached", row.Pane.SessionAttached);
            if (row.Record is { } record)
            {
                writer.WriteNumber("pr", record.PrNumber);
                writer.WriteString("source", record.Source.ToString().ToLowerInvariant());
                writer.WriteString("rec", record.Recommendation.ToString().ToLowerInvariant());
                if (record.ReviewsRequired is { } required)
                {
                    writer.WriteString("reviews", $"{record.ReviewsClean ?? 0}/{required}");
                }

                if (record.Head is { } head)
                {
                    writer.WriteString("head", head);
                }

                writer.WriteStartArray("blocked");
                foreach (int b in record.Blocked)
                {
                    writer.WriteNumberValue(b);
                }

                writer.WriteEndArray();
                writer.WriteStartArray("defects");
                foreach (string d in record.Defects)
                {
                    writer.WriteStringValue(d);
                }

                writer.WriteEndArray();
            }

            writer.WriteString("state", row.Verdict.State.ToString().ToLowerInvariant());
            writer.WriteString("owner", row.Verdict.Owner.ToString().ToLowerInvariant());
            writer.WriteString("reason", row.Verdict.Reason);
            if (row.StoppedFor is { } stopped)
            {
                writer.WriteNumber("stoppedForSeconds", (long)stopped.TotalSeconds);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
}
