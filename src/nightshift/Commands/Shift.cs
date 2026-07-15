namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// Shared shift-presence gesture for <c>join</c>/<c>standby</c>: hold a lease for the whole shift and
/// stamp this agent's roster key with a status. The lease is separate from any per-order claim lease, so
/// presence survives across orders; when the agent stops renewing, the roster entry expires on its own.
/// </summary>
internal static class Shift
{
    // Presence spans the shift, not a single order; a quiet agent's roster entry lingers this long.
    public const long PresenceTtlSecs = 45 * 60;

    /// <summary>Ensures a live presence lease and stamps the roster key with <paramref name="status"/>.</summary>
    public static async Task ClockAsync(TurnstileClient client, string status, CancellationToken ct)
    {
        PresenceState? existing = Presence.Load();
        string leaseId = existing is not null && await client.KeepAliveAsync(existing.LeaseId, ct)
            ? existing.LeaseId
            : await client.CreateLeaseAsync(PresenceTtlSecs, ct);

        await client.PutLeasedAsync(Presence.Key, status, leaseId, ct);
        Presence.Save(new PresenceState(leaseId));
    }
}
