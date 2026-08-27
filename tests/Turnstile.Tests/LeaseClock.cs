namespace Turnstile.Tests;

using Turnstile.Storage;

/// <summary>
/// Lease deadlines are whole seconds: <c>expires_at = floor(now) + ttl</c>, and a lease is live while
/// <c>expires_at &gt; floor(now)</c>. A one-second lease created 10ms before the second rolls over is
/// therefore dead 10ms later, so its effective lifetime is uniform in (0, 1s] rather than a full second.
/// Any setup step that runs after the create can find the lease already expired, which surfaces as a
/// rare "lease not found or expired" under parallel load. These helpers take the luck out of it.
/// </summary>
internal static class LeaseClock
{
    /// <summary>
    /// Waits, if needed, until a lease created next keeps at least <paramref name="minMillis"/> of its
    /// final second. Costs nothing for most of each second and never more than one boundary crossing.
    /// </summary>
    internal static async Task EnsureHeadroomAsync(CancellationToken ct, int minMillis = 750)
    {
        int remaining = 1000 - (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1000);
        if (remaining < minMillis)
        {
            await Task.Delay(remaining + 20, ct);
        }
    }

    /// <summary>
    /// Waits until <paramref name="offsetMillis"/> either side of the lease's deadline. Anchoring to the
    /// deadline rather than to elapsed-time-since-create keeps a race aimed at the deadline even when the
    /// machine stalls between the two.
    /// </summary>
    internal static async Task WaitUntilNearExpiryAsync(LeaseInfo lease, int offsetMillis, CancellationToken ct)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long targetMs = (lease.ExpiresAt * 1000L) + offsetMillis;
        await Task.Delay((int)Math.Max(0, targetMs - nowMs), ct);
    }

    /// <summary>
    /// Waits until the lease's whole-second deadline has passed, so a sweep must see it expired.
    /// Derived from the lease itself rather than a padded constant that assumes a full-length TTL.
    /// </summary>
    internal static async Task WaitPastExpiryAsync(LeaseInfo lease, CancellationToken ct)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long deadlineMs = lease.ExpiresAt * 1000L;
        await Task.Delay((int)Math.Max(0, deadlineMs - nowMs) + 50, ct);
    }
}
