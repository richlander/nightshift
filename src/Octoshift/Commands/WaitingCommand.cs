namespace Octoshift.Commands;

using System.Text;
using System.Text.Json;
using Octoshift.GitHub;
using Octoshift.Waiting;

/// <summary>One stopped pane, what it said, and what GitHub says about the PR it named.</summary>
internal sealed record WaitingRow
{
    public required TmuxPane Pane { get; init; }

    /// <summary>
    /// Null when nothing — neither option nor window name — identified a PR. A window that published
    /// something anyway is carried by <see cref="Unidentified"/> instead.
    /// </summary>
    public AgentState? Record { get; init; }

    /// <summary>
    /// What the window published when it identified nothing. Kept beside <see cref="Record"/> rather than
    /// folded into it: there is no number to write here, and inventing one would put a PR that does not
    /// exist into every column and field that carries a real one.
    /// </summary>
    public UnidentifiedState? Unidentified { get; init; }

    public required WaitingVerdict Verdict { get; init; }

    /// <summary>How long the pane has been quiet, from tmux's own activity clock.</summary>
    public TimeSpan? StoppedFor { get; init; }

    /// <summary>Ways whatever was published contradicts its own contract, identified or not.</summary>
    public IReadOnlyList<string> Defects => Record?.Defects ?? Unidentified?.Defects ?? [];
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
        //
        // Validated again here rather than trusted from the parser, because every value becomes an ssh
        // argument and an option-shaped one succeeds quietly.
        foreach (string host in hosts)
        {
            if (HostTarget.Validate(host) is { } invalid)
            {
                Console.Error.WriteLine($"octoshift: {invalid}");
                return ExitCode.Usage;
            }
        }

        Collection collected = await CollectAsync(hosts, (host, token) => new TmuxScanner(host).ScanAsync(token), ct);

        // Total failure keeps its own path. A sweep where nothing could be collected is not a quiet fleet,
        // and printing a QUIET summary above the failure inverts which of the two the reader sees first.
        if (collected.TotalFailure)
        {
            if (json)
            {
                WriteJsonError(Console.OpenStandardOutput(), string.Join("; ", collected.Unreachable));
            }
            else
            {
                foreach (string failure in collected.Unreachable)
                {
                    Console.Error.WriteLine($"octoshift: {DisplayText.Safe(failure)}");
                }
            }

            return ExitCode.Unavailable;
        }

        var facts = new GhPrFactsSource(
            repo,
            new FileConditionalCache(),
            (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));
        IReadOnlyList<WaitingRow> rows = await BuildRowsAsync(
            collected.Panes, facts.FetchAsync, facts.RefreshMergeabilityAsync, DateTimeOffset.UtcNow, all, ct);

        if (json)
        {
            WriteJson(Console.OpenStandardOutput(), rows, Budget.From(facts), collected.Unreachable);
        }
        else
        {
            WriteTable(Console.Out, rows, Budget.From(facts), collected.Unreachable);
        }

