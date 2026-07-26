namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift coordinate [scope]</c> — the coordinator's one-shot filtered wait. Blocks until the first
/// coordinator-actionable transition, printing one token line with minimal payload so the coordinator can
/// act without a second query.
/// </summary>
internal static class CoordinateCommand
{
    private const string PlanRoot = "/plan/";
    private const string ReadyRoot = "/ready/";
    private const string ControlRoot = "/control/";
    private const string HaltKey = "/control/halt";
    private const string DrainingKey = "/control/draining";
    private const string BlessedMainHeadKey = "/coord/main-head";
    private const string CorpseRefPrefix = "refs/nightshift/corpse";
    private const string MainRef = "refs/heads/main";

    private const string StateSuffix = "/state";
    private const string ClaimSuffix = "/claim";
    private const string BranchSuffix = "/branch";
    private const string SpecSuffix = "/spec";

    private static readonly HashSet<string> RequeueIneligibleStatuses =
    [
        "done",
        "landed",
        "blocked",
        "escalated",
        "refused",
        "declined",
    ];

    internal static TimeSpan KeepAliveCadence
    {
        get => FilteredWaitEngine.KeepAliveCadence;
        set => FilteredWaitEngine.KeepAliveCadence = value;
    }

    internal enum MainVerdict
    {
        Healthy,
        Adopt,
        Advance,
        Steal,
    }

    internal static MainVerdict ClassifyMain(
        string? observed,
        string? blessed,
        bool blessedIsAncestorOfObserved,
        bool observedMatchesClaimedBranch)
    {
        if (observed is null)
        {
            return MainVerdict.Healthy;
        }

        if (string.IsNullOrWhiteSpace(blessed))
        {
            return MainVerdict.Adopt;
        }

        if (observed == blessed)
        {
            return MainVerdict.Healthy;
        }

        if (blessedIsAncestorOfObserved)
        {
            return observedMatchesClaimedBranch ? MainVerdict.Steal : MainVerdict.Advance;
        }

        return MainVerdict.Steal;
    }

    public static async Task<int> RunAsync(string? scope, int? timeoutSecs, bool once)
    {
        if (timeoutSecs is <= 0)
        {
            Console.Error.WriteLine("nightshift coordinate: --timeout must be a positive integer");
            return ExitCode.Usage;
        }

        string planPrefix = scope is null ? PlanRoot : $"{PlanRoot}{scope}/";
        string readyPrefix = scope is null ? ReadyRoot : $"{ReadyRoot}{scope}/";

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        var predicate = new CoordinatePredicate();

        try
        {
            // Blocking coordinate treats DRAINING as a transition signal (return when drain STARTS while
            // parked), not as a standing startup terminal. --once includes standing draining by design.
            if (await TryReadTerminalSignalAsync(client, includeDraining: once, ct) is { } terminal)
            {
                Console.WriteLine(terminal.Token);
                return terminal.ExitCode;
            }

            CoordinateOutcome? outcome = await WaitForActionCoreAsync(
                once,
                timeoutSecs,
                token => ProbeActionableStatesAsync(client, planPrefix, readyPrefix, token),
                client.CurrentRevisionAsync,
                [
                    new FilteredWaitEngine.WatchScope("plan", (from, token) => client.WatchAsync(planPrefix, from, token)),
                    new FilteredWaitEngine.WatchScope("control", (from, token) => client.WatchAsync(ControlRoot, from, token)),
                ],
                token => KeepPresenceAliveAsync(client, token),
                (edge, token) => predicate.TryMatchAsync(edge, client.GetAsync, token),
                token => ProbeActionableStatesAsync(client, planPrefix, readyPrefix, token),
                ct);

            if (outcome is null)
            {
                Console.WriteLine("NOCOORD");
                return ExitCode.NoCoordinate;
            }

            Console.WriteLine(outcome.Render());
            return outcome.ExitCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("INTERRUPTED");
            return ExitCode.Interrupted;
        }
    }

    internal static async Task<CoordinateOutcome?> WaitForActionCoreAsync(
        bool once,
        int? timeoutSecs,
        Func<CancellationToken, Task<CoordinateOutcome?>> probeActionableAsync,
        Func<CancellationToken, Task<long>> currentRevision,
        IReadOnlyList<FilteredWaitEngine.WatchScope> scopes,
        Func<CancellationToken, Task> keepAliveAsync,
        Func<FilteredWaitEngine.WatchEdge, CancellationToken, Task<CoordinateOutcome?>> reconcileAsync,
        Func<CancellationToken, Task<CoordinateOutcome?>> reconcileSnapshotAsync,
        CancellationToken ct)
    {
        if (once)
        {
            return await probeActionableAsync(ct);
        }

        // Capture the revision floor BEFORE waiting. In blocking mode coordinate is edge-triggered, so we
        // do not probe standing states here; callers reconcile snapshots outside the wait loop.
        long fromRevision = await currentRevision(ct);
        DateTime? deadline = timeoutSecs is { } secs ? DateTime.UtcNow.AddSeconds(secs) : null;
        FilteredWaitEngine.WaitResult<CoordinateOutcome> result = await FilteredWaitEngine.WaitForMatchAsync(
            scopes,
            currentRevision,
            fromRevision,
            deadline,
            keepAliveAsync,
            reconcileAsync,
            reconcileSnapshotAsync,
            ct);

        return result.TimedOut ? null : result.Match;
    }

