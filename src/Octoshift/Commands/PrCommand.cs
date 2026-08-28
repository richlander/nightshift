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
    public static async Task<int> RunAsync(int prNumber, IReadOnlyList<string> repoFlags, IReadOnlyList<string> hosts, bool json, CancellationToken ct, string? historyPath = null)
    {
        RepoScope.Resolution scope = RepoScope.Resolve(repoFlags);
        if (scope.Error is { } scopeError)
        {
            Console.Error.WriteLine($"octoshift: {scopeError}");
            return ExitCode.Usage;
        }

        IReadOnlyList<string> repos = scope.Repos;
        if (repos.Count == 0)
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

        var facts = new GhFleetPrFactsSource(repos, new FileConditionalCache(), GhProcessRunner.RunGhAsync);

        try
        {
            PrLocation located = await CollectAndLocateAsync(
                prNumber, hosts, (host, token) => new TmuxScanner(host).ScanAsync(token),
                facts.FetchDetailedAsync, facts.RefreshMergeabilityAsync, now: null, ct, historyPath: historyPath);

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
        catch (HistoryUnavailableException ex)
        {
            // A history failure — a malformed or unreadable file, a lock that could not be taken, or a
            // write that did not land — leaves fleet ownership unknown. The human output leads with an
            // aligned failure token on the first stdout line so a harness sees the disposition before the
            // details, matching the unavailable exit; the specific cause goes to stderr. A genuine caller
            // cancellation is a different exception, not caught here, so it propagates without a token.
            // JSON stays a single error document, never a token prepended to it.
            if (json)
            {
                WaitingCommand.WriteJsonError(Console.OpenStandardOutput(), ex.Message);
            }
            else
            {
                Console.Out.WriteLine($"PARTIAL PR #{prNumber} — pane history unavailable; fleet ownership is unknown");
                Console.Error.WriteLine($"octoshift: {DisplayText.Safe(ex.Message)}");
            }

            return ExitCode.Unavailable;
        }
    }

    /// <summary>
    /// The product core of <c>octoshift pr</c>: acquire the history transaction, collect, and locate — in
    /// that order, so the cross-process lock brackets the whole collect→reconcile→save as one unit and no
    /// older scan can commit a stale snapshot after a newer one. A concurrent waiting/pr blocks on
    /// <see cref="PaneHistory.OpenAsync"/> until this transaction commits and releases, so whoever collects
    /// does so at-or-after the previous committer's snapshot. The scan and GitHub fetchers are injected so
    /// the ordering is testable without ssh or a network; <paramref name="historyPath"/> and
    /// <paramref name="perTargetTimeout"/> are internal seams for the same reason. LocateAsync's Save
    /// releases the lock before its GitHub read, so the network is never held under it; the finally
    /// disposes the transaction on any early exit.
    /// </summary>
    internal static async Task<PrLocation> CollectAndLocateAsync(
        int prNumber,
        IReadOnlyList<string> hosts,
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanAsync,
        Func<int, CancellationToken, Task<PrFetch>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset? now,
        CancellationToken ct,
        string? historyPath = null,
        TimeSpan? perTargetTimeout = null)
    {
        PaneHistory? history = null;
        try
        {
            history = await PaneHistory.OpenAsync(historyPath, ct);

            // Collect the whole declared fleet, not merely this run's --host arguments — the same
            // decode-the-remembered-members step `waiting` performs. Locating a PR across the fleet is only
            // as trustworthy as the view is complete, so a lookup must reach every host ever attempted
            // (local plus remotes) rather than reading a narrower view of the fleet as the whole of it.
            IReadOnlyList<string?> targets = history.FleetTargets(hosts);
            WaitingCommand.Collection collected = await WaitingCommand.CollectTargetsAsync(targets, scanAsync, ct, perTargetTimeout);

            // Sample the registration clock inside the held transaction, after collection, and clamp it
            // above the greatest time already on disk — the same monotonicity waiting uses, so a pr sweep
            // that acquired the lock late (or ran under a stepped-back clock) cannot stamp a new claimant
            // before one an already-committed transaction recorded. A test may inject the sample.
            DateTimeOffset stamped = history.TransactionTime(now ?? DateTimeOffset.UtcNow);
            return await LocateAsync(prNumber, collected, history, fetchAsync, refreshMergeabilityAsync, stamped, ct);
        }
        finally
        {
            history?.Dispose();
        }
    }

    /// <summary>Where a PR was found, and everything the report and the exit code are computed from.</summary>
    /// <param name="PrNumber">The PR asked about.</param>
    /// <param name="Claims">This PR's claimants, owner first.</param>
    /// <param name="Facts">What GitHub said, or null when it had no such PR or could not be read.</param>
    /// <param name="Github">Which of the three GitHub outcomes this was — found, an affirmative 404, or unavailable.</param>
    /// <param name="ViewComplete">Whether the whole fleet was seen — every host answered, none dropped.</param>
    /// <param name="Collected">The sweep, for its unreachable hosts and window count.</param>
    /// <param name="Silence">Per-window silence, keyed by host and pane id.</param>
    internal readonly record struct PrLocation(
        int PrNumber,
        IReadOnlyList<(TmuxPane Pane, AgentState State, Claim Claim)> Claims,
        PrFacts? Facts,
        PrFetchStatus Github,
        bool ViewComplete,
        WaitingCommand.Collection Collected,
        IReadOnlyDictionary<string, TimeSpan?> Silence)
    {
        /// <summary>The repos this PR was actually queried in — narrower than <see cref="Configured"/> when
        /// the search stopped early on a proven collision or an exhausted shared budget.</summary>
        public IReadOnlyList<string> Searched { get; init; } = [];

        /// <summary>The searched repos this PR number was found in; more than one means <see cref="PrDisposition.Ambiguous"/>.</summary>
        public IReadOnlyList<string> FoundIn { get; init; } = [];

        /// <summary>The full configured <c>--repo</c> (or inferred) scope, so a report can explain the
        /// unqueried remainder rather than relabelling it as searched.</summary>
        public IReadOnlyList<string> Configured { get; init; } = [];

        /// <summary>
        /// The full multi-repo resolution reconstructed from its parts, so a claim's verdict is joined
        /// against the same outcome the top-line disposition names — an affirmative not-found, an ambiguous
        /// collision, or a partial hit whose uniqueness is unproven resolves the claim's verdict truthfully
        /// rather than falling to a bare "could not read from GitHub" that contradicts the header.
        /// </summary>
        public PrFetch Fetch => new(Github, Facts, Searched, FoundIn, Configured);

        /// <summary>
        /// The single word this location reduces to, which the human report leads its first line with and
        /// the exit code follows. A partly invisible fleet (a host that did not answer, or a
        /// previously-collected host this run omitted) means the PR may be claimed somewhere this sweep
        /// could not see, so a success-shaped result would assert a completeness the sweep did not have:
        /// <see cref="PrDisposition.Partial"/> when a host was unreachable, <see cref="PrDisposition.Narrowed"/>
        /// when the view was merely narrower than before. Under a complete, fully-reached view: a PR number
        /// that resolves in more than one searched repo is <see cref="PrDisposition.Ambiguous"/> — reported
        /// rather than resolved to an arbitrary repo; a claim or a single-repo GitHub PR is
        /// <see cref="PrDisposition.Found"/>; neither, with every searched repo answering 404, is
        /// <see cref="PrDisposition.NotFound"/>; neither, with a searched repo merely unreadable, is
        /// <see cref="PrDisposition.Unavailable"/> — the one case a prior head wrongly called NotFound, since
        /// an outage cannot prove a PR does not exist.
        /// </summary>
        public PrDisposition Disposition
            => Collected.Unreachable.Count > 0 ? PrDisposition.Partial
                : !ViewComplete ? PrDisposition.Narrowed
                : Github == PrFetchStatus.Ambiguous ? PrDisposition.Ambiguous
                : Claims.Count > 0 || Github == PrFetchStatus.Found ? PrDisposition.Found
                : Github == PrFetchStatus.Unavailable ? PrDisposition.Unavailable
                : PrDisposition.NotFound;

        /// <summary>Only a found PR succeeds; every other disposition is unavailable, matching the token
        /// the first line leads with.</summary>
        public int ExitCode => Disposition == PrDisposition.Found ? Octoshift.ExitCode.Ok : Octoshift.ExitCode.Unavailable;
    }

    /// <summary>What <c>octoshift pr</c> reduced to, named so the human report's first-line token and the
    /// exit code stay aligned: a harness greps the token, the shell branches on the exit code, and the two
    /// can never disagree.</summary>
    internal enum PrDisposition
    {
        /// <summary>A complete view with a claim, a PR, or both. The one success. Leads with <c>PR</c>.</summary>
        Found,

        /// <summary>A host could not be read, so the PR may be claimed where the sweep could not see. Leads with <c>PARTIAL</c>.</summary>
        Partial,

        /// <summary>Fewer hosts than have been collected before, so the view is narrower than it has been. Leads with <c>NARROWED</c>.</summary>
        Narrowed,

        /// <summary>A complete view with neither a claiming window nor a GitHub PR, GitHub affirmatively 404. Leads with <c>NOTFOUND</c>.</summary>
        NotFound,

        /// <summary>A complete view with no claim, and GitHub could not be read — so existence is unknown, never
        /// a not-found. Leads with the same <c>PARTIAL</c> token the other unavailable results use.</summary>
        Unavailable,

        /// <summary>The PR number resolves in more than one searched repo, so no single repo can be chosen
        /// truthfully. A non-success disposition leading with <c>AMBIGUOUS</c>; the remedy is a single <c>--repo</c>.</summary>
        Ambiguous,
    }


    /// <summary>
    /// Locates a PR across an already-collected fleet. Injectable collection, history and fetch so the
    /// whole ownership, view-completeness and partial-fleet policy is testable without ssh or a network.
    /// </summary>
    internal static async Task<PrLocation> LocateAsync(
        int prNumber,
        WaitingCommand.Collection collected,
        PaneHistory? history,
        Func<int, CancellationToken, Task<PrFetch>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset now,
        CancellationToken ct,
        string? historyPath = null)
    {
        // The shared history transaction. In production RunAsync opens it *before* collection and injects
        // it here, so the cross-process lock brackets the whole collect→reconcile→save as one unit and no
        // older scan can commit a stale snapshot after a newer one. A test may inject its own history or a
        // historyPath to open one here; when this method owns it, the finally is the safety net for an
        // early exit. The lock is held only across the local parse and reconcile below and released by
        // Save before the GitHub read. A malformed or unreadable existing file, or a lock failure, escapes
        // as HistoryUnavailableException, which RunAsync maps to the unavailable contract.
        bool ownsHistory = history is null;
        history ??= await PaneHistory.OpenAsync(historyPath, ct);
        try
        {

        // Window names that appear twice on one host. A duplicate is a rename that went where it did not
        // belong, so the name identifies nothing — the same safeguard `waiting` applies, so `pr` cannot
        // report a claim `waiting` rejects as defective.
        HashSet<string> ambiguousNames = [.. collected.Panes
            .Where(p => p.WindowName.Length > 0)
            .GroupBy(p => TargetId.ForHost(p.Host).ComposeWith(p.WindowName), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        // Read every pane once, with the duplicate-name and pane-corroboration safeguards. Keep every
        // pane's reading — including the ones that identify nothing — so each is observed below and its
        // stale registration cleared; collect the claimants across the whole fleet so the contest is
        // ranked exactly as the full sweep ranks it.
        var parsed = new List<(TmuxPane Pane, AgentState? State)>();
        var claimants = new List<(TmuxPane Pane, int PrNumber, int? Round)>();
        foreach (TmuxPane pane in collected.Panes)
        {
            AgentState? state = AgentState.Parse(
                pane.AgentStateOption,
                pane.WindowName,
                nameIsAmbiguous: ambiguousNames.Contains(TargetId.ForHost(pane.Host).ComposeWith(pane.WindowName)),
                paneContradictsPr: pr => TmuxScanner.PaneContradictsPr(pane.Capture, pr));

            parsed.Add((pane, state));
            if (state is { IsIssue: false } claim)
            {
                claimants.Add((pane, claim.PrNumber, claim.Round));
            }
        }

        // A host that did not answer, and a host nobody asked about, both leave windows unseen — so a
        // window that is a follower on the full fleet can look like the sole claimant of its PR. The view
        // is complete only when every asked host answered and no previously-collected host was dropped.
        var collectedKeys = collected.CollectedHosts.Select(h => TargetId.ForHost(h).Key).ToHashSet(StringComparer.Ordinal);
        bool viewComplete = collected.Unreachable.Count == 0
            && !history.KnownHosts.Any(k => !collectedKeys.Contains(k));

        // Adopt each host's tmux epoch, capturing the prior sweep so a host first seen this run does not
        // hand a genuinely observed rival a witnessed order it never earned.
        var sweptBefore = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (IGrouping<string?, TmuxPane> host in collected.Panes.Where(p => p.Epoch.Length > 0).GroupBy(p => p.Host))
        {
            string key = TargetId.ForHost(host.Key).Key;
            DateTimeOffset? prior = history.SweptAt(host.Key);
            bool continuous = history.AdoptEpoch(host.Key, host.First().Epoch, now);
            sweptBefore[key] = continuous ? prior : null;
        }

        // A host that answered with no windows contributed no pane and no epoch, so the loop above never
        // saw it. Record it anyway, exactly as `waiting` does: an empty successful sweep is evidence the
        // host was observed, and if it never enters KnownHosts a later run that omits it reads its narrowed
        // view as complete. No epoch is claimed, so continuity is not invented across the empty gap.
        var hostsWithPanes = collected.Panes.Select(p => TargetId.ForHost(p.Host).Key).ToHashSet(StringComparer.Ordinal);
        foreach (string? host in collected.CollectedHosts)
        {
            if (!hostsWithPanes.Contains(TargetId.ForHost(host).Key))
            {
                history.RecordSweptEmpty(host, now);
            }
        }

        // Observe every collected window, claiming or not: the history is shared with `waiting`, and a run
        // that recorded only the claimants would prune the rest and reset their silence. A pane that now
        // identifies no PR — absent, malformed, or an issue — is observed with a null claim, which clears
        // its stale registration and provenance while keeping its digest, so a window that owned this PR,
        // fell silent, and later reclaimed it cannot inherit its old place in the queue. Keyed by the
        // structured target id and pane id, because a pane id is unique only within one tmux server.
        var silence = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        foreach ((TmuxPane pane, AgentState? state) in parsed)
        {
            int? claimedPr = state is { IsIssue: false } id ? id.PrNumber : null;
            bool registrationWitnessed = viewComplete && sweptBefore.GetValueOrDefault(TargetId.ForHost(pane.Host).Key) is not null;
            silence[Claim.Key(pane)] = history.Observe(pane, now, claimedPr, registrationWitnessed);
        }

        IReadOnlyDictionary<string, Claim> claims = Claim.Register(
            claimants,
            history.ClaimedAt,
            history.IsWitnessed,
            viewComplete);

        // Persist history only for the hosts actually collected, so a partial sweep keeps what it did not
        // look at rather than deleting the claim memory of unreachable or unrequested hosts. The attempted
        // set is recorded as persistent fleet membership — the same distinction `waiting` persists — so a
        // target that failed on its first attempt is remembered and a later omission reads as narrowed.
        history.Save(collected.Panes, collected.CollectedHosts, collected.AttemptedHosts);

        // This PR's claimants, owner first. The rank comes from Claim.Register, so followers sort after the
        // owner; ties break on the same fixed key the sweep uses, so the owner is the same window in both.
        (TmuxPane Pane, AgentState State, Claim Claim)[] mine = [.. parsed
            .Where(r => r.State is { IsIssue: false } s && s.PrNumber == prNumber)
            .Select(r => (r.Pane, State: r.State!, Claim: claims.GetValueOrDefault(Claim.Key(r.Pane), Claim.Sole)))
            .OrderBy(m => m.Claim.IsFollower ? 1 : 0)
            .ThenBy(m => history.ClaimedAt(m.Pane) ?? DateTimeOffset.MaxValue)
            .ThenBy(m => m.Pane.Host ?? string.Empty, StringComparer.Ordinal)
            .ThenBy(m => m.Pane.PaneId, StringComparer.Ordinal)];

        PrFetch fetched = await fetchAsync(prNumber, ct);
        PrFacts? prFacts = fetched.Facts;
        if (prFacts is not null && !prFacts.MergeabilityKnown && !prFacts.Merged)
        {
            prFacts = await refreshMergeabilityAsync(prNumber, ct) is { MergeabilityKnown: true } refreshed
                && string.Equals(refreshed.HeadSha, prFacts.HeadSha, StringComparison.OrdinalIgnoreCase)
                    ? prFacts with { MergeableState = refreshed.MergeableState }
                    : prFacts;
        }

        return new PrLocation(prNumber, mine, prFacts, fetched.Status, viewComplete, collected, silence)
        {
            Searched = fetched.Searched,
            FoundIn = fetched.FoundIn,
            Configured = fetched.Configured,
        };
        }
        finally
        {
            if (ownsHistory)
            {
                history.Dispose();
            }
        }
    }

    internal static void WriteReport(TextWriter output, PrLocation located, DateTimeOffset now)
    {
        int prNumber = located.PrNumber;
        IReadOnlyList<(TmuxPane Pane, AgentState State, Claim Claim)> claims = located.Claims;
        PrFacts? facts = located.Facts;
        WaitingCommand.Collection collected = located.Collected;
        IReadOnlyDictionary<string, TimeSpan?> silence = located.Silence;
        bool viewComplete = located.ViewComplete;

        // Lead the first line with a stable token aligned to the exit code, so a harness sees the failure
        // before the details rather than a success-shaped `PR #…` above a later NARROWED/UNREACHABLE line.
        // The reasons themselves still follow in the body below; this is the one-line summary.
        string titleSuffix = facts?.Title is { Length: > 0 } title ? $"  {DisplayText.Safe(title)}" : string.Empty;
        string scope = located.Searched.Count > 0 ? string.Join(", ", located.Searched.Select(DisplayText.Safe)) : "the searched repo(s)";
        string configuredScope = located.Configured.Count > 0 ? string.Join(", ", located.Configured.Select(DisplayText.Safe)) : scope;
        string ambiguousRepos = located.FoundIn.Count > 0 ? string.Join(", ", located.FoundIn.Select(DisplayText.Safe)) : scope;
        string foundInText = string.Join(", ", located.FoundIn.Select(DisplayText.Safe));
        IReadOnlyList<string> unsearched = located.Fetch.Unsearched;
        string partialTail = unsearched.Count > 0
            ? $"{string.Join(", ", unsearched.Select(DisplayText.Safe))} not searched (budget spent)"
            : "part of the scope could not be read";

        // A partial hit — GitHub could not be read across the whole scope, but the PR *was* found in at
        // least one repo — is not the same as a blank outage: existence is proven, only uniqueness is not.
        bool partialHit = located.Disposition == PrDisposition.Unavailable && located.FoundIn.Count > 0;
        string headline = located.Disposition switch
        {
            PrDisposition.Partial => $"PARTIAL PR #{prNumber}{titleSuffix} — fleet partly unreachable; a claim may be on a host not swept",
            PrDisposition.Narrowed => $"NARROWED PR #{prNumber}{titleSuffix} — fewer hosts than collected before; a claim may be on a host not swept this run",
            PrDisposition.NotFound => $"NOTFOUND PR #{prNumber}{titleSuffix} — no window claims it and no such PR in {scope}",
            PrDisposition.Unavailable when partialHit => $"PARTIAL PR #{prNumber}{titleSuffix} — found in {foundInText}, but uniqueness unproven; {partialTail}",
            PrDisposition.Unavailable => $"PARTIAL PR #{prNumber}{titleSuffix} — no window claims it and GitHub could not be read; existence unknown",
            PrDisposition.Ambiguous => $"AMBIGUOUS PR #{prNumber}{titleSuffix} — #{prNumber} exists in {ambiguousRepos}; pass a single --repo to choose",
            _ => $"PR #{prNumber}{titleSuffix}",
        };
        output.WriteLine(headline);

        // Name the scope, truthfully separating what was configured from what was actually searched, so a
        // cross-repo miss is diagnosed as "wrong scope" and an early exit does not read as a full search.
        // Placed under the token line, never on it.
        if (located.Configured.Count > 0)
        {
            bool sameScope = located.Searched.Count == located.Configured.Count
                && located.Searched.SequenceEqual(located.Configured, StringComparer.OrdinalIgnoreCase);
            output.WriteLine(sameScope
                ? $"  scope     searched {scope}"
                : $"  scope     configured {configuredScope}; searched {scope}");
        }

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
            // follower is still working, so ownership is with the window that is doing the least. The
            // owner's verdict is read through the same pane-activity gate the verdict lines use, so a
            // working owner with a stale rec is never mistaken for one that has finished and handed over.
            if (Claim.IsReleasing(claims[0].State, VerdictFor(claims[0].Pane, claims[0].State, located.Fetch), claims[0].Pane.Activity)
                && claims.Skip(1).Any(c => c.Pane.Activity == PaneActivity.Working))
            {
                output.WriteLine("            the owner is disengaging while a follower is active — consider promoting it");
            }
        }

        output.WriteLine($"  github    {Github(facts, located.Github, now, scope, ambiguousRepos, foundInText, partialTail)}");

        // One verdict per claim when contested: the disagreement between them is the finding, and
        // collapsing it to a single row would hide which window the answer came from. Emitted for every
        // claim even when GitHub could not be read: VerdictFor gates on pane activity first, so a
        // working/blocked/stalled/unreadable pane resolves from what it is doing — no GitHub needed — and
        // an idle pane's explicit `rec=stop`/`rec=approve` escalation still reaches the operator with the
        // PR unread. Only the genuinely GitHub-dependent idle outcomes fall to a low-confidence Unknown,
        // which is truthful rather than an invented readiness.
        foreach ((TmuxPane pane, AgentState state, _) in claims)
        {
            WaitingVerdict verdict = VerdictFor(pane, state, located.Fetch);
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

        if (state.Source == StateSource.WindowName)
        {
            // Identity came from the window name, not the record. Saying "published no state" is false when
            // the agent published fields but omitted its pr/issue — #164 established that a record can carry
            // rec, reviews and the rest while its identity is read from the name. So the lead names the one
            // thing certainly missing, the identity (the shared wording WaitingVerdict uses), and the
            // published fields, if any, still follow.
            const string Lead = "published no identity; identity read from the window name";
            return parts.Count == 0 ? Lead : $"{Lead}, {string.Join(", ", parts)}";
        }

        return string.Join(", ", parts);
    }

    /// <summary>
    /// The verdict for one claiming window, gated on what its pane is doing right now. A published record
    /// is only ever read as a handover — and only then can it resolve to an actionable READY — when the
    /// pane is idle; a working, blocked, stalled or unreadable pane keeps the corresponding UNKNOWN or
    /// operator verdict instead. This is <see cref="WaitingVerdict.ForActivity"/>, the single copy of that
    /// policy `octoshift waiting` uses, so `pr` cannot print a verdict `waiting` would not.
    /// </summary>
    private static WaitingVerdict VerdictFor(TmuxPane pane, AgentState state, PrFetch fetch)
        => WaitingVerdict.ForActivity(pane.Activity, pane.Capture, () => WaitingVerdict.Resolve(state, fetch));

    private static string Github(PrFacts? facts, PrFetchStatus outcome, DateTimeOffset now, string scope, string ambiguousRepos, string foundInText, string partialTail)
    {
        if (facts is null)
        {
            // Keep the body honest about which null outcome this was: every searched repo answered 404,
            // the number collides across repos, a partial hit whose uniqueness is unproven, or GitHub could
            // not be read at all. Each has a different remedy — widen the scope, narrow it, retry, or wait.
            return outcome switch
            {
                PrFetchStatus.NotFound => $"no such PR in {scope}",
                PrFetchStatus.Ambiguous => $"AMBIGUOUS — exists in {ambiguousRepos}; pass a single --repo to choose",
                PrFetchStatus.Unavailable when foundInText.Length > 0
                    => $"found in {foundInText}; uniqueness unproven ({partialTail})",
                _ => "could not be read",
            };
        }

        // Name the repo a single-repo resolution landed in, so the reader is never left inferring scope.
        string repoSuffix = facts.Repo is { Length: > 0 } repo ? $" [{DisplayText.Safe(repo)}]" : string.Empty;

        if (facts.Merged)
        {
            // The question behind "where is it" is often "did I already land this", and how long ago is
            // what turns that from a fact into an orientation.
            string ago = facts.MergedAt is { } mergedAt && mergedAt <= now ? $" {Duration(now - mergedAt)} ago" : string.Empty;
            return $"merged{ago}{repoSuffix}";
        }

        if (string.Equals(facts.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return $"closed without merging{repoSuffix}";
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

        return string.Join(" · ", parts) + $" · head {Short(facts.HeadSha)}{repoSuffix}";
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

        // The producer-owned repo labels: the full configured scope, the subset actually searched (narrower
        // when an early exit stopped the search), of those where the PR was found, and the single repo it
        // resolved to. Naming all four lets a consumer tell a wrong-scope miss from an outage, diagnose an
        // ambiguous collision, and see a partial hit whose uniqueness could not be established.
        writer.WriteStartArray("configured");
        foreach (string repo in located.Configured)
        {
            writer.WriteStringValue(repo);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("searched");
        foreach (string repo in located.Searched)
        {
            writer.WriteStringValue(repo);
        }

        writer.WriteEndArray();
        writer.WriteStartArray("foundIn");
        foreach (string repo in located.FoundIn)
        {
            writer.WriteStringValue(repo);
        }

        writer.WriteEndArray();
        if (facts?.Repo is { Length: > 0 } resolvedRepo)
        {
            writer.WriteString("repo", resolvedRepo);
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

            // The verdict, gated on pane activity exactly as the human report gates it, so the two surfaces
            // answer the same claim the same way — a working/blocked/stalled/unreadable pane never carries a
            // resolved READY here while the human line shows UNKNOWN. Emitted for every claim even when
            // GitHub could not be read: VerdictFor resolves the non-idle activities and an idle explicit
            // escalation without facts, and only the genuinely GitHub-dependent idle outcomes fall to a
            // low-confidence unknown, matching the human report which now also prints a verdict per claim.
            WaitingVerdict verdict = VerdictFor(pane, state, located.Fetch);
            writer.WriteStartObject("verdict");
            writer.WriteString("state", verdict.State.ToString().ToLowerInvariant());
            writer.WriteString("confidence", verdict.Assurance.Label);
            writer.WriteString("reason", verdict.Reason);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("contested", claims.Count > 1);
        if (claims.Count > 1)
        {
            writer.WriteString("order", claims[0].Claim.Basis.ToString().ToLowerInvariant());
        }

        // A partly invisible fleet is named in the output as well as the exit code: the requested PR may
        // be claimed on a host that did not answer, or on one this run omitted, so success-shaped JSON that
        // omitted the failure would assert a completeness the sweep did not have. This mirrors the exit
        // code, which fails on any incomplete view.
        writer.WriteBoolean("viewComplete", viewComplete);
        writer.WriteStartArray("unreachable");
        foreach (string failure in collected.Unreachable)
        {
            writer.WriteStringValue(failure);
        }

        writer.WriteEndArray();

        // The GitHub outcome, kept truthful and distinct: a null read is either an affirmative 404 or an
        // unavailable read, and JSON that omitted the difference (only the facts block below, absent in
        // both) would let a consumer read an outage as "no such PR". Mirrors the human first-line token.
        writer.WriteString("github", located.Github switch
        {
            PrFetchStatus.Found => "found",
            PrFetchStatus.NotFound => "notfound",
            PrFetchStatus.Ambiguous => "ambiguous",
            _ => "unavailable",
        });

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
