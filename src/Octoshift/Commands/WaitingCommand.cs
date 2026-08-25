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
    public static async Task<int> RunAsync(string? repoFlag, IReadOnlyList<string> hosts, bool all, bool json, CancellationToken ct)
    {
        string? repo = RepoScope.Resolve(repoFlag);
        if (repo is null)
        {
            Console.Error.WriteLine("octoshift: could not resolve a repo scope; pass --repo owner/name.");
            return ExitCode.Usage;
        }

        // No --host means this machine. Named hosts are collected over ssh, one command each, and the
        // GitHub half stays here: putting a collector on every host would mean a cache and a rate-limit
        // budget on every host too, which is the condition this tool exists to remove.
        IReadOnlyList<string?> targets = hosts.Count > 0 ? [.. hosts] : [null];

        var panes = new List<TmuxPane>();
        var unreachable = new List<string>();
        foreach (string? host in targets)
        {
            try
            {
                panes.AddRange(await new TmuxScanner(host).ScanAsync(ct));
            }
            catch (TmuxUnavailableException ex)
            {
                // One unreachable host must not hide the others, and must not be silently absorbed
                // either — a fleet that is partly invisible looks exactly like a fleet that is quiet.
                unreachable.Add(ex.Message);
            }
        }

        // Total failure keeps its own path. A sweep where nothing could be collected is not a quiet fleet,
        // and printing a QUIET summary above the failure inverts which of the two the reader sees first.
        if (panes.Count == 0 && unreachable.Count == targets.Count)
        {
            if (json)
            {
                WriteJsonError(string.Join("; ", unreachable));
            }
            else
            {
                foreach (string failure in unreachable)
                {
                    Console.Error.WriteLine($"octoshift: {failure}");
                }
            }

            return ExitCode.Unavailable;
        }

        var facts = new GhPrFactsSource(
            repo,
            new FileConditionalCache(),
            (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));
        IReadOnlyList<WaitingRow> rows = await BuildRowsAsync(
            panes, facts.FetchAsync, facts.RefreshMergeabilityAsync, DateTimeOffset.UtcNow, all, ct);

        if (json)
        {
            WriteJson(rows, facts, unreachable);
        }
        else
        {
            WriteTable(rows, facts, unreachable);
        }

        return unreachable.Count > 0 ? ExitCode.Unavailable : ExitCode.Ok;
    }

    /// <summary>
    /// Joins panes with GitHub. Injectable fetch so the whole selection and ordering policy is testable
    /// without tmux or a network.
    /// </summary>
    internal static async Task<IReadOnlyList<WaitingRow>> BuildRowsAsync(
        IReadOnlyList<TmuxPane> panes,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset now,
        bool all,
        CancellationToken ct)
    {
        var rows = new List<WaitingRow>();
        var pending = new List<(TmuxPane Pane, AgentState State)>();

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
                // The pane itself is the evidence, and it is unambiguous: a prompt is open.
                rows.Add(Row(pane, record, new WaitingVerdict(
                    WaitingState.NeedsOperator, RowOwner.Operator, "prompt open; awaiting a keystroke", Assurance.High), now));
                continue;
            }

            if (record is null)
            {
                // Neither a published state nor a pr#### window name. Usually an empty shell, so it is
                // available under --all rather than mixed into the default view.
                if (all)
                {
                    rows.Add(Row(pane, null, new WaitingVerdict(
                        WaitingState.Unknown, RowOwner.Nobody, "no published state and no pr#### window name",
                        Assurance.Low("nothing identifies this window")), now));
                }

                continue;
            }

            // An issue-tracking window has no PR, so spending a call on pulls/{n} would ask GitHub about
            // a number that means something else entirely.
            if (!record.IsIssue && !seen.TryGetValue(record.PrNumber, out _))
            {
                seen[record.PrNumber] = await fetchAsync(record.PrNumber, ct);
            }

            pending.Add((pane, record));
        }

        // Second pass for mergeability GitHub had not finished computing. Deliberately after every other
        // PR has been read: the calculation needs a moment, and the time spent on the rest of the fleet
        // is that moment. Re-reading immediately just collects `unknown` a second time.
        foreach (int prNumber in seen.Where(e => e.Value is { MergeabilityKnown: false, Merged: false }).Select(e => e.Key).ToArray())
        {
            if (await refreshMergeabilityAsync(prNumber, ct) is not { } refreshed || !refreshed.MergeabilityKnown)
            {
                continue;
            }

            // Only graft when the PR has not moved between the two reads. Otherwise the answer belongs to
            // a different head, and pairing it with the old snapshot would report the agent's head as
            // mergeable on the strength of a newer one.
            PrFacts current = seen[prNumber]!;
            seen[prNumber] = string.Equals(current.HeadSha, refreshed.HeadSha, StringComparison.OrdinalIgnoreCase)
                ? current with { MergeableState = refreshed.MergeableState }
                : refreshed with { ChecksKnown = false };
        }

        foreach ((TmuxPane pane, AgentState state) in pending)
        {
            rows.Add(Row(pane, state, WaitingVerdict.Resolve(state, state.IsIssue ? null : seen.GetValueOrDefault(state.PrNumber)), now));
        }

        // Longest wait first among the rows that need you: coming back after hours away, the thing that
        // has been stuck longest is the thing that cost the most.
        // The operator's queue first, longest wait at the top: after hours away, the row that has been
        // stuck longest is the one that cost the most.
        return rows
            .Where(r => all || r.Verdict.NeedsAttention || r.Record?.Defects.Count > 0)
            .OrderByDescending(r => r.Verdict.NeedsAttention)
            .ThenBy(r => r.Verdict.Severity)
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

    private static void WriteTable(IReadOnlyList<WaitingRow> rows, GhPrFactsSource facts, IReadOnlyList<string> unreachable)
    {
        int attention = rows.Count(r => r.Verdict.NeedsAttention);
        Console.WriteLine(attention > 0
            ? $"ATTENTION {attention} of {rows.Count} window(s) need you"
            : $"QUIET {rows.Count} window(s), none need you");

        // Said on every run, including when the number is zero. A tool that speaks to agents only when it
        // is sure has to be legible about when it was not, or "it did nothing" and "it saw nothing" look
        // the same from here.
        int actionable = rows.Count(r => r.Verdict.MayAct);
        int unsure = rows.Count(r => !r.Verdict.Assurance.MayAct);
        Console.WriteLine($"NOT ACTED nothing was sent to any agent; {actionable} row(s) met the bar to act, {unsure} did not");

        if (rows.Count > 0)
        {
            Console.WriteLine();
            // Built by Add: inside a collection initializer, `[...]` parses as an indexed element.
            var table = new List<string[]>();
            table.Add(["WINDOW", "PR", "STATE", "CONF", "FOR", "DETAIL"]);
            foreach (WaitingRow row in rows)
            {
                table.Add([
                    row.Pane.Where + (row.Pane.WindowName.Length > 0 ? $" {row.Pane.WindowName}" : string.Empty),
                    row.Record is null ? "-" : $"#{row.Record.PrNumber}{(row.Record.Source == StateSource.WindowName ? "~" : string.Empty)}",
                    row.Verdict.State.ToString().ToUpperInvariant(),
                    row.Verdict.Assurance.Label,
                    Duration(row.StoppedFor),
                    Detail(row),
                ]);
            }

            WriteAligned(table);
        }

        Console.WriteLine();
        Console.WriteLine(Budget(facts));
        foreach (string failure in unreachable)
        {
            Console.WriteLine($"UNREACHABLE {failure}");
        }
    }

    private static string Detail(WaitingRow row)
    {
        string detail = row.Verdict.Reason;
        if (row.Verdict.Assurance.Caveat is { Length: > 0 } caveat)
        {
            // Say what would have been needed, not merely that the tool was unsure.
            detail += $"  (~ {caveat})";
        }

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

    /// <summary>Emits a machine-readable failure so <c>--json</c> never returns non-JSON.</summary>
    private static void WriteJsonError(string message)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("error", message);
        writer.WriteStartArray("rows");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }

    private static void WriteJson(IReadOnlyList<WaitingRow> rows, GhPrFactsSource facts, IReadOnlyList<string> unreachable)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("calls", facts.Calls);
        writer.WriteNumber("notModified", facts.NotModified);
        writer.WriteNumber("mergeabilityRereads", facts.Recomputed);
        writer.WriteBoolean("rateLimited", facts.RateLimited);
        writer.WriteStartArray("unreachable");
        foreach (string failure in unreachable)
        {
            writer.WriteStringValue(failure);
        }

        writer.WriteEndArray();
        if (facts.RateLimitRemaining is { } remaining)
        {
            writer.WriteNumber("rateLimitRemaining", remaining);
        }

        writer.WriteStartArray("rows");
        foreach (WaitingRow row in rows)
        {
            writer.WriteStartObject();
            writer.WriteString("target", row.Pane.Target);
            if (row.Pane.Host is { } host)
            {
                writer.WriteString("host", host);
            }

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
            writer.WriteString("confidence", row.Verdict.Assurance.Label);
            if (row.Verdict.Assurance.Caveat is { } caveat)
            {
                writer.WriteString("caveat", caveat);
            }

            writer.WriteBoolean("mayAct", row.Verdict.MayAct);
            writer.WriteBoolean("acted", false);
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
