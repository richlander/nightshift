namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift recover</c> — re-attach to the order encoded by the current git branch after a reboot or a
/// wiped runtime dir. The branch (<c>nightshift/{plan}/{order}</c>) is the durable, on-disk breadcrumb: it
/// outlives the runtime session file, so it — not the lease handle — is the recovery key. If this agent
/// still owns the claim, or the claim has lapsed back to the pool and is not terminal, recover auto-adopts
/// it under a fresh lease and reprints the WORK packet, so the agent resumes exactly where it left off.
/// If a peer now holds the order, or it is already terminal, recover stands down.
/// </summary>
internal static class RecoverCommand
{
    // Match next's lease TTL — a re-adopted order is worked the same as a freshly claimed one.
    private const long LeaseTtlSecs = 45 * 60;

    // Terminal outcomes are finished work; recover must never silently re-open them (mirrors Reconciler).
    private static readonly string[] Terminal = ["done", "landed", "blocked", "escalated", "refused"];

    public static async Task<int> RunAsync(string[] args)
    {
        if (OrderRef.FromBranch(Git.CurrentBranch()) is not { } order)
        {
            Console.Error.WriteLine("nightshift recover: current git branch is not a nightshift/<plan>/<order> branch");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Already adopted in this worktree with a live lease? Nothing to recover — just reprint.
        SessionState? existing = Session.Load();
        if (existing is not null && existing.OrderBase == order.Base && await client.KeepAliveAsync(existing.LeaseId, ct))
        {
            await ReprintAsync(client, order.Base, existing.Fence, ct);
            return ExitCode.Ok;
        }

        if (await CurrentStatusAsync(client, order.Base, ct) is { } status && Array.IndexOf(Terminal, status) >= 0)
        {
            Console.Error.WriteLine($"nightshift recover: order is '{status}' — nothing to recover");
            Session.Clear();
            return ExitCode.NoClaim;
        }

        KvItem? claim = await client.GetAsync(order.ClaimKey, ct);
        if (claim is not null && claim.Text.Trim() != Session.Identity)
        {
            Console.Error.WriteLine("nightshift recover: order is claimed by another agent — standing down");
            Session.Clear();
            return ExitCode.NoClaim;
        }

        // Either the claim is gone (its lease lapsed → the order returned to the pool) or it is still ours
        // but we lost the lease handle (runtime dir wiped). Re-establish under a fresh lease; if it was
        // ours, free it first so it can re-attach. The final CAS makes this safe against a racing peer:
        // whoever wins the create owns it, so recover never double-claims.
        string leaseId = await client.CreateLeaseAsync(LeaseTtlSecs, ct);
        if (claim is not null)
        {
            await client.DeleteAsync(order.ClaimKey, ct);
        }

        ClaimResult reclaim = await client.TryClaimAsync(order.ClaimKey, leaseId, Session.Identity, ct);
        if (!reclaim.Won)
        {
            await client.RevokeLeaseAsync(leaseId, ct);
            Console.Error.WriteLine("nightshift recover: order was taken while recovering — standing down");
            Session.Clear();
            return ExitCode.NoClaim;
        }

        Session.Save(new SessionState(leaseId, reclaim.Revision, order.ClaimKey, order.Base, order.ReadyKey));
        await client.SetAsync($"{order.Base}/branch", order.Branch, ct);
        await ReprintAsync(client, order.Base, reclaim.Revision, ct);
        return ExitCode.Ok;
    }

    private static async Task ReprintAsync(TurnstileClient client, string orderBase, long fence, CancellationToken ct)
    {
        KvItem? spec = await client.GetAsync($"{orderBase}/spec", ct);
        OrderView view = spec is null ? OrderView.Empty : OrderView.Parse(spec.Text);
        view.PrintWork(Console.Out, orderBase, fence);
    }

    private static async Task<string?> CurrentStatusAsync(TurnstileClient client, string orderBase, CancellationToken ct)
    {
        KvItem? state = await client.GetAsync($"{orderBase}/state", ct);
        if (state is null)
        {
            return null;
        }

        try
        {
            using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(state.Text);
            return doc.RootElement.TryGetProperty("status", out System.Text.Json.JsonElement s) ? s.GetString() : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
