namespace Octoshift.Commands;

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
/// rather than two per open PR, and the host collection runs one command per host. That is what keeps it
/// usable as a reflex rather than something worth avoiding.
///
/// Ownership is decided by the same <see cref="Claim.Register"/> logic <c>waiting</c> uses, over the same
/// shared history: a sort local to this command would label the first row owner even when the order is
/// only inferred, and would answer "who owns PR 1234" differently from the full sweep. The two must agree,
/// so the answer comes from one place.
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

        // Validated here as well as at the parser, because every value becomes an ssh argument and an
        // option-shaped one succeeds quietly as a quiet fleet.
        foreach (string host in hosts)
        {
            if (HostTarget.Validate(host) is { } invalid)
            {
                Console.Error.WriteLine($"octoshift: {invalid}");
                return ExitCode.Usage;
            }
        }

        WaitingCommand.Collection collected = await WaitingCommand.CollectAsync(
            hosts, (host, token) => new TmuxScanner(host).ScanAsync(token), ct);

        var facts = new GhPrFactsSource(repo, new FileConditionalCache(), (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));
        PrLocation located = await LocateAsync(
            prNumber, collected, new PaneHistory(), facts.FetchAsync, facts.RefreshMergeabilityAsync, DateTimeOffset.UtcNow, ct);

        if (json)
        {
            WriteJson(Console.OpenStandardOutput(), located, DateTimeOffset.UtcNow);
        }
        else
        {
            WriteReport(Console.Out, located, DateTimeOffset.UtcNow);
        }

        return located.ExitCode;
    }

    /// <summary>Where a PR was found, and everything the report and the exit code are computed from.</summary>
    /// <param name="PrNumber">The PR asked about.</param>
    /// <param name="Claims">This PR's claimants, owner first.</param>
    /// <param name="Facts">What GitHub said, or null when it could not be read.</param>
    /// <param name="ViewComplete">Whether the whole fleet was seen — every host answered, none dropped.</param>
    /// <param name="Collected">The sweep, for its unreachable hosts and window count.</param>
    /// <param name="Silence">Per-window silence, keyed by host and pane id.</param>
    internal readonly record struct PrLocation(
        int PrNumber,
        IReadOnlyList<(TmuxPane Pane, AgentState State, Claim Claim)> Claims,
        PrFacts? Facts,
        bool ViewComplete,
        WaitingCommand.Collection Collected,
        IReadOnlyDictionary<string, TimeSpan?> Silence)
    {
        /// <summary>
        /// A partly invisible fleet cannot produce success-shaped output: the PR may well be claimed on a
        /// host that did not answer, so a partial sweep fails even when this run happened to find it.
        /// Otherwise, finding neither a claim nor a PR is the not-found failure.
        /// </summary>
        public int ExitCode => Collected.AnyFailure
            ? Octoshift.ExitCode.Unavailable
            : Facts is null && Claims.Count == 0 ? Octoshift.ExitCode.Unavailable : Octoshift.ExitCode.Ok;
    }

    /// <summary>
    /// Locates a PR across an already-collected fleet. Injectable collection, history and fetch so the
    /// whole ownership, view-completeness and partial-fleet policy is testable without ssh or a network.
    /// </summary>
    internal static async Task<PrLocation> LocateAsync(
        int prNumber,
        WaitingCommand.Collection collected,
        PaneHistory history,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset now,
        CancellationToken ct)
    {
        // Window names that appear twice on one host. A duplicate is a rename that went where it did not
        // belong, so the name identifies nothing — the same safeguard `waiting` applies, so `pr` cannot
        // report a claim `waiting` rejects as defective.
        HashSet<string> ambiguousNames = [.. collected.Panes
            .Where(p => p.WindowName.Length > 0)
            .GroupBy(p => $"{p.Host ?? "local"}|{p.WindowName}", StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        // Read every pane once, with the duplicate-name and pane-corroboration safeguards, and collect
        // every claimant across the whole fleet — not just this PR's — so the contest is ranked exactly as
        // the full sweep ranks it.
        var readings = new List<(TmuxPane Pane, AgentState State)>();
        var claimants = new List<(TmuxPane Pane, int PrNumber, int? Round)>();
        foreach (TmuxPane pane in collected.Panes)
        {
            AgentState? state = AgentState.Parse(
                pane.AgentStateOption,
                pane.WindowName,
                nameIsAmbiguous: ambiguousNames.Contains($"{pane.Host ?? "local"}|{pane.WindowName}"),
                paneContradictsPr: pr => TmuxScanner.PaneContradictsPr(pane.Capture, pr));

            if (state is { IsIssue: false } claim)
            {
                claimants.Add((pane, claim.PrNumber, claim.Round));
            }

            if (state is not null)
            {
                readings.Add((pane, state));
            }
        }

        // A host that did not answer, and a host nobody asked about, both leave windows unseen — so a
        // window that is a follower on the full fleet can look like the sole claimant of its PR. The view
        // is complete only when every asked host answered and no previously-collected host was dropped.
        var collectedSet = collected.CollectedHosts.Select(h => h ?? "local").ToHashSet(StringComparer.Ordinal);
        bool viewComplete = collected.Unreachable.Count == 0
            && !history.KnownHosts.Any(h => !collectedSet.Contains(h));

        // Adopt each host's tmux epoch, capturing the prior sweep so a host first seen this run does not
        // hand a genuinely observed rival a witnessed order it never earned.
        var sweptBefore = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (IGrouping<string?, TmuxPane> host in collected.Panes.Where(p => p.Epoch.Length > 0).GroupBy(p => p.Host))
        {
            string key = host.Key ?? "local";
            DateTimeOffset? prior = history.SweptAt(host.Key);
            bool continuous = history.AdoptEpoch(host.Key, host.First().Epoch, now);
            sweptBefore[key] = continuous ? prior : null;
        }

        // Observe every window, not only the ones claiming this PR: the history is shared with `waiting`,
        // and a run that recorded a subset would prune the rest and reset their silence measurements.
        // Keyed by host and pane id together, because a pane id is unique only within one tmux server —
        // `%3` on two hosts is two windows, and a host-local key would let one overwrite the other.
        var silence = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach ((TmuxPane pane, AgentState state) in readings)
        {
            silence[Claim.Key(pane)] = history.Observe(pane, now, state.IsIssue ? null : state.PrNumber);
        }

        IReadOnlyDictionary<string, Claim> claims = Claim.Register(
            claimants,
            history.ClaimedAt,
            host => sweptBefore.GetValueOrDefault(host ?? "local"),
            viewComplete);

        // Persist history only for the hosts actually collected, so a partial sweep keeps what it did not
        // look at rather than deleting the claim memory of unreachable or unrequested hosts.
        history.Save(collected.Panes, collected.CollectedHosts);

        // This PR's claimants, owner first. The rank comes from Claim.Register, so followers sort after the
        // owner; ties break on the same fixed key the sweep uses, so the owner is the same window in both.
        (TmuxPane Pane, AgentState State, Claim Claim)[] mine = [.. readings
            .Where(r => !r.State.IsIssue && r.State.PrNumber == prNumber)
            .Select(r => (r.Pane, r.State, Claim: claims.GetValueOrDefault(Claim.Key(r.Pane), Claim.Sole)))
            .OrderBy(m => m.Claim.IsFollower ? 1 : 0)
            .ThenBy(m => history.ClaimedAt(m.Pane) ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Pane.Host ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Pane.PaneId, StringComparer.Ordinal)];

        PrFacts? prFacts = await fetchAsync(prNumber, ct);
        if (prFacts is not null && !prFacts.MergeabilityKnown && !prFacts.Merged)
        {
            prFacts = await refreshMergeabilityAsync(prNumber, ct) is { MergeabilityKnown: true } refreshed
                && string.Equals(refreshed.HeadSha, prFacts.HeadSha, StringComparison.OrdinalIgnoreCase)
                    ? prFacts with { MergeableState = refreshed.MergeableState }
                    : prFacts;
        }

        return new PrLocation(prNumber, mine, prFacts, viewComplete, collected, silence);
    }

    private static void WriteReport(TextWriter output, PrLocation located, DateTimeOffset now)
    {
        int prNumber = located.PrNumber;
        IReadOnlyList<(TmuxPane Pane, AgentState State, Claim Claim)> claims = located.Claims;
        PrFacts? facts = located.Facts;
        WaitingCommand.Collection collected = located.Collected;
        IReadOnlyDictionary<string, TimeSpan?> silence = located.Silence;
        bool viewComplete = located.ViewComplete;

        output.WriteLine($"PR #{prNumber}{(facts?.Title is { Length: > 0 } title ? $"  {DisplayText.Safe(title)}" : string.Empty)}");

        if (claims.Count == 0)
        {
            string where = collected.Panes.Count == 0 ? "no windows collected" : "no window claims it";
            output.WriteLine($"  where     {where}");
        }

        foreach ((TmuxPane pane, AgentState state, Claim claim) in claims)
        {
            string name = pane.WindowName.Length > 0 ? $" {DisplayText.Safe(pane.WindowName)}" : string.Empty;
            string role = claims.Count > 1 ? (claim.IsFollower ? "  [follows]" : "  [owner]") : string.Empty;
            output.WriteLine($"  where     {DisplayText.Safe(pane.Where)}{name}   {Activity(pane, now, silence.GetValueOrDefault(Claim.Key(pane)))}{role}");
            if (Agent(state) is { Length: > 0 } line)
            {
                output.WriteLine($"  agent     {DisplayText.Safe(line)}");
            }
        }

        // The fight this is meant to catch. Same host means one worktree, so the two are overwriting each
        // other's edits rather than racing to push.
        if (claims.Count > 1)
        {
            (TmuxPane ownerPane, _, Claim ownerClaim) = claims[0];
            bool sameHost = claims.Select(c => c.Pane.Host).Distinct().Count() == 1;
            string order = ownerClaim.Basis switch
            {
                ClaimBasis.Observed => string.Empty,
                ClaimBasis.PartialView => " — order unconfirmed while the fleet is partly unseen",
                _ => " — order inferred, not observed",
            };
            output.WriteLine($"  CONTESTED {claims.Count} windows claim this PR"
                + (sameHost ? " on one host — they are likely sharing a worktree" : " across hosts")
                + $"; owner is {DisplayText.Safe(ownerPane.Where)}, the rest are followed and never driven"
                + order);

            // The one contested shape worth calling out: the owner is putting the claim down while a
            // follower is still working, so ownership is with the window that is doing the least.
            if (facts is not null
                && Claim.IsReleasing(claims[0].State, WaitingVerdict.Resolve(claims[0].State, facts))
                && claims.Skip(1).Any(c => c.Pane.Activity == PaneActivity.Working))
            {
                output.WriteLine("            the owner is disengaging while a follower is active — consider promoting it");
            }
        }

        output.WriteLine($"  github    {Github(facts, now)}");

        // One verdict per claim when contested: the disagreement between them is the finding, and
        // collapsing it to a single row would hide which window the answer came from.
        foreach ((TmuxPane pane, AgentState state, _) in claims)
        {
            if (facts is null)
            {
                break;
            }

            WaitingVerdict verdict = WaitingVerdict.Resolve(state, facts);
            string from = claims.Count > 1 ? $" [{DisplayText.Safe(pane.Where)}]" : string.Empty;
            output.WriteLine($"  verdict   {verdict.State.ToString().ToUpperInvariant()} ({verdict.Assurance.Label}) — {DisplayText.Safe(verdict.Reason)}{from}");
        }

        if (!viewComplete && collected.Unreachable.Count == 0)
        {
            output.WriteLine("  NARROWED  fewer hosts than have been collected before; a claim may be on a host not swept this run");
        }

        foreach (string failure in collected.Unreachable)
        {
            output.WriteLine($"  UNREACHABLE {DisplayText.Safe(failure)}");
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
            PaneActivity.Stalled => "agent stalled",
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
        parts.Add(facts.IsConflicting ? "CONFLICTING" : facts.IsMergeable ? "mergeable" : DisplayText.Safe(facts.MergeableState) ?? "mergeability unknown");

        if (!facts.ChecksKnown)
        {
            parts.Add("checks unreadable");
        }
        else if (facts.Checks.FirstOrDefault(c => c.IsFailure) is { } failed)
        {
            parts.Add($"CI red ({DisplayText.Safe(failed.Name)})");
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

    internal static void WriteJson(Stream output, PrLocation located, DateTimeOffset now)
    {
        int prNumber = located.PrNumber;
        IReadOnlyList<(TmuxPane Pane, AgentState State, Claim Claim)> claims = located.Claims;
        PrFacts? facts = located.Facts;
        WaitingCommand.Collection collected = located.Collected;
        bool viewComplete = located.ViewComplete;

        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteNumber("pr", prNumber);
        if (facts?.Title is { } title)
        {
            writer.WriteString("title", title);
        }

        writer.WriteStartArray("claims");
        foreach ((TmuxPane pane, AgentState state, Claim claim) in claims)
        {
            writer.WriteStartObject();
            writer.WriteString("where", pane.Where);
            writer.WriteString("window", pane.WindowName);
            if (pane.Host is { } host)
            {
                writer.WriteString("host", host);
            }

            writer.WriteString("activity", pane.Activity.ToString().ToLowerInvariant());
            if (claims.Count > 1)
            {
                writer.WriteString("role", claim.IsFollower ? "follower" : "owner");
            }

            if (state.Round is { } round)
            {
                writer.WriteNumber("round", round);
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("contested", claims.Count > 1);
        if (claims.Count > 1)
        {
            writer.WriteString("order", claims[0].Claim.Basis.ToString().ToLowerInvariant());
        }

        // A partly invisible fleet is named in the output as well as the exit code: the requested PR may
        // be claimed on a host that did not answer, so success-shaped JSON that omits the failure would
        // assert a completeness the sweep did not have.
        writer.WriteBoolean("viewComplete", viewComplete && collected.Unreachable.Count == 0);
        writer.WriteStartArray("unreachable");
        foreach (string failure in collected.Unreachable)
        {
            writer.WriteStringValue(failure);
        }

        writer.WriteEndArray();

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
        output.Write("\n"u8);
        output.Flush();
    }
}
