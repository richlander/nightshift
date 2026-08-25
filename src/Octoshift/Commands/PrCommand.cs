namespace Octoshift.Commands;

using System.Text;
using System.Text.Json;
using Octoshift.GitHub;
using Octoshift.Waiting;

/// <summary>
/// Answers "where is PR 1234, and what is happening to it" in one command.
/// </summary>
/// <remarks>
/// The work is spread across machines, so realising mid-conversation that the PR in front of you
/// interacts with another one starts a hunt: which host, which session, which window. This turns that
/// into one question with one answer.
///
/// It is cheap for the same reason the full sweep is not: asking about one PR costs two REST calls
/// rather than two per open PR, and the host collection runs concurrently. That is what keeps it usable
/// as a reflex rather than something worth avoiding.
/// </remarks>
internal static class PrCommand
{
    public static async Task<int> RunAsync(int prNumber, string? repoFlag, IReadOnlyList<string> hosts, bool json, CancellationToken ct)
    {
        string? repo = RepoScope.Resolve(repoFlag);
        if (repo is null)
        {
            Console.Error.WriteLine("octoshift: could not resolve a repo scope; pass --repo owner/name.");
            return ExitCode.Usage;
        }

        FleetScan scan = await FleetScan.CollectAsync(hosts, ct);
        var history = new PaneHistory();

        // Windows claiming this PR through either channel: a published state, or the window name alone.
        var claims = new List<(TmuxPane Pane, AgentState State)>();
        foreach (TmuxPane pane in scan.Panes)
        {
            if (AgentState.Parse(pane.AgentStateOption, pane.WindowName) is { } state && state.PrNumber == prNumber && !state.IsIssue)
            {
                claims.Add((pane, state));
            }
        }

        var facts = new GhPrFactsSource(repo, new FileConditionalCache(), (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));
        PrFacts? prFacts = await facts.FetchAsync(prNumber, ct);
        if (prFacts is not null && !prFacts.MergeabilityKnown && !prFacts.Merged)
        {
            prFacts = await facts.RefreshMergeabilityAsync(prNumber, ct) is { MergeabilityKnown: true } refreshed
                && string.Equals(refreshed.HeadSha, prFacts.HeadSha, StringComparison.OrdinalIgnoreCase)
                    ? prFacts with { MergeableState = refreshed.MergeableState }
                    : prFacts;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        // Observe every window, not only the ones claiming this PR: the history is shared with `waiting`,
        // and a run that recorded a subset would prune the rest and reset their silence measurements.
        Dictionary<string, TimeSpan?> silence = [];
        foreach (TmuxPane pane in scan.Panes)
        {
            silence[pane.PaneId] = history.Observe(pane, now);
        }

        history.Save(scan.Panes);

        if (json)
        {
            WriteJson(prNumber, claims, prFacts, scan, now);
        }
        else
        {
            WriteReport(prNumber, claims, prFacts, scan, now, silence);
        }

        return prFacts is null && claims.Count == 0 ? ExitCode.Unavailable : ExitCode.Ok;
    }

    private static void WriteReport(
        int prNumber,
        IReadOnlyList<(TmuxPane Pane, AgentState State)> claims,
        PrFacts? facts,
        FleetScan scan,
        DateTimeOffset now,
        IReadOnlyDictionary<string, TimeSpan?> silence)
    {
        Console.WriteLine($"PR #{prNumber}{(facts?.Title is { Length: > 0 } title ? $"  {title}" : string.Empty)}");

        if (claims.Count == 0)
        {
            string where = scan.Panes.Count == 0 ? "no windows collected" : "no window claims it";
            Console.WriteLine($"  where     {where}");
        }

        foreach ((TmuxPane pane, AgentState state) in claims)
        {
            string name = pane.WindowName.Length > 0 ? $" {pane.WindowName}" : string.Empty;
            Console.WriteLine($"  where     {pane.Where}{name}   {Activity(pane, now, silence.GetValueOrDefault(pane.PaneId))}");
            if (Agent(state) is { Length: > 0 } line)
            {
                Console.WriteLine($"  agent     {line}");
            }
        }

        // The fight this is meant to catch. Same host means one worktree, so the two are overwriting each
        // other's edits rather than racing to push.
        if (claims.Count > 1)
        {
            bool sameHost = claims.Select(c => c.Pane.Host).Distinct().Count() == 1;
            Console.WriteLine($"  CONFLICT  {claims.Count} windows claim this PR"
                + (sameHost ? " on one host — they are likely sharing a worktree" : " across hosts"));
        }

        Console.WriteLine($"  github    {Github(facts, now)}");

        // One verdict per claim when contested: the disagreement between them is the finding, and
        // collapsing it to a single row would hide which window the answer came from.
        foreach ((TmuxPane pane, AgentState state) in claims)
        {
            if (facts is null)
            {
                break;
            }

            WaitingVerdict verdict = WaitingVerdict.Resolve(state, facts);
            string from = claims.Count > 1 ? $" [{pane.Where}]" : string.Empty;
            Console.WriteLine($"  verdict   {verdict.State.ToString().ToUpperInvariant()} ({verdict.Assurance.Label}) — {verdict.Reason}{from}");
        }

        foreach (string failure in scan.Unreachable)
        {
            Console.WriteLine($"  UNREACHABLE {failure}");
        }
    }

    private static string Activity(TmuxPane pane, DateTimeOffset now, TimeSpan? silent)
    {
        string quiet = pane.LastActivity is { } at && at <= now ? $" for {Duration(now - at)}" : string.Empty;

        // The distinction that matters when two windows claim one PR: a spinner is not progress, and
        // both of them look busy from the outside.
        string producing = silent is { } s && s > TimeSpan.FromMinutes(2)
            ? $", no output for {Duration(s)}"
            : string.Empty;

        return pane.Activity switch
        {
            PaneActivity.Working => $"working{producing}",
            PaneActivity.Blocked => "blocked on a prompt",
            PaneActivity.Unreadable => "pane unreadable",
            _ => $"idle{quiet}",
        };
    }

    private static string Agent(AgentState state)
    {
        if (state.Source == StateSource.WindowName)
        {
            return "published no state; identified by window name";
        }

        var parts = new List<string>();
        if (state.Round is { } round)
        {
            parts.Add($"round {round}");
        }

        if (state.ReviewsRequired is { } required)
        {
            parts.Add($"reviews {state.ReviewsClean ?? 0}/{required}");
        }

        if (state.Waiting.Kind != WaitKind.None)
        {
            parts.Add($"waiting {state.Waiting}");
        }

        if (state.Blocked.Count > 0)
        {
            parts.Add($"blocked by {string.Join(", ", state.Blocked.Select(b => "#" + b))}");
        }

        if (state.Recommendation != Recommendation.None)
        {
            parts.Add($"rec {state.Recommendation.ToString().ToLowerInvariant()}");
        }

        if (state.Defects.Count > 0)
        {
            parts.Add($"[!] {string.Join("; ", state.Defects)}");
        }

        return string.Join(", ", parts);
    }

    private static string Github(PrFacts? facts, DateTimeOffset now)
    {
        if (facts is null)
        {
            return "could not be read";
        }

        if (facts.Merged)
        {
            // The question behind "where is it" is often "did I already land this", and how long ago is
            // what turns that from a fact into an orientation.
            string ago = facts.MergedAt is { } mergedAt && mergedAt <= now ? $" {Duration(now - mergedAt)} ago" : string.Empty;
            return $"merged{ago}";
        }

        if (string.Equals(facts.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return "closed without merging";
        }

        var parts = new List<string> { "open" };
        parts.Add(facts.IsConflicting ? "CONFLICTING" : facts.IsMergeable ? "mergeable" : facts.MergeableState ?? "mergeability unknown");

        if (!facts.ChecksKnown)
        {
            parts.Add("checks unreadable");
        }
        else if (facts.Checks.FirstOrDefault(c => c.IsFailure) is { } failed)
        {
            parts.Add($"CI red ({failed.Name})");
        }
        else if (facts.Checks.Any(c => !c.IsComplete))
        {
            parts.Add($"{facts.Checks.Count(c => !c.IsComplete)} check(s) running");
        }
        else if (facts.Checks.Count > 0)
        {
            parts.Add("CI green");
        }

        return string.Join(" · ", parts) + $" · head {Short(facts.HeadSha)}";
    }

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];

    private static string Duration(TimeSpan span) => span switch
    {
        { TotalMinutes: < 1 } => "under a minute",
        { TotalHours: < 1 } value => $"{(int)value.TotalMinutes}m",
        { TotalDays: < 1 } value => $"{(int)value.TotalHours}h{value.Minutes:00}m",
        var value => $"{(int)value.TotalDays}d{value.Hours:00}h",
    };

    private static void WriteJson(
        int prNumber,
        IReadOnlyList<(TmuxPane Pane, AgentState State)> claims,
        PrFacts? facts,
        FleetScan scan,
        DateTimeOffset now)
    {
        using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("pr", prNumber);
        if (facts?.Title is { } title)
        {
            writer.WriteString("title", title);
        }

        writer.WriteStartArray("claims");
        foreach ((TmuxPane pane, AgentState state) in claims)
        {
            writer.WriteStartObject();
            writer.WriteString("where", pane.Where);
            writer.WriteString("window", pane.WindowName);
            if (pane.Host is { } host)
            {
                writer.WriteString("host", host);
            }

            writer.WriteString("activity", pane.Activity.ToString().ToLowerInvariant());
            if (state.Round is { } round)
            {
                writer.WriteNumber("round", round);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("contested", claims.Count > 1);
        if (facts is not null)
        {
            writer.WriteString("state", facts.Merged ? "merged" : facts.State);
            if (facts.MergedAt is { } mergedAt)
            {
                writer.WriteString("mergedAt", mergedAt.ToString("O"));
                writer.WriteNumber("mergedHoursAgo", Math.Round((now - mergedAt).TotalHours, 1));
            }

            writer.WriteString("head", facts.HeadSha);
            writer.WriteBoolean("mergeable", facts.IsMergeable);
        }

        writer.WriteEndObject();
        writer.Flush();
        Console.WriteLine();
    }
}
