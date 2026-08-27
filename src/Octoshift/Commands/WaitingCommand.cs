namespace Octoshift.Commands;

using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>Whether this window's work is over, and what to do about it.</summary>
    public Retirement Retirement { get; init; } = Retirement.None;

    /// <summary>Ways whatever was published contradicts its own contract, identified or not.</summary>
    public IReadOnlyList<string> Defects => Record?.Defects ?? Unidentified?.Defects ?? [];

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
    /// <summary>Windows that were present at the last sweep and are gone now. Reported, not swallowed.</summary>
    internal static IReadOnlyList<string> Departed { get; private set; } = [];

    /// <summary>Hosts collected before but not in this run, so the view is narrower than it has been.</summary>
    internal static IReadOnlyList<string> Omitted { get; private set; } = [];

    public static async Task<int> RunAsync(string? repoFlag, IReadOnlyList<string> hosts, bool all, bool json, bool rename, CancellationToken ct, string? historyPath = null)
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

        var facts = new GhPrFactsSource(
            repo,
            new FileConditionalCache(),
            (args, token) => GhAuthenticatedRunner.RunGhAsync(args, null, token));

        try
        {
            FleetResult result = await CollectAndResolveAsync(
                hosts, (host, token) => new TmuxScanner(host).ScanAsync(token),
                facts.FetchAsync, facts.RefreshMergeabilityAsync, now: null, ct, historyPath: historyPath);

            // An explicitly empty fleet is its own disposition, not a quiet sweep and not a failure: the
            // operator retired every target, so there is nothing to sweep. Lead with a distinct EMPTY token
            // (its own truthful contract, separate from the #169 total-failure gap) and succeed, so a
            // harness can tell "nothing declared" from "nothing found".
            if (result.EmptyFleet)
            {
                if (json)
                {
                    WriteEmptyFleetJson(Console.OpenStandardOutput());
                }
                else
                {
                    Console.Out.WriteLine("EMPTY the declared fleet is empty; nothing to sweep — add a target with 'octoshift fleet add'");
                }

                return ExitCode.Ok;
            }

            // Total failure keeps its own path. A sweep where nothing could be collected is not a quiet
            // fleet, and printing a QUIET summary above the failure inverts which of the two the reader
            // sees first. CollectAndResolveAsync has already persisted the discontinuity under the
            // transaction and returns the failure text (including any persistence failure) with null rows.
            if (result.Rows is null)
            {
                if (json)
                {
                    WriteJsonError(Console.OpenStandardOutput(), string.Join("; ", result.Failures));
                }
                else
                {
                    foreach (string failure in result.Failures)
                    {
                        Console.Error.WriteLine($"octoshift: {DisplayText.Safe(failure)}");
                    }
                }

                return ExitCode.Unavailable;
            }

            Collection collected = result.Collected;
            IReadOnlyList<WaitingRow> resolved = result.Rows;

            int renameFailures = 0;
            if (rename)
            {
                // Diagnostics (RENAMED, and the failure/skip lines) go to stderr, never stdout, so a
                // --json --rename run leaves a single valid JSON document on stdout.
                renameFailures = await RenameAsync(resolved, ShellRunner.For, Console.Error, ct);
            }

            IReadOnlyList<WaitingRow> shown = Present(resolved, all);
            if (json)
            {
                WriteJson(Console.OpenStandardOutput(), shown, Budget.From(facts), collected.Unreachable, Omitted, Departed);
            }
            else
            {
                WriteTable(Console.Out, shown, Budget.From(facts), collected.Unreachable, Omitted, Departed);
            }

            // A partly invisible fleet is not a clean sweep, so a single failed host — or a previously
            // collected host this run omitted — still costs the exit code even though every other host's
            // rows were printed. A rename that could not be confirmed does too, so a harness is not told
            // everything was corrected when some of it was not. Omitted is set by ResolveAllAsync and
            // reflects this run.
            return collected.AnyFailure || Omitted.Count > 0 || renameFailures > 0 ? ExitCode.Unavailable : ExitCode.Ok;
        }
        catch (HistoryUnavailableException ex)
        {
            // A history failure — a malformed or unreadable file, a lock that could not be taken, or a
            // persistence write that did not land — leaves fleet ownership unknown. The human output leads
            // its first stdout line with the same PARTIAL token the sweep's own summary and trailer lines
            // use, so a harness sees the disposition before the diagnostic, matching the unavailable exit;
            // the specific cause goes to stderr. JSON stays a single error document, never a token prepended
            // to it. A genuine caller cancellation is a different exception, not caught here, so it
            // propagates without a token. The separate total-collection-failure stdout-token gap is #169.
            if (json)
            {
                WriteJsonError(Console.OpenStandardOutput(), ex.Message);
            }
            else
            {
                Console.Out.WriteLine("PARTIAL pane history unavailable; fleet ownership is unknown");
                Console.Error.WriteLine($"octoshift: {DisplayText.Safe(ex.Message)}");
            }

            return ExitCode.Unavailable;
        }
    }

    /// <summary>A completed sweep: its collection, its resolved rows (null on total failure), and, on
    /// total failure, the messages to report. <see cref="EmptyFleet"/> is the distinct disposition of a
    /// fleet deliberately emptied by retirement — no target was even attempted, so it is neither a quiet
    /// sweep nor a failure.</summary>
    internal readonly record struct FleetResult(
        Collection Collected,
        IReadOnlyList<WaitingRow>? Rows,
        IReadOnlyList<string> Failures,
        bool EmptyFleet = false);

    /// <summary>
    /// The product core of a sweep: acquire the history transaction, collect, and reconcile — in that
    /// order, so the cross-process lock brackets the whole collect→reconcile→save as one unit. This is
    /// what stops a slower, older scan from committing its stale snapshot after a newer sweep has already
    /// recorded fresh state: a concurrent waiting/pr blocks on <see cref="PaneHistory.OpenAsync"/> until
    /// this transaction commits and releases, so whoever collects does so at-or-after the previous
    /// committer's snapshot, never before it. The scan and GitHub fetchers are injected so the ordering is
    /// testable without ssh or a network; <paramref name="historyPath"/> and
    /// <paramref name="perTargetTimeout"/> are internal seams for the same reason. The lock is released by
    /// <see cref="ResolveAllAsync"/>'s Save before its GitHub reads (or by the total-failure Save here), so
    /// the network is never held under it; the finally disposes the transaction on any early exit.
    /// </summary>
    internal static async Task<FleetResult> CollectAndResolveAsync(
        IReadOnlyList<string> hosts,
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanAsync,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
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

            // Collect the whole declared fleet, not merely the run's --host arguments. The history is open,
            // so the remembered members are decoded and folded into the target set here: a bare sweep
            // reaches every host ever attempted (local plus remotes), and a --host run adds to that rather
            // than narrowing to it. This is what makes a complete view reachable — the local-then---host
            // ordering that used to leave completeness permanently unsatisfiable now converges — while the
            // attempted membership still grows, so the round-9 safety is intact.
            IReadOnlyList<string?> targets = history.FleetTargets(hosts);

            // An explicitly empty fleet — established, then emptied by retirement, with no --host request —
            // has no target to attempt. That is neither a quiet sweep nor a total failure (no host was even
            // asked), so it is reported as its own disposition. Membership is unchanged, so nothing is
            // saved; the finally releases the transaction lock.
            if (targets.Count == 0)
            {
                return new FleetResult(new Collection([], [], 0, [], []), [], [], EmptyFleet: true);
            }

            Collection collected = await CollectTargetsAsync(targets, scanAsync, ct, perTargetTimeout);

            // Sample the registration clock only now — inside the held transaction, after collection.
            // Sampling it in RunAsync, before the wait for the lock, is the bug: lock acquisition is not
            // fair, so a transaction that started waiting first can commit second and would stamp its new
            // claimant with the earlier time it read while queued, inverting a witnessed order. Clamping
            // against the persisted history closes the same gap for a clock stepped backwards. A test may
            // inject the sample (still clamped, still after the lock); production reads the wall clock here.
            DateTimeOffset stamped = history.TransactionTime(now ?? DateTimeOffset.UtcNow);

            if (collected.TotalFailure)
            {
                // A totally failed sweep is still a completed sweep: every previously known host went
                // uncollected, so its continuity must be broken on disk — or a witnessed order would
                // survive a run that saw nothing and be read as current next time. Persist that, under the
                // same held transaction, before reporting. The attempted set is recorded too, so a target
                // that failed on its first ever attempt is remembered as fleet membership rather than lost
                // with the sweep. A persistence failure only adds to the failure already being reported, so
                // it is folded into the same unavailable output. (The stdout-token gap on this path is the
                // separate #169; recording attempted membership here does not change that public behavior.)
                var failures = new List<string>(collected.Unreachable);
                try
                {
                    history.Save([], collected.CollectedHosts, collected.AttemptedHosts);
                }
                catch (HistoryUnavailableException ex)
                {
                    failures.Add(ex.Message);
                }

                return new FleetResult(collected, null, failures);
            }

            IReadOnlyList<WaitingRow> resolved = await ResolveAllAsync(
                collected.Panes, fetchAsync, refreshMergeabilityAsync, stamped, ct,
                collected.CollectedHosts, allHostsAnswered: collected.Unreachable.Count == 0, history: history,
                attemptedHosts: collected.AttemptedHosts);

            return new FleetResult(collected, resolved, []);
        }
        finally
        {
            history?.Dispose();
        }
    }

    /// <summary>What one sweep managed to collect, and from how many targets it tried.</summary>
    /// <param name="Panes">Windows collected, in target order.</param>
    /// <param name="Unreachable">One message per target that failed, already naming the target.</param>
    /// <param name="Targets">How many distinct targets were asked, so total failure can be told from partial.</param>
    /// <param name="CollectedHosts">
    /// The targets that answered — <c>null</c> for the local machine. A target that answered with no
    /// windows is here, because an empty successful sweep is evidence the host was observed, not a host
    /// that was skipped. Bound to the exact successful set rather than inferred from pane presence, so a
    /// quiet host still counts toward a complete view and still has its history pruned.
    /// </param>
    /// <param name="AttemptedHosts">
    /// Every target this sweep <em>tried</em> to reach — <c>null</c> for the local machine — whether or not
    /// it answered, so it is a superset of <see cref="CollectedHosts"/>. Carried as identities, not a
    /// count, so a host attempted for the first time and failing before it ever collected can still be
    /// persisted as fleet membership: that is what lets a later sweep that omits it tell its view narrowed
    /// rather than reading a sole claim as owned while a rival may run on the unreached host.
    /// </param>
    internal readonly record struct Collection(
        IReadOnlyList<TmuxPane> Panes,
        IReadOnlyList<string> Unreachable,
        int Targets,
        IReadOnlyList<string?> CollectedHosts,
        IReadOnlyList<string?> AttemptedHosts)
    {
        /// <summary>Nothing was collected anywhere. Reported as a failure, never as a quiet fleet.</summary>
        public bool TotalFailure => Panes.Count == 0 && Unreachable.Count == Targets;

        /// <summary>At least one target could not be read, whatever the others returned.</summary>
        public bool AnyFailure => Unreachable.Count > 0;
    }

    /// <summary>
    /// How long one target gets before it is abandoned as unreachable. Generous on purpose: an entire
    /// three-host sweep has been observed at roughly four seconds, so a single target that is still
    /// running after thirty is not slow, it is stuck. ssh's own <c>ConnectTimeout</c> only bounds
    /// establishing the connection; a host that connects and then hangs inside the remote shell or tmux
    /// is unbounded without this, and would block every later target and the partial report forever.
    /// </summary>
    internal static readonly TimeSpan DefaultTargetTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Collects from each distinct target in turn. Injectable scan so fan-out, deduplication and partial
    /// failure are testable without ssh or a tmux server. This overload takes the run's <c>--host</c>
    /// arguments and reaches exactly those (the local machine when none are given); production goes through
    /// <see cref="CollectTargetsAsync"/> with the full declared fleet instead, so a sweep covers everything
    /// remembered rather than only what this invocation named.
    /// </summary>
    /// <remarks>
    /// Repeats are dropped in first-seen order: naming an alias twice is a typo, and honouring it would
    /// buy a second ssh connection and a duplicate of every row and count that host contributes.
    /// </remarks>
    internal static Task<Collection> CollectAsync(
        IReadOnlyList<string> hosts,
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanAsync,
        CancellationToken ct,
        TimeSpan? perTargetTimeout = null)
        => CollectTargetsAsync(
            hosts.Count > 0 ? [.. HostTarget.Distinct(hosts)] : [null],
            scanAsync, ct, perTargetTimeout);

    /// <summary>
    /// Collects from an explicit target list — <c>null</c> for the local machine, an alias for a remote —
    /// in order. This is the core the production path uses with the whole declared fleet
    /// (<see cref="PaneHistory.FleetTargets"/>); <see cref="CollectAsync"/> is the thin wrapper that turns
    /// a run's <c>--host</c> arguments into a target list for the tests that drive collection directly.
    /// </summary>
    /// <remarks>
    /// Repeats are dropped by target identity in first-seen order, so a member that a <c>--host</c>
    /// argument also names is reached once, not twice. Each target runs under a linked token that also
    /// fires after <paramref name="perTargetTimeout"/> (default <see cref="DefaultTargetTimeout"/>). A
    /// target that trips its own deadline is recorded as unreachable and the sweep moves on, so one hung
    /// host cannot hold the others hostage. The caller's <paramref name="ct"/> is different in kind: when
    /// it is what cancelled, the cancellation propagates rather than being laundered into an unreachable
    /// host — and it escapes carrying exactly <paramref name="ct"/>, never the internal linked token, so
    /// the caller sees its own token back. <paramref name="perTargetTimeout"/> is an internal seam so a
    /// test can drive the deadline in milliseconds without a wall-clock wait.
    /// </remarks>
    internal static async Task<Collection> CollectTargetsAsync(
        IReadOnlyList<string?> requested,
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanAsync,
        CancellationToken ct,
        TimeSpan? perTargetTimeout = null)
    {
        TimeSpan timeout = perTargetTimeout ?? DefaultTargetTimeout;

        // Deduplicate by target identity, not raw string, so the local sentinel and a remote alias cannot
        // collide and a member already reached is not swept twice.
        var targets = new List<string?>();
        var seenTargets = new HashSet<string>(StringComparer.Ordinal);
        foreach (string? host in requested)
        {
            if (seenTargets.Add(TargetId.ForHost(host).Key))
            {
                targets.Add(host);
            }
        }

        var panes = new List<TmuxPane>();
        var unreachable = new List<string>();
        var collectedHosts = new List<string?>();
        foreach (string? host in targets)
        {
            // Linked to the caller's token so real cancellation still reaches ShellRunner and takes the
            // ssh/tmux tree down; CancelAfter adds the per-target deadline on top of it.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            try
            {
                panes.AddRange(await scanAsync(host, linked.Token));

                // A host that answered is collected, whether or not it had any windows on it. Recorded
                // here rather than inferred from pane presence: an empty successful sweep still proves the
                // host was seen, which is what keeps a quiet host counting toward a complete view and its
                // stale history pruned instead of retained forever.
                collectedHosts.Add(host);
            }
            catch (TmuxUnavailableException ex)
            {
                // Caller cancellation dominates a target failure: if the outer token is already
                // cancelled, a scanner/process/parsing loss racing it must not launder the cancellation
                // into an unreachable host — escape with the caller's token instead.
                ct.ThrowIfCancellationRequested();

                // One unreachable host must not hide the others, and must not be silently absorbed
                // either — a fleet that is partly invisible looks exactly like a fleet that is quiet.
                unreachable.Add(ex.Message);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Real cancellation, not a per-target deadline. The exception was raised on the linked
                // token, but the caller is owed exactly the token it passed in — re-throw on ct so the
                // escaping OperationCanceledException carries ct, not the internal linked token.
                ct.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                // The per-target deadline fired, not the caller. Same rule as an unreachable host: record
                // it and keep going, so a host that connected and then hung cannot bury the partial report.
                unreachable.Add(TimedOut(host, timeout));
            }
        }

        // A cancellation that lands after the final scan and catch — between the last callback and this
        // return — is still the caller's, and must surface rather than yield a quietly completed report.
        ct.ThrowIfCancellationRequested();

        // Attempted membership is the full distinct target set — every host asked, answering or not — so a
        // first-time failure is still persisted as fleet membership rather than forgotten.
        return new Collection(panes, unreachable, targets.Count, collectedHosts, targets);
    }

    /// <summary>
    /// The unreachable message for a target that ran past its deadline. Names the local machine or the
    /// remote host so the report says which one hung, and — like every other unreachable message — is
    /// escaped at the output boundary, so an alias carrying terminal control sequences is reported here
    /// as text rather than executed.
    /// </summary>
    private static string TimedOut(string? host, TimeSpan timeout)
    {
        string detail = string.Create(
            CultureInfo.InvariantCulture,
            $"tmux scan timed out after {timeout.TotalSeconds:0.###}s");
        return host is null ? $"local: {detail}" : $"{host}: {detail}";
    }

    /// <summary>
    /// Joins panes with GitHub and returns the rows a run would show. A test helper: injectable fetch so
    /// the whole selection and ordering policy is testable without tmux or a network. When no history is
    /// supplied it isolates to a throwaway file rather than the shared default path, so a single-shot row
    /// test neither pollutes nor reads another run's state.
    /// </summary>
    internal static async Task<IReadOnlyList<WaitingRow>> BuildRowsAsync(
        IReadOnlyList<TmuxPane> panes,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset now,
        bool all,
        CancellationToken ct,
        IReadOnlyList<string?>? collectedHosts = null,
        bool allHostsAnswered = true,
        PaneHistory? history = null)
    {
        string? tempPath = history is null
            ? Path.Combine(Path.GetTempPath(), $"octoshift-buildrows-{Guid.NewGuid():N}.json")
            : null;
        try
        {
            return Present(
                await ResolveAllAsync(panes, fetchAsync, refreshMergeabilityAsync, now, ct, collectedHosts, allHostsAnswered, history, tempPath),
                all);
        }
        finally
        {
            if (tempPath is not null)
            {
                TryDelete(tempPath);
                TryDelete(tempPath + ".lock");
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Resolves every pane into a row — the complete fleet, before the presentation filter. Ownership is
    /// decided here, across every host, because the two halves of a contest are often not on one machine.
    /// </summary>
    /// <remarks>
    /// Every window that identifies a PR contends for it, whatever it is doing right now: a window
    /// mid-turn, one blocked on a prompt, one stalled and one idle all hold the same claim, and leaving
    /// any of them out of the contest hands the PR to whoever is left — a deterministic ranking that
    /// silently awards ownership to the one idle rival is the failure this guards against. The GitHub
    /// lookup, by contrast, is spent only on an idle window, because idle is the one state a published
    /// record is taken as a handover in and the only state a verdict may be acted on in.
    /// </remarks>
    internal static async Task<IReadOnlyList<WaitingRow>> ResolveAllAsync(
        IReadOnlyList<TmuxPane> panes,
        Func<int, CancellationToken, Task<PrFacts?>> fetchAsync,
        Func<int, CancellationToken, Task<PrFacts?>> refreshMergeabilityAsync,
        DateTimeOffset now,
        CancellationToken ct,
        IReadOnlyList<string?>? collectedHosts,
        bool allHostsAnswered,
        PaneHistory? history = null,
        string? historyPath = null,
        IReadOnlyList<string?>? attemptedHosts = null)
    {
        Departed = [];
        Omitted = [];

        // Window names that appear twice on one host. A tmux name is not unique by construction, and a
        // duplicate is evidence that a rename went somewhere it did not belong rather than a coincidence.
        // Keyed by the structured target id so an alias containing the composite separator cannot forge or
        // mask an ambiguity across hosts.
        HashSet<string> ambiguousNames = [.. panes
            .Where(p => p.WindowName.Length > 0)
            .GroupBy(p => TargetId.ForHost(p.Host).ComposeWith(p.WindowName), StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)];

        // Read every pane once, with the duplicate-name and pane-corroboration safeguards fed in: a shared
        // name identifies nothing, and a pane whose own output never mentions the PR its state claims is a
        // state that may have been written by another agent.
        var readings = new List<(TmuxPane Pane, StateReading Reading)>(panes.Count);
        var claimants = new List<(TmuxPane Pane, int PrNumber, int? Round)>();

        foreach (TmuxPane pane in panes)
        {
            StateReading reading = AgentState.Read(
                pane.AgentStateOption,
                pane.WindowName,
                nameIsAmbiguous: ambiguousNames.Contains(TargetId.ForHost(pane.Host).ComposeWith(pane.WindowName)),
                paneContradictsPr: pr => TmuxScanner.PaneContradictsPr(pane.Capture, pr));
            readings.Add((pane, reading));

            if (reading.Identified is { IsIssue: false } claimant)
            {
                claimants.Add((pane, claimant.PrNumber, claimant.Round));
            }
        }

        // Open the shared history for a serialized transaction and hold it across the whole reconcile.
        // In production the transaction is acquired by RunAsync *before* collection and injected here, so
        // the cross-process lock brackets collect→reconcile→save as one unit: a slower, older scan cannot
        // commit its stale snapshot after a newer sweep, because a concurrent waiting/pr only proceeds once
        // this transaction commits and releases. The reconcile below — adopt, observe, register, save — is
        // entirely local; the GitHub reads happen *after* Save releases the lock, so the network is never
        // under it. A test may inject its own history (owning its lifetime, keeping the direct lock-free
        // constructor) or a historyPath to open one here; the finally is the safety net for an early exit.
        bool ownsHistory = history is null;
        history ??= await PaneHistory.OpenAsync(historyPath, ct);
        IReadOnlyDictionary<string, Claim> claims;
        var silence = new Dictionary<string, TimeSpan?>(StringComparer.Ordinal);
        try
        {

        // A host that did not answer and a host nobody asked about produce the same thing: a view with
        // windows missing from it. The second is invisible from the arguments alone — a host not named is
        // indistinguishable from a host that does not exist — so it is caught by remembering which hosts
        // have been collected before and noticing when a run covers fewer of them. The collected set is
        // the exact successful target set, so a host that answered empty still counts as seen.
        IReadOnlyList<string?> collected = collectedHosts ?? [.. panes.Select(p => p.Host).Distinct()];
        var collectedKeys = collected.Select(h => TargetId.ForHost(h).Key).ToHashSet(StringComparer.Ordinal);
        string[] omitted = [.. history.KnownHosts.Where(k => !collectedKeys.Contains(k)).Select(k => TargetId.FromKey(k).Display)];
        bool viewComplete = allHostsAnswered && omitted.Length == 0;
        Omitted = omitted;

        // Adopt each host's tmux epoch, capturing whether it was swept in full under this same server
        // before this run. A registration time only orders a claim if its host was already under
        // observation when the time was recorded; a host first seen this run has "now" for every window on
        // it, which is a first look rather than an appearance. Ranking a genuinely-observed rival against
        // one of those would launder a narrow view into a fleet-wide fact — which is why the prior sweep,
        // not this one's, is what a claim's registration is trusted against.
        var sweptBefore = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (IGrouping<string?, TmuxPane> host in panes.Where(p => p.Epoch.Length > 0).GroupBy(p => p.Host))
        {
            string key = TargetId.ForHost(host.Key).Key;
            DateTimeOffset? prior = history.SweptAt(host.Key);
            bool continuous = history.AdoptEpoch(host.Key, host.First().Epoch, now);
            sweptBefore[key] = continuous ? prior : null;
        }

        // A host that answered with no windows contributed no pane and no epoch, so the loop above never
        // saw it. Record it anyway: an empty successful sweep is evidence the host was observed, and if it
        // never enters KnownHosts a later run that omits it cannot tell the fleet narrowed. No epoch is
        // claimed, so a window reappearing on it next run is not treated as continuous across the gap.
        var hostsWithPanes = panes.Select(p => TargetId.ForHost(p.Host).Key).ToHashSet(StringComparer.Ordinal);
        foreach (string? host in collected)
        {
            if (!hostsWithPanes.Contains(TargetId.ForHost(host).Key))
            {
                history.RecordSweptEmpty(host, now);
            }
        }

        // Observe every collected pane, claiming or not, and register its claim before ranking. A pane
        // that now identifies no PR — absent, malformed, or an issue — is observed with a null claim,
        // which clears its stale registration and provenance while keeping its digest and silence: without
        // that, a window that owned a PR, went quiet, then reclaimed it would inherit its old place in the
        // queue ahead of a rival that claimed it in between. A new registration is witnessed only when its
        // host was under continuous observation before this run and the view was complete, so a claim seen
        // for the first time under a narrow view is recorded untrusted and stays that way.
        foreach ((TmuxPane pane, StateReading reading) in readings)
        {
            int? claimedPr = reading.Identified is { IsIssue: false } identified ? identified.PrNumber : null;
            bool registrationWitnessed = viewComplete && sweptBefore.GetValueOrDefault(TargetId.ForHost(pane.Host).Key) is not null;
            silence[Claim.Key(pane)] = history.Observe(pane, now, claimedPr, registrationWitnessed);
        }

        claims = Claim.Register(
            claimants,
            history.ClaimedAt,
            history.IsWitnessed,
            viewComplete);

        // Prune history only for the hosts actually collected. A window on a host that did not answer, or
        // that this run was not asked about, has not departed — it is merely unseen, and forgetting it
        // would manufacture a departure and discard its registration on every partial sweep. The attempted
        // set is recorded as fleet membership so a target that failed on its first attempt is not forgotten;
        // it defaults to the collected set when a caller does not distinguish them. Save is the commit: it
        // releases the cross-process lock, so everything above ran under it and everything below — the
        // GitHub reads — runs after it.
        Departed = history.Save(panes, collected, attemptedHosts);
        }
        finally
        {
            if (ownsHistory)
            {
                history.Dispose();
            }
        }

        // The transaction has committed and released the lock; only now is GitHub read, so the network is
        // never held under the cross-process lock. One fetch per PR, not per pane: #159 measured PRs
        // claimed by two windows at once, and the second window's question has the same answer as the
        // first's. Only idle claimants are resolved — a window mid-turn has handed nothing over, and an
        // issue-tracking window has no PR to ask pulls/{n} about — but every claimant already contested
        // above, under the lock, on the strength of the history alone.
        var seen = new Dictionary<int, PrFacts?>();
        foreach ((TmuxPane pane, StateReading reading) in readings)
        {
            if (pane.Activity == PaneActivity.Idle
                && reading.Identified is { IsIssue: false } toFetch
                && !seen.ContainsKey(toFetch.PrNumber))
            {
                seen[toFetch.PrNumber] = await fetchAsync(toFetch.PrNumber, ct);
            }
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

        var rows = new List<WaitingRow>(readings.Count);
        foreach ((TmuxPane pane, StateReading reading) in readings)
        {
            AgentState? record = reading.Identified;
            WaitingVerdict verdict = WaitingVerdict.ForActivity(pane.Activity, pane.Capture, () =>
                record is not null
                    ? WaitingVerdict.Resolve(record, record.IsIssue ? null : seen.GetValueOrDefault(record.PrNumber))
                    : reading.Unidentified is { } unusable
                        ? WaitingVerdict.Unidentified(unusable)
                        : new WaitingVerdict(
                            WaitingState.Unknown, RowOwner.Nobody, "no published state and no pr#### window name",
                            Assurance.Low("nothing identifies this window")));

            rows.Add(Row(pane, reading, verdict, now) with
            {
                Claim = claims.GetValueOrDefault(Claim.Key(pane), Claim.Sole),
                Retirement = Retirement.For(verdict, record, pane.Activity),
                SilentFor = silence.GetValueOrDefault(Claim.Key(pane)),
            });
        }

        return rows;
    }

    /// <summary>The rows a run shows: everything under <c>--all</c>, otherwise what needs a person, is
    /// contested, or contradicts itself. Longest wait first among the rows that need you.</summary>
    private static IReadOnlyList<WaitingRow> Present(IReadOnlyList<WaitingRow> rows, bool all)
        => [.. rows
            .Where(r => all || r.Verdict.NeedsAttention || r.Defects.Count > 0 || r.Claim.IsContested)
            .OrderByDescending(r => r.Verdict.NeedsAttention)
            .ThenBy(r => r.Verdict.Severity)
            .ThenByDescending(r => r.StoppedFor ?? TimeSpan.Zero)];

    /// <summary>
    /// Corrects window names the tool can see are wrong, one batched command per host. Only names that
    /// actually differ are touched, so a fleet already correct costs nothing.
    /// </summary>
    /// <remarks>
    /// Works over the complete resolved fleet, not the shown subset: a quiet or working window with a
    /// stale suffix is exactly the one the presentation filter drops, and it still needs correcting.
    /// Diagnostics go to <paramref name="diagnostics"/> — stderr in production — never stdout, so
    /// <c>--json --rename</c> leaves a single valid JSON document on stdout rather than a run of
    /// <c>RENAMED</c> lines before it. The shell runner and the diagnostics sink are injected so the whole
    /// decision is testable without a tmux server.
    /// </remarks>
    internal static async Task<int> RenameAsync(
        IReadOnlyList<WaitingRow> rows,
        Func<string?, Func<string, CancellationToken, Task<CommandResult>>> shellFor,
        TextWriter diagnostics,
        CancellationToken ct,
        TimeSpan? perHostTimeout = null)
    {
        TimeSpan timeout = perHostTimeout ?? DefaultTargetTimeout;
        int failures = 0;
        foreach (IGrouping<string?, WaitingRow> host in rows.GroupBy(r => r.Pane.Host))
        {
            string label = TargetId.ForHost(host.Key).Display;

            // Window ids duplicated across this host's rows are ambiguous. One-row-per-window collection
            // should never produce them, so a duplicate is a defect — and renaming by an id that names two
            // windows could rename the wrong one, so those are skipped conservatively, as is a row whose
            // window id was not captured.
            HashSet<string> duplicateWindowIds = [.. host
                .Where(r => r.Pane.WindowId.Length > 0)
                .GroupBy(r => r.Pane.WindowId, StringComparer.Ordinal)
                .Where(g => g.Count() > 1)
                .Select(g => g.Key)];

            var renames = new List<(TmuxPane Pane, string Desired)>();
            foreach ((TmuxPane pane, string desired) in RenamePlan(host))
            {
                if (pane.WindowId.Length == 0 || duplicateWindowIds.Contains(pane.WindowId))
                {
                    failures++;
                    diagnostics.WriteLine($"RENAME-SKIPPED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)}: window id is missing or ambiguous");
                    continue;
                }

                renames.Add((pane, desired));
            }

            if (renames.Count == 0)
            {
                continue;
            }

            // All panes on a host share one tmux server, so any of their scanned epochs is the batch's.
            string scannedEpoch = renames[0].Pane.Epoch;
            string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
            string script = WindowNaming.BuildRenameScript(renames, scannedEpoch, nonce)!;

            // A linked deadline per host, mirroring CollectAsync: one hung host cannot hold up the rest,
            // and genuine caller cancellation dominates and escapes carrying the caller's own token.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(timeout);
            CommandResult result;
            try
            {
                result = await shellFor(host.Key)(script, linked.Token);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            catch (OperationCanceledException)
            {
                failures += renames.Count;
                string secs = timeout.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
                diagnostics.WriteLine($"RENAME-TIMEOUT {DisplayText.Safe(label)}: rename timed out after {secs}s; {renames.Count} window(s) not renamed");
                continue;
            }

            // A nonzero exit is the shell or ssh transport failing before anything was confirmed: nothing
            // renamed, so nothing is reported as renamed.
            if (result.ExitCode != 0)
            {
                failures += renames.Count;
                string detail = result.Stderr.Trim() is { Length: > 0 } stderr ? stderr : $"exited {result.ExitCode}";
                diagnostics.WriteLine($"RENAME-FAILED {DisplayText.Safe(label)}: {DisplayText.Safe(detail)}; {renames.Count} window(s) not renamed");
                continue;
            }

            string[] lines = result.Stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

            // Account per window, independently. Each window's guard prints exactly one marker naming that
            // window: `<nonce>:ok:@id` when its epoch, activity, name and state all still matched and tmux
            // confirmed the rename; `<nonce>:stale:@id` when the server was unchanged but the window's name,
            // published state, or activity had moved since the sweep, so the planned rename would overwrite
            // a newer identity or name a pane that has resumed; and `<nonce>:epoch:@id` when the server
            // generation itself changed. A restart or a reassignment between windows leaves the ones already
            // renamed reported and only the affected one skipped — the earlier success is never discarded.
            // Markers are counted per id and only for ids this host actually requested; a marker naming an
            // unrequested id, or naming one more than once, or naming it in more than one way, is a shape
            // the script never writes, so it confers nothing and the window falls through to failed,
            // fail-closed.
            string okPrefix = nonce + ":ok:";
            string epochPrefix = nonce + ":epoch:";
            string stalePrefix = nonce + ":stale:";
            HashSet<string> requested = [.. renames.Select(r => r.Pane.WindowId)];
            var okCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var epochCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var staleCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string line in lines)
            {
                Tally(line, okPrefix, requested, okCounts);
                Tally(line, epochPrefix, requested, epochCounts);
                Tally(line, stalePrefix, requested, staleCounts);
            }

            foreach ((TmuxPane pane, string desired) in renames)
            {
                int oks = okCounts.GetValueOrDefault(pane.WindowId);
                int epochs = epochCounts.GetValueOrDefault(pane.WindowId);
                int stales = staleCounts.GetValueOrDefault(pane.WindowId);
                int total = oks + epochs + stales;
                if (total == 1 && oks == 1)
                {
                    diagnostics.WriteLine($"RENAMED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)} -> {DisplayText.Safe(desired)}");
                }
                else if (total == 1 && epochs == 1)
                {
                    failures++;
                    diagnostics.WriteLine($"RENAME-SKIPPED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)}: tmux server changed since the sweep");
                }
                else if (total == 1 && stales == 1)
                {
                    failures++;
                    diagnostics.WriteLine($"RENAME-SKIPPED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)}: window name, published state, or activity changed since the sweep");
                }
                else if (total == 0)
                {
                    failures++;
                    diagnostics.WriteLine($"RENAME-FAILED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)}: tmux did not confirm the rename");
                }
                else
                {
                    // More than one marker for one window — impossible from the single guard per window this
                    // script writes, so it is a defect: fail closed rather than credit the success.
                    failures++;
                    diagnostics.WriteLine($"RENAME-FAILED {DisplayText.Safe(pane.Where)} {DisplayText.Safe(pane.WindowName)}: conflicting confirmations");
                }
            }
        }

        // A cancellation landing between the last shell call and here is still the caller's.
        ct.ThrowIfCancellationRequested();
        return failures;
    }

    /// <summary>Counts a marker line against the window it names, but only when that id was requested — so
    /// a marker for an unrequested id can never confer success.</summary>
    private static void Tally(string line, string prefix, HashSet<string> requested, Dictionary<string, int> counts)
    {
        if (line.StartsWith(prefix, StringComparison.Ordinal)
            && line[prefix.Length..] is { } id
            && requested.Contains(id))
        {
            counts[id] = counts.GetValueOrDefault(id) + 1;
        }
    }

    /// <summary>The windows whose current name differs from the one the tool would give them.</summary>
    internal static IEnumerable<(TmuxPane Pane, string Desired)> RenamePlan(IEnumerable<WaitingRow> rows)
        => rows
            .Select(r => (r.Pane, Desired: WindowNaming.Apply(r.Pane.WindowName, WindowNaming.SuffixFor(r.Verdict))))
            .Where(r => !string.Equals(r.Desired, r.Pane.WindowName, StringComparison.Ordinal));

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
    /// partial; with a previously-collected host omitted this run the lead is that it is narrowed —
    /// otherwise the one shape that matters most, a host that has gone dark while its windows sit
    /// finished, prints the word QUIET on the first line and the reason on the last. Both PARTIAL and
    /// NARROWED are the same tokens the trailer lines and the exit code already use, so nothing new has to
    /// be taught to a reader or a harness.
    /// </remarks>
    internal static string Summary(IReadOnlyList<WaitingRow> rows, IReadOnlyList<string> unreachable, IReadOnlyList<string>? omitted = null)
    {
        int attention = rows.Count(r => r.Verdict.NeedsAttention);
        if (unreachable.Count > 0)
        {
            return $"PARTIAL {unreachable.Count} host(s) unreachable; {attention} of {rows.Count} visible window(s) need you";
        }

        if (omitted is { Count: > 0 })
        {
            return $"NARROWED {omitted.Count} host(s) not collected this run; {attention} of {rows.Count} visible window(s) need you";
        }

        return attention > 0
            ? $"ATTENTION {attention} of {rows.Count} window(s) need you"
            : $"QUIET {rows.Count} window(s), none need you";
    }

    internal static void WriteTable(
        TextWriter output,
        IReadOnlyList<WaitingRow> rows,
        Budget budget,
        IReadOnlyList<string> unreachable,
        IReadOnlyList<string>? omitted = null,
        IReadOnlyList<string>? departed = null)
    {
        output.WriteLine(Summary(rows, unreachable, omitted));

        // Said on every run, including when the number is zero. A tool that speaks to agents only when it
        // is sure has to be legible about when it was not, or "it did nothing" and "it saw nothing" look
        // the same from here. The two counts partition the reported rows — the second is the complement of
        // the first, not a re-derivation — so a high-confidence row held back by a contested claim is
        // counted among the rows that did not meet the bar rather than in neither.
        int actionable = rows.Count(r => r.MayAct);
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
                    Duration(row.SilentFor ?? row.StoppedFor),
                    Detail(row),
                ]);
            }

            WriteAligned(output, table);
        }

        output.WriteLine();
        output.WriteLine(budget.ToString());

        if (omitted is { Count: > 0 })
        {
            output.WriteLine($"NARROWED not collected this run: {string.Join(", ", omitted.Select(DisplayText.Safe))} — nothing is owned while the fleet is partly unseen");
        }

        foreach (string gone in departed ?? [])
        {
            output.WriteLine($"DEPARTED {DisplayText.Safe(gone)}");
        }

        foreach (string failure in unreachable)
        {
            // An ssh failure carries the remote's stderr, which is as arbitrary as anything else here.
            output.WriteLine($"UNREACHABLE {DisplayText.Safe(failure)}");
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

        if (row.Retirement.IsRetirable)
        {
            detail += $"  [retire] {row.Retirement.Advice}";
        }

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

    /// <summary>Emits the explicitly-empty-fleet disposition as one JSON document — a success with no rows
    /// and a <c>fleet:"empty"</c> marker, so a <c>--json</c> consumer can tell an emptied fleet from a
    /// quiet one without a token to grep.</summary>
    internal static void WriteEmptyFleetJson(Stream output)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("fleet", "empty");
        writer.WriteString("message", "the declared fleet is empty; nothing to sweep — add a target with 'octoshift fleet add'");
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
    internal static void WriteJson(
        Stream output,
        IReadOnlyList<WaitingRow> rows,
        Budget budget,
        IReadOnlyList<string> unreachable,
        IReadOnlyList<string>? omitted = null,
        IReadOnlyList<string>? departed = null)
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

        if (omitted is { Count: > 0 })
        {
            writer.WriteStartArray("omitted");
            foreach (string host in omitted)
            {
                writer.WriteStringValue(host);
            }

            writer.WriteEndArray();
        }

        if (departed is { Count: > 0 })
        {
            writer.WriteStartArray("departed");
            foreach (string gone in departed)
            {
                writer.WriteStringValue(gone);
            }

            writer.WriteEndArray();
        }

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
        output.Write("\n"u8);
        output.Flush();
    }
}