        // A partly invisible fleet is not a clean sweep, so a single failed host still costs the exit
        // code even though every other host's rows were printed.
        return collected.AnyFailure ? ExitCode.Unavailable : ExitCode.Ok;
    }

    /// <summary>What one sweep managed to collect, and from how many targets it tried.</summary>
    internal readonly record struct Collection(IReadOnlyList<TmuxPane> Panes, IReadOnlyList<string> Unreachable, int Targets)
    {
        /// <summary>Nothing was collected anywhere. Reported as a failure, never as a quiet fleet.</summary>
        public bool TotalFailure => Panes.Count == 0 && Unreachable.Count == Targets;

        /// <summary>At least one target could not be read, whatever the others returned.</summary>
        public bool AnyFailure => Unreachable.Count > 0;
    }

    /// <summary>
    /// Collects from each distinct target in turn. Injectable scan so fan-out, deduplication and partial
    /// failure are testable without ssh or a tmux server.
    /// </summary>
    /// <remarks>
    /// Repeats are dropped in first-seen order: naming an alias twice is a typo, and honouring it would
    /// buy a second ssh connection and a duplicate of every row and count that host contributes.
    /// </remarks>
    internal static async Task<Collection> CollectAsync(
        IReadOnlyList<string> hosts,
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanAsync,
        CancellationToken ct)
    {
        IReadOnlyList<string?> targets = hosts.Count > 0 ? [.. HostTarget.Distinct(hosts)] : [null];

        var panes = new List<TmuxPane>();
        var unreachable = new List<string>();
        foreach (string? host in targets)
        {
            try
            {
                panes.AddRange(await scanAsync(host, ct));
            }
            catch (TmuxUnavailableException ex)
            {
                // One unreachable host must not hide the others, and must not be silently absorbed
                // either — a fleet that is partly invisible looks exactly like a fleet that is quiet.
                unreachable.Add(ex.Message);
            }
        }

        return new Collection(panes, unreachable, targets.Count);
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

            StateReading reading = AgentState.Read(pane.AgentStateOption, pane.WindowName);
            AgentState? record = reading.Identified;

            // A pane nobody could read is reported, never resolved. Its window options came from the
            // manifest and are trustworthy, but whether the agent is mid-turn is exactly what the capture
            // was for — so this must not fall through to the idle path, where a published record is taken
            // as a handover and can reach a high-confidence, actionable verdict on unread evidence.
            if (pane.Activity == PaneActivity.Unreadable)
            {
                rows.Add(Row(pane, reading, new WaitingVerdict(
                    WaitingState.Unknown, RowOwner.Operator, "pane could not be captured; its state is unread",
                    Assurance.Low("the pane could not be read")), now));
                continue;
            }

            if (pane.Activity == PaneActivity.Blocked)
            {
                // A held-open prompt is answered with a keystroke, not with a GitHub lookup.
                // The pane itself is the evidence, and it is unambiguous: a prompt is open.
                rows.Add(Row(pane, reading, new WaitingVerdict(
                    WaitingState.NeedsOperator, RowOwner.Operator, "prompt open; awaiting a keystroke", Assurance.High), now));
                continue;
            }

            if (record is null)
            {
                if (reading.Unidentified is { } unusable)
                {
                    // The window published something and it names nothing this reader can look up. There
                    // is no number to fetch — asking GitHub would mean inventing one — so the row is
                    // resolved here, kept in the default view, and left unactionable. Dropping it is how
                    // `rec=stop` in a window named `worker` became a report of a quiet fleet.
                    rows.Add(Row(pane, reading, WaitingVerdict.Unidentified(unusable), now));
                    continue;
                }

                // Neither a published state nor a pr#### window name. Usually an empty shell, so it is
                // available under --all rather than mixed into the default view.
                if (all)
                {
                    rows.Add(Row(pane, reading, new WaitingVerdict(
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
            rows.Add(Row(
                pane,
                StateReading.For(state),
                WaitingVerdict.Resolve(state, state.IsIssue ? null : seen.GetValueOrDefault(state.PrNumber)),
                now));
        }

        // Longest wait first among the rows that need you: coming back after hours away, the thing that
        // has been stuck longest is the thing that cost the most.
        // The operator's queue first, longest wait at the top: after hours away, the row that has been
        // stuck longest is the one that cost the most.
        return rows
            .Where(r => all || r.Verdict.NeedsAttention || r.Defects.Count > 0)
            .OrderByDescending(r => r.Verdict.NeedsAttention)
            .ThenBy(r => r.Verdict.Severity)
            .ThenByDescending(r => r.StoppedFor ?? TimeSpan.Zero)
            .ToArray();
    }

    private static WaitingRow Row(TmuxPane pane, StateReading reading, WaitingVerdict verdict, DateTimeOffset now)
        => new()
        {
            Pane = pane,
            Record = reading.Identified,
            Unidentified = reading.Unidentified,
            Verdict = verdict,
            StoppedFor = pane.LastActivity is { } at && at <= now ? now - at : null,
        };

    /// <summary>
    /// What one sweep cost GitHub, lifted off the source that spent it so the report can be written — and
    /// tested — without a live fetcher.
    /// </summary>
    internal readonly record struct Budget(int Calls, int NotModified, int Recomputed, int? RateLimitRemaining, bool RateLimited)
    {
        public static Budget From(GhPrFactsSource facts)
            => new(facts.Calls, facts.NotModified, facts.Recomputed, facts.RateLimitRemaining, facts.RateLimited);

        public override string ToString()
        {
            var note = new StringBuilder($"{Calls} REST call(s), {NotModified} free (304)");
            if (Recomputed > 0)
            {
                note.Append($", {Recomputed} mergeability re-read");
            }

            if (RateLimitRemaining is { } remaining)
            {
                note.Append($", {remaining} remaining");
            }

            if (RateLimited)
            {
                note.Append(" — RATE LIMITED");
            }

            return note.ToString();
        }
    }

    /// <summary>
    /// The first line: what this sweep saw, said so that it cannot overstate its own coverage.
    /// </summary>
    /// <remarks>
    /// QUIET is a claim about the fleet, not about the rows that happened to arrive, so it may only be
    /// made when the whole fleet was read. With a host unreachable the honest lead is that the sweep is
    /// partial — otherwise the one shape that matters most, a host that has gone dark while its windows
    /// sit finished, prints the word QUIET on the first line and the reason on the last. The
    /// <c>UNREACHABLE</c> lines and the exit code already said it; the summary was the only part that
    /// did not.
    /// </remarks>
    internal static string Summary(IReadOnlyList<WaitingRow> rows, IReadOnlyList<string> unreachable)
    {
        int attention = rows.Count(r => r.Verdict.NeedsAttention);
        if (unreachable.Count > 0)
        {
            return $"PARTIAL {unreachable.Count} host(s) unreachable; {attention} of {rows.Count} visible window(s) need you";
        }

        return attention > 0
            ? $"ATTENTION {attention} of {rows.Count} window(s) need you"
            : $"QUIET {rows.Count} window(s), none need you";
    }

    internal static void WriteTable(TextWriter output, IReadOnlyList<WaitingRow> rows, Budget budget, IReadOnlyList<string> unreachable)
    {
        output.WriteLine(Summary(rows, unreachable));

        // Said on every run, including when the number is zero. A tool that speaks to agents only when it
        // is sure has to be legible about when it was not, or "it did nothing" and "it saw nothing" look
        // the same from here.
        //
        // The two counts partition the reported rows, so they add up to what the table shows. They did not
        // when the second was taken from assurance alone: a high-confidence row that is merely holding is
        // neither actionable nor unsure, so it was counted in neither and the line described fewer rows
        // than were printed under it.
        int actionable = rows.Count(r => r.Verdict.MayAct);
        output.WriteLine($"NOT ACTED nothing was sent to any agent; {actionable} row(s) met the bar to act, {rows.Count - actionable} did not");

        if (rows.Count > 0)
        {
            output.WriteLine();
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

            WriteAligned(output, table);
        }

        output.WriteLine();
        output.WriteLine(budget.ToString());
        foreach (string failure in unreachable)
        {
            // An ssh failure carries the remote's stderr, which is as arbitrary as anything else here.
            output.WriteLine($"UNREACHABLE {DisplayText.Safe(failure)}");
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

        if (row.Defects is { Count: > 0 } defects)
        {
            // Reported, never repaired: a state that contradicts itself is a signal about the agent.
            detail += "  [!] " + string.Join("; ", defects);
        }

        return detail;
    }

    private static void WriteAligned(TextWriter output, IReadOnlyList<string[]> table)
    {
        // The single output boundary for untrusted text. Window and session names, verdict reasons and
        // the defects that quote a record back are all arbitrary strings somebody else chose, and this is
        // where they become terminal rows — so they are escaped here, once, rather than at each of the
        // half-dozen places that compose a cell. Escaping before the widths are measured is what keeps
        // the alignment honest: the padded width is the width that will actually be printed.
        string[][] cells = [.. table.Select(row => row.Select(DisplayText.Safe).ToArray())];

        int columns = cells[0].Length;
        var widths = new int[columns];
        foreach (string[] row in cells)
        {
            for (int i = 0; i < columns; i++)
            {
                widths[i] = Math.Max(widths[i], row[i].Length);
            }
        }

        foreach (string[] row in cells)
        {
            var line = new StringBuilder();
            for (int i = 0; i < columns; i++)
            {
                // The last column is free-form, so it is never padded.
                line.Append(i == columns - 1 ? row[i] : row[i].PadRight(widths[i] + 2));
            }

            output.WriteLine(line.ToString().TrimEnd());
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
    internal static void WriteJsonError(Stream output, string message)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("error", message);
        writer.WriteStartArray("rows");
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        output.Write("\n"u8);
        output.Flush();
    }

    /// <summary>
    /// Emits the rows as JSON.
    /// </summary>
    /// <remarks>
    /// Untrusted strings are written verbatim rather than escaped for display, and that is deliberate:
    /// <see cref="Utf8JsonWriter"/> already makes them structurally safe — a newline or an ESC inside a
    /// window name cannot end a string or forge a field — while the consumer is a program that wants the
    /// value the agent actually published, not this tool's rendering of it. Escaping is a property of
    /// printing to a terminal, so it belongs to the table and not here.
    /// </remarks>
    internal static void WriteJson(Stream output, IReadOnlyList<WaitingRow> rows, Budget budget, IReadOnlyList<string> unreachable)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("calls", budget.Calls);
        writer.WriteNumber("notModified", budget.NotModified);
        writer.WriteNumber("mergeabilityRereads", budget.Recomputed);
        writer.WriteBoolean("rateLimited", budget.RateLimited);
        writer.WriteStartArray("unreachable");
        foreach (string failure in unreachable)
        {
            writer.WriteStringValue(failure);
        }

        writer.WriteEndArray();
        if (budget.RateLimitRemaining is { } remaining)
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
            else if (row.Unidentified is { } unusable)
            {
                // No `pr` key, deliberately: this record named nothing, and a `0` or a `-1` there would be
                // a PR number to every consumer that reads one. Its absence is the fact — what the agent
                // asked for and how the record fails are still carried, because those are what a reader
                // needs to answer a `rec=stop` it cannot look up.
                writer.WriteString("rec", unusable.Recommendation.ToString().ToLowerInvariant());
                writer.WriteStartArray("defects");
                foreach (string d in unusable.Defects)
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
        output.Write("\n"u8);
        output.Flush();
    }
}