    private static async Task<CoordinateOutcome?> ProbeActionableStatesAsync(
        TurnstileClient client,
        string planPrefix,
        string readyPrefix,
        CancellationToken ct)
    {
        if (await CheckMainIntegrityAsync(client, ct) is { } mainOutcome)
        {
            return mainOutcome;
        }

        foreach (KvItem item in await client.RangeAsync(planPrefix, ct))
        {
            if (!item.Key.EndsWith(StateSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string orderBase = item.Key[..^StateSuffix.Length];
            string? status = StatusOf(item.Text);
            if (status == "done" && !await AreDependenciesLandedAsync(client.GetAsync, orderBase, ct))
            {
                continue; // Stacked orders §5: a dependent is not landable until its contract has landed.
            }

            if (TryBuildStateOutcome(orderBase, status, ReasonOf(item.Text)) is { } outcome)
            {
                return outcome;
            }
        }

        foreach (KvItem ready in await client.RangeAsync(readyPrefix, ct))
        {
            string orderBase = ready.Text.Trim();
            if (OrderRef.FromBase(orderBase) is not { } order)
            {
                continue;
            }

            if (await client.GetAsync(order.ClaimKey, ct) is not null)
            {
                continue;
            }

            if (await client.GetAsync($"{orderBase}{BranchSuffix}", ct) is null)
            {
                continue; // Never claimed: not a requeue.
            }

            string? status = StatusOf((await client.GetAsync($"{orderBase}{StateSuffix}", ct))?.Text);
            if (status is not null && RequeueIneligibleStatuses.Contains(status))
            {
                continue;
            }

            return CoordinateOutcome.Action(orderBase, transition: "requeued", status: "ready");
        }

        return null;
    }

    private static async Task<CoordinateOutcome?> CheckMainIntegrityAsync(TurnstileClient client, CancellationToken ct)
    {
        try
        {
            string? observed = Git.RevParse("main");
            if (observed is null)
            {
                return null;
            }

            string? blessed = (await client.GetAsync(BlessedMainHeadKey, ct))?.Text.Trim();
            (string orderBase, KvItem claim)? offender = null;
            bool observedMatchesClaimedBranch = false;
            if (observed != blessed)
            {
                string? originMain = Git.RevParse("origin/main");
                if (originMain is not null && observed == originMain)
                {
                    return await WriteBlessedMainHeadAsync(client, observed, ct);
                }

                offender = await FindMainStealOffenderAsync(client, observed, ct);
                observedMatchesClaimedBranch = offender is not null;
            }

            bool blessedIsAncestorOfObserved = blessed is { Length: > 0 } && Git.IsAncestor(blessed, observed);
            return ClassifyMain(observed, blessed, blessedIsAncestorOfObserved, observedMatchesClaimedBranch) switch
            {
                MainVerdict.Healthy => null,
                MainVerdict.Adopt => await WriteBlessedMainHeadAsync(client, observed, ct),
                MainVerdict.Advance => await WriteBlessedMainHeadAsync(client, observed, ct),
                MainVerdict.Steal => await HandleMainStealAsync(client, observed, blessed!, offender, ct),
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nightshift coordinate: non-fatal main-integrity check error: {ex.Message}");
            return null;
        }
    }

    private static async Task<CoordinateOutcome?> WriteBlessedMainHeadAsync(
        TurnstileClient client,
        string observed,
        CancellationToken ct)
    {
        await client.SetAsync(BlessedMainHeadKey, observed, ct);
        return null;
    }

    private static async Task<CoordinateOutcome> HandleMainStealAsync(
        TurnstileClient client,
        string observed,
        string blessed,
        (string orderBase, KvItem claim)? offender,
        CancellationToken ct)
    {
        ReconcileMainRef(blessed);
        if (offender is null)
        {
            Console.Error.WriteLine("nightshift coordinate: local main moved unexpectedly; offender branch was not identified");
            return CoordinateOutcome.Action("/plan/unknown/order/unknown", transition: "main-stolen", status: "cancelled");
        }

        if (OrderRef.FromBase(offender.Value.orderBase) is { } offenderRef)
        {
            string corpseRef = $"{CorpseRefPrefix}/{offenderRef.Plan}/{offenderRef.Order}/steal";
            if (!Git.UpdateRef(corpseRef, observed))
            {
                Console.Error.WriteLine($"nightshift coordinate: failed to quarantine offender branch at {corpseRef}");
            }
        }
        else
        {
            Console.Error.WriteLine($"nightshift coordinate: offender order base was invalid: {offender.Value.orderBase}");
        }

        await OrderState.WriteAsync(client, offender.Value.orderBase, "refused", "stole main: claim cancelled", "coordinator", ct);

        if (offender.Value.claim.Lease is { Length: > 0 } leaseId)
        {
            await client.RevokeLeaseAsync(leaseId, ct);
        }
        else
        {
            await client.DeleteAsync($"{offender.Value.orderBase}{ClaimSuffix}", ct);
        }

        return CoordinateOutcome.Action(offender.Value.orderBase, transition: "main-stolen", status: "cancelled");
    }

    private static void ReconcileMainRef(string blessed)
    {
        if (string.Equals(Git.CurrentBranch(), "main", StringComparison.Ordinal))
        {
            if (!Git.ResetHardTo(blessed))
            {
                Console.Error.WriteLine($"nightshift coordinate: failed to reset local main to blessed head {blessed}");
            }

            return;
        }

        if (!Git.UpdateRef(MainRef, blessed))
        {
            Console.Error.WriteLine($"nightshift coordinate: failed to update {MainRef} to blessed head {blessed}");
        }
    }

    private static async Task<(string orderBase, KvItem claim)?> FindMainStealOffenderAsync(
        TurnstileClient client,
        string observed,
        CancellationToken ct)
    {
        foreach (KvItem item in await client.RangeAsync(PlanRoot, ct))
        {
            if (!item.Key.EndsWith(BranchSuffix, StringComparison.Ordinal))
            {
                continue;
            }

            string orderBase = item.Key[..^BranchSuffix.Length];
            KvItem? claim = await client.GetAsync($"{orderBase}{ClaimSuffix}", ct);
            if (claim is null)
            {
                continue;
            }

            string branchName = item.Text.Trim();
            if (branchName.Length == 0)
            {
                continue;
            }

            if (Git.RevParse(branchName) == observed)
            {
                return (orderBase, claim);
            }
        }

        return null;
    }

    private static async Task KeepPresenceAliveAsync(TurnstileClient client, CancellationToken ct)
    {
        PresenceState? presence = Presence.Load();
        if (presence is not null)
        {
            await client.KeepAliveAsync(presence.LeaseId, ct);
        }
    }

    private static async Task<CoordinateOutcome?> TryReadTerminalSignalAsync(TurnstileClient client, bool includeDraining, CancellationToken ct)
    {
        if (await client.GetAsync(HaltKey, ct) is not null)
        {
            return CoordinateOutcome.Halt;
        }

        if (includeDraining && await client.GetAsync(DrainingKey, ct) is not null)
        {
            return CoordinateOutcome.Draining;
        }

        return null;
    }

    private static CoordinateOutcome? TryBuildStateOutcome(string orderBase, string? status, string? reason)
        => status switch
        {
            "done" => CoordinateOutcome.Action(orderBase, transition: "done", status: "done"),
            "landed" => CoordinateOutcome.Action(orderBase, transition: "landed", status: "landed"),

            // Stacked orders §4: a prereq-unreachable escalation is a publish-the-base request, distinct from a
            // judgment escalation. Surface it as transition=prereq so the coordinator publishes the base ref to
            // origin (workers stay read-only w.r.t. origin), rather than adjudicating it.
            "escalated" => EscalateCommand.IsPrereqUnreachableReason(reason)
                ? CoordinateOutcome.Action(orderBase, transition: "prereq", status: "escalated")
                : CoordinateOutcome.Action(orderBase, transition: "escalated", status: "escalated"),
            _ => null,
        };

    private static string? StatusOf(string? stateJson) => FieldOf(stateJson, "status");

    private static string? ReasonOf(string? stateJson) => FieldOf(stateJson, "reason");

    /// <summary>
    /// Stacked orders §5 (land in topological order): a <c>done</c> order is landable only once every order it
    /// depends on — the order ids in its spec <c>after</c> — has itself reached <c>landed</c> (contract first,
    /// then its dependents). This gates on the dependency's <b>landed state</b>, not on a git-ancestor check, so
    /// it holds under any merge strategy: a squash merge never makes the contract's literal base SHA an ancestor
    /// of <c>main</c>, yet the contract's order still transitions to <c>landed</c>. An order with no <c>after</c>
    /// deps (an independent order or the contract itself) is trivially landable, so existing single-order plans
    /// are unaffected. A dependency whose state is missing or unreadable is treated as not-yet-landed
    /// (fail closed — never surface a dependent for landing on incomplete information).
    /// </summary>
    internal static async Task<bool> AreDependenciesLandedAsync(
        Func<string, CancellationToken, Task<KvItem?>> getAsync,
        string orderBase,
        CancellationToken ct)
    {
        if (OrderRef.FromBase(orderBase) is not { } order)
        {
            return true;
        }

        string? specJson = (await getAsync($"{orderBase}{SpecSuffix}", ct))?.Text;
        foreach (string depId in AfterIdsOf(specJson))
        {
            string depBase = $"/plan/{order.Plan}/order/{depId}";
            string? depStatus = StatusOf((await getAsync($"{depBase}{StateSuffix}", ct))?.Text);
            if (depStatus != "landed")
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The order ids in a spec's <c>after</c> array. The spec normalizes every edge to a bare string id
    /// (see <c>Plan.CopyAfterIds</c>), so this reads string entries only and tolerates a missing/other-shaped
    /// <c>after</c> by yielding nothing.</summary>
    private static IReadOnlyList<string> AfterIdsOf(string? specJson)
    {
        if (string.IsNullOrWhiteSpace(specJson))
        {
            return [];
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(specJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("after", out JsonElement after)
                || after.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<string>();
            foreach (JsonElement edge in after.EnumerateArray())
            {
                if (edge.ValueKind == JsonValueKind.String && edge.GetString() is { Length: > 0 } id)
                {
                    ids.Add(id);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? FieldOf(string? stateJson, string field)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            JsonElement root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty(field, out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal sealed class CoordinatePredicate
    {
        internal async Task<CoordinateOutcome?> TryMatchAsync(
            FilteredWaitEngine.WatchEdge edge,
            Func<string, CancellationToken, Task<KvItem?>> getAsync,
            CancellationToken ct)
        {
            if (edge.Scope == "control")
            {
                if (!edge.Signal.Deleted && edge.Signal.Key == HaltKey)
                {
                    return CoordinateOutcome.Halt;
                }

                if (!edge.Signal.Deleted && edge.Signal.Key == DrainingKey)
                {
                    return CoordinateOutcome.Draining;
                }

                return null;
            }

            if (edge.Scope == "plan")
            {
                if (!edge.Signal.Deleted && edge.Signal.Key.EndsWith(StateSuffix, StringComparison.Ordinal))
                {
                    string orderBase = edge.Signal.Key[..^StateSuffix.Length];
                    KvItem? state = await getAsync(edge.Signal.Key, ct);
                    string? status = StatusOf(state?.Text);
                    if (status == "done" && !await AreDependenciesLandedAsync(getAsync, orderBase, ct))
                    {
                        return null; // Stacked orders §5: hold a dependent's done until its contract has landed.
                    }

                    return TryBuildStateOutcome(orderBase, status, ReasonOf(state?.Text));
                }

                if (edge.Signal.Key.EndsWith(ClaimSuffix, StringComparison.Ordinal))
                {
                    string orderBase = edge.Signal.Key[..^ClaimSuffix.Length];
                    if (!edge.Signal.Deleted)
                    {
                        return null;
                    }

                    return await TryBuildRequeueFromClaimDeleteAsync(orderBase, getAsync, ct);
                }
            }

            return null;
        }

        private static async Task<CoordinateOutcome?> TryBuildRequeueFromClaimDeleteAsync(
            string orderBase,
            Func<string, CancellationToken, Task<KvItem?>> getAsync,
            CancellationToken ct)
        {
            if (OrderRef.FromBase(orderBase) is not { } order)
            {
                return null;
            }

            if (await getAsync(order.ClaimKey, ct) is not null)
            {
                return null;
            }

            string? status = StatusOf((await getAsync($"{orderBase}{StateSuffix}", ct))?.Text);
            if (status is not null && RequeueIneligibleStatuses.Contains(status))
            {
                return null;
            }

            return CoordinateOutcome.Action(orderBase, transition: "requeued", status: "ready");
        }
    }

    internal sealed record CoordinateOutcome(string Token, int ExitCode, string? PlanBase, string? OrderBase, string? Transition, string? Status)
    {
        public static CoordinateOutcome Action(string orderBase, string transition, string status)
        {
            string planBase = OrderRef.FromBase(orderBase) is { } order ? $"/plan/{order.Plan}" : "/plan/unknown";
            return new CoordinateOutcome("COORD", Nightshift.ExitCode.Coordinate, planBase, orderBase, transition, status);
        }

        public static CoordinateOutcome Halt { get; } = new("HALT", Nightshift.ExitCode.Halt, null, null, null, null);

        public static CoordinateOutcome Draining { get; } = new("DRAINING", Nightshift.ExitCode.Draining, null, null, null, null);

        public string Render()
            => PlanBase is null || OrderBase is null || Transition is null || Status is null
                ? Token
                : $"{Token} plan={PlanBase} order={OrderBase} transition={Transition} status={Status}";
    }
}
