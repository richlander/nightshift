namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift check</c> — the heartbeat and directive read. Renews the lease (the forcing function that
/// keeps the claim alive), verifies the claim still belongs to this agent at its fence, and surfaces any
/// standing directive. Responses: OK | HALT | FENCE_STALE | QUERY.
/// </summary>
internal static class CheckCommand
{
    // A global stop flag any operator can set; every checking agent sees it and halts.
    private const string HaltKey = "/control/halt";

    /// <summary>The re-arm decision for a worker parked on a prereq-unreachable base ref (stacked orders §4).</summary>
    internal enum PrereqOutcome
    {
        /// <summary>Not a prereq escalation — fall through to the normal directive/OK path.</summary>
        NotPrereq,

        /// <summary>The base ref is now reachable: clear the self-raised escalation and let the worker build.</summary>
        Resolved,

        /// <summary>Still unreachable: keep the worker parked (the lease was already renewed).</summary>
        Parked,
    }

    /// <summary>How <c>check</c> clears a resolved prereq escalation: restore rework, or clear to a fresh claim.</summary>
    internal enum PrereqResolution
    {
        /// <summary>The order had no pre-escalation state (a fresh claim): delete <c>{base}/state</c>.</summary>
        ClearToFresh,

        /// <summary>The order was a rework: restore <c>changes-requested</c> so its findings keep rendering.</summary>
        RestoreRework,
    }

    /// <summary>Resolves a satisfied prereq to the state to restore — rework when the order carried rework findings.</summary>
    internal static PrereqResolution ResolutionFor(bool isRework)
        => isRework ? PrereqResolution.RestoreRework : PrereqResolution.ClearToFresh;

    /// <summary>
    /// Classifies a standing order state for the prereq re-arm path: a prereq-unreachable escalation resolves
    /// once <paramref name="reachable"/>, stays parked until then, and anything else is not a prereq.
    /// </summary>
    internal static PrereqOutcome ClassifyPrereq(string? status, string? reason, bool reachable)
        => status == "escalated" && EscalateCommand.IsPrereqUnreachableReason(reason)
            ? (reachable ? PrereqOutcome.Resolved : PrereqOutcome.Parked)
            : PrereqOutcome.NotPrereq;

    public static async Task<int> RunAsync(string[] args)
    {
        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift check: no active claim (run `nightshift next` first)");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Renew first: if the lease is gone the claim has already been swept — the agent must stop.
        bool alive = await client.KeepAliveAsync(session.LeaseId, ct);
        if (!alive)
        {
            Session.Clear();
            Console.WriteLine("FENCE_STALE");
            return ExitCode.FenceStale;
        }

        // The claim is lease-attached, but verify it is still ours at the fence we were issued.
        KvItem? claim = await client.GetAsync(session.ClaimKey, ct);
        if (claim is null || claim.ModRevision != session.Fence || claim.Text.Trim() != Session.Identity)
        {
            Session.Clear();
            Console.WriteLine("FENCE_STALE");
            return ExitCode.FenceStale;
        }

        if (await client.GetAsync(HaltKey, ct) is not null)
        {
            Console.WriteLine("HALT");
            return ExitCode.Halt;
        }

        // Stacked orders §4: re-arm a worker parked on an unreachable base ref. When the standing state is a
        // self-raised prereq-unreachable escalation, re-test reachability. Once the coordinator has published
        // the base to origin and the worker has fetched it, the base resolves locally: clear the escalation
        // and let the worker build. Until then keep it parked (the lease was renewed above). The worker never
        // pushes — this path only reads the local object database.
        (string? status, string? reason) = ParseState((await client.GetAsync($"{session.OrderBase}/state", ct))?.Text);
        if (status == "escalated" && EscalateCommand.IsPrereqUnreachableReason(reason))
        {
            OrderView view = await OrderView.LoadAsync(client, session.OrderBase, ct);
            bool reachable = NextCommand.BaseRefReachable(view.BaseRef, Git.RevParse);
            switch (ClassifyPrereq(status, reason, reachable))
            {
                case PrereqOutcome.Resolved:
                    // The self-raised prereq is satisfied. Restore the pre-escalation state so we do not lose
                    // rework semantics: an order that was `changes-requested` when claimed still carries its
                    // findings in {base}/rework, so returning it to `changes-requested` keeps `show`/`recover`
                    // rendering `mode: rework`. A fresh claim had no state — clear it back to none.
                    bool isRework = await client.GetAsync($"{session.OrderBase}/rework", ct) is not null;
                    if (ResolutionFor(isRework) == PrereqResolution.RestoreRework)
                    {
                        await OrderState.WriteAsync(
                            client,
                            session.OrderBase,
                            OrderView.ChangesRequested,
                            "prereq resolved: base-ref reachable — resuming rework",
                            Session.Identity,
                            ct);
                    }
                    else
                    {
                        await client.DeleteAsync($"{session.OrderBase}/state", ct);
                    }

                    Console.WriteLine("OK");
                    Console.WriteLine("prereq resolved: base-ref is reachable locally — proceed to build");
                    return ExitCode.Ok;
                case PrereqOutcome.Parked:
                    Console.WriteLine("QUERY");
                    Console.WriteLine(reason);
                    return ExitCode.Query;
            }
        }

        KvItem? directive = await client.GetAsync($"{session.OrderBase}/directive", ct);
        if (directive is not null && directive.Text.Trim() is { Length: > 0 } text)
        {
            Console.WriteLine("QUERY");
            Console.WriteLine(text);
            return ExitCode.Query;
        }

        Console.WriteLine("OK");
        return ExitCode.Ok;
    }

    /// <summary>Extracts the <c>status</c> and <c>reason</c> fields from an order's <c>{base}/state</c> JSON.</summary>
    private static (string? Status, string? Reason) ParseState(string? stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
        {
            return (null, null);
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            string? status = root.TryGetProperty("status", out JsonElement s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            string? reason = root.TryGetProperty("reason", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            return (status, reason);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
