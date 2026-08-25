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

    /// <summary>
    /// How long the window has produced no new content, measured across runs. Null until a second
    /// observation exists. Distinct from <see cref="StoppedFor"/>, which a repainting spinner resets.
    /// </summary>
    public TimeSpan? SilentFor { get; init; }

    /// <summary>
    /// This window's standing among the windows claiming its PR. Two agents on one PR fight; on one host
    /// they fight over the same worktree, which is more destructive and more discoverable at once.
    /// </summary>
    public Claim Claim { get; init; } = Claim.Sole;

    /// <summary>
    /// Whether a tool may speak to this window unattended. The verdict decides whether the PR's state
    /// warrants it; the claim decides whether this window is the one entitled to hear it. A follower is
    /// never spoken to however good its evidence, because the fix for two agents on one PR is not to
    /// drive both of them carefully.
    /// </summary>
    public bool MayAct => Verdict.MayAct && Claim.OwnsClaim;
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
    public static async Task<int> RunAsync(string? repoFlag, IReadOnlyList<string> hosts, bool all, bool json, bool rename, CancellationToken ct)
    {
        string? repo = RepoScope.Resolve(repoFlag);
        if (repo is null)
        {
            Console.Error.WriteLine("octoshift: could not resolve a repo scope; pass --repo owner/name.");
            return ExitCode.Usage;
        }

        FleetScan scan = await FleetScan.CollectAsync(hosts, ct);
        IReadOnlyList<TmuxPane> panes = scan.Panes;
        IReadOnlyList<string> unreachable = scan.Unreachable;

        // Total failure keeps its own path. A sweep where nothing could be collected is not a quiet fleet,
        // and printing a QUIET summary above the failure inverts which of the two the reader sees first.
        if (scan.TotalFailure)
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

        if (rename)
        {
            await RenameAsync(rows, ct);
        }

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

        // A PR claimed by more than one window is a fight in progress. Computed across every host, since
        // the two halves of it are often not on the same machine.
        var history = new PaneHistory();

        // Adopt each host's tmux epoch first: a server that has restarted invalidates every pane id
        // remembered for it, and keeping those would present one window's registration as another's.
        foreach (IGrouping<string?, TmuxPane> host in panes.Where(p => p.Epoch.Length > 0).GroupBy(p => p.Host))
        {
            history.AdoptEpoch(host.Key, host.First().Epoch, now);
        }

        // Register every claim before ranking, so a window seen for the first time this sweep still has
        // a registration time to be ordered by.
        // Observed once per window, and the answer kept: calling it twice happens to work, because the
        // second call sees the digest it just stored, but it makes the silence measurement depend on
        // call order rather than on the data.
        var silence = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach ((TmuxPane pane, AgentState state) in pending)
        {
            silence[Claim.Key(pane)] = history.Observe(pane, now, state.IsIssue ? null : state.PrNumber);
        }

        IReadOnlyDictionary<string, Claim> claims = Claim.Register(
            pending.Where(e => !e.State.IsIssue).Select(e => (e.Pane, e.State.PrNumber, e.State.Round)),
            history.ClaimedAt,
            history.SweptAt);

        foreach ((TmuxPane pane, AgentState state) in pending)
        {
            rows.Add(Row(pane, state, WaitingVerdict.Resolve(state, state.IsIssue ? null : seen.GetValueOrDefault(state.PrNumber)), now)
                with
                {
                    Claim = claims.GetValueOrDefault(Claim.Key(pane), Claim.Sole),
                    SilentFor = silence.GetValueOrDefault(Claim.Key(pane)),
                });
        }

        // Longest wait first among the rows that need you: coming back after hours away, the thing that
        // has been stuck longest is the thing that cost the most.
        history.Save(panes);

        // The operator's queue first, longest wait at the top: after hours away, the row that has been
        // stuck longest is the one that cost the most.
        return rows
            .Where(r => all || r.Verdict.NeedsAttention || r.Record?.Defects.Count > 0 || r.Claim.IsContested)
            .OrderByDescending(r => r.Verdict.NeedsAttention)
            .ThenBy(r => r.Verdict.Severity)
            .ThenByDescending(r => r.StoppedFor ?? TimeSpan.Zero)
            .ToArray();
    }

    /// <summary>
    /// Corrects window names the tool can see are wrong, one batched command per host. Only names that
    /// actually differ are touched, so a fleet already correct costs nothing.
    /// </summary>
    private static async Task RenameAsync(IReadOnlyList<WaitingRow> rows, CancellationToken ct)
    {
        foreach (IGrouping<string?, WaitingRow> host in rows.GroupBy(r => r.Pane.Host))
        {
            (TmuxPane Pane, string Desired)[] renames = [.. host
                .Select(r => (r.Pane, Desired: WindowNaming.Apply(r.Pane.WindowName, WindowNaming.SuffixFor(r.Verdict, r.Claim))))
                .Where(r => !string.Equals(r.Desired, r.Pane.WindowName, StringComparison.Ordinal))];

            if (WindowNaming.BuildRenameScript(renames) is { } script)
            {
                await ShellRunner.For(host.Key)(script, ct);
                foreach ((TmuxPane pane, string desired) in renames)
                {
                    Console.WriteLine($"RENAMED {pane.Where} {pane.WindowName} -> {desired}");
                }
            }
        }
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
        int actionable = rows.Count(r => r.MayAct);

        // Counted as the complement, not by re-deriving the test: the two must always add up to the rows
        // shown, and an earlier version counted only low confidence, so rows held back by a contested
        // claim appeared in neither number.
        int unsure = rows.Count - actionable;
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
                    Duration(row.SilentFor ?? row.StoppedFor),
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
        if (row.Claim.IsContested)
        {
            // Same host is called out separately: those two are sharing a worktree, so the damage is
            // direct rather than a race to push.
            bool sameHost = row.Claim.Others.Any(r => r.Host == row.Pane.Host);
            string role = row.Claim.IsFollower ? "FOLLOWER of" : "OWNER; also claimed by";
            string basis = row.Claim.Basis == ClaimBasis.Inferred ? " (order inferred, not observed)" : string.Empty;
            detail += $"  [!] {role} {string.Join(", ", row.Claim.Others.Select(r => r.Where))}"
                + (sameHost ? " — same host, likely one worktree" : string.Empty) + basis;
        }

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

            writer.WriteBoolean("mayAct", row.MayAct);
            writer.WriteBoolean("acted", false);
            if (row.Claim.IsContested)
            {
                writer.WriteString("claim", row.Claim.Rank.ToString().ToLowerInvariant());
                writer.WriteStartArray("alsoClaimedBy");
                foreach (TmuxPane other in row.Claim.Others)
                {
                    writer.WriteStringValue(other.Where);
                }

                writer.WriteEndArray();
            }
            if (row.StoppedFor is { } stopped)
            {
                writer.WriteNumber("stoppedForSeconds", (long)stopped.TotalSeconds);
            }

            if (row.SilentFor is { } silent)
            {
                writer.WriteNumber("silentForSeconds", (long)silent.TotalSeconds);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
}
