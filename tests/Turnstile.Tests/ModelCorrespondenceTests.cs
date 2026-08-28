namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Each test here corresponds to one property in <c>docs/model/Turnstile.tla</c>, checked against the
/// real store rather than the model.
/// </summary>
/// <remarks>
/// A model that has been checked exhaustively proves things about the model. It says nothing about the
/// code unless the correspondence is demonstrated, and an unchecked correspondence is how a specification
/// ends up describing a system nobody built. These are named for the TLA+ definitions they mirror so a
/// change to either side has an obvious counterpart to update.
///
/// The model is the authority on what a lease holder may conclude; these tests are the evidence that the
/// C# agrees.
/// </remarks>
public class ModelCorrespondenceTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-model-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// TLA+ <c>LogIsGapless</c>: every revision the store hands out is a row in the log. The model has no
    /// action for a rejected write that consumes a revision — <c>PutRejected</c> exists only under the
    /// <c>FailedWriteConsumesRevision</c> mutation, which violates the property. A gap matters because a
    /// watcher resuming across it waits for an event that is never coming. This is the correspondence for
    /// #192's transaction-local allocation: a rejected write returns without advancing the counter, and the
    /// committed revision is persisted atomically with its rows so a rolled-back write leaves no phantom.
    /// </summary>
    [Fact]
    public async Task LogIsGapless_RejectedWriteConsumesNoRevision()
    {
        using KvStore store = Open();
        Assert.Equal(WriteStatus.Created, (await store.CreateAsync("/k", Bytes("v"))).Status);

        long afterCreate = store.CurrentRevision;
        Assert.Equal(WriteStatus.Exists, (await store.CreateAsync("/k", Bytes("again"))).Status);
        Assert.Equal(WriteStatus.NotFound, (await store.UpdateAsync("/absent", Bytes("v"), ifMatch: 1)).Status);

        Assert.Equal(afterCreate, store.CurrentRevision);
    }

    /// <summary>
    /// TLA+ <c>NoWriteUnderExpiredLease</c>: a write naming an expired lease is refused. In the model the
    /// write guard <c>LeaseLive</c> and the sweeper's <c>Expired</c> partition existing leases exactly;
    /// the <c>ExpiryBoundaryOff</c> mutation makes them overlap on the boundary tick and the property
    /// fails. Here the guard is <c>KvStore.LeaseIsLive</c>, <c>exp &gt; Now()</c>.
    /// </summary>
    [Fact]
    public async Task NoWriteUnderExpiredLease_PutUnderAnExpiredLeaseIsRefused()
    {
        using KvStore store = Open();
        await LeaseClock.EnsureHeadroomAsync(Ct);
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);

        await LeaseClock.WaitPastExpiryAsync(lease, Ct);

        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.CreateAsync("/claim", Bytes("me"), lease: lease.Id));
    }

    /// <summary>
    /// TLA+ <c>RemovalIsLoggedStep</c>: a key stops being live only by way of a row in the log. This is a
    /// step property rather than an invariant because lazy expiry would give every reader the same answer
    /// — the difference exists only as an event that did not happen, which is why the model states it over
    /// the transition and why <c>SweepExpiredAsync</c> is eager.
    /// </summary>
    [Fact]
    public async Task RemovalIsLoggedStep_ExpiryTombstonesThroughTheLog()
    {
        using KvStore store = Open();
        await LeaseClock.EnsureHeadroomAsync(Ct);
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);
        WriteResult created = await store.CreateAsync("/ephemeral", Bytes("v"), lease: lease.Id);

        await LeaseClock.WaitPastExpiryAsync(lease, Ct);
        long before = store.CurrentRevision;
        Assert.Equal(1, await store.SweepExpiredAsync());

        // The key is gone, the log grew by exactly one, and that one row is the delete.
        Assert.Null(store.Get("/ephemeral"));
        Assert.Equal(before + 1, store.CurrentRevision);

        WatchEvent removal = Assert.Single(store.ReadEvents("/", fromExclusive: created.Revision, limit: 0));
        Assert.Equal("/ephemeral", removal.Key);
        Assert.True(removal.Deleted);
    }

    /// <summary>
    /// TLA+ <c>LiveKeysHaveALeaseRow</c>: a live key always names a lease whose row still exists, because
    /// deleting the keys and deleting the row are the same step. Revoke is the explicit form of the sweep.
    /// </summary>
    [Fact]
    public async Task LiveKeysHaveALeaseRow_RevokeTakesTheKeysWithIt()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 60);
        await store.CreateAsync("/a", Bytes("v"), lease: lease.Id);
        await store.CreateAsync("/b", Bytes("v"), lease: lease.Id);

        Assert.True(await store.RevokeLeaseAsync(lease.Id));

        Assert.Null(store.GetLease(lease.Id));
        Assert.Null(store.Get("/a"));
        Assert.Null(store.Get("/b"));
    }

    /// <summary>
    /// TLA+ <c>NoLostWakeup</c>: a parked watcher that is behind the log always has a pending reason to
    /// wake. <c>WatchAsync</c> gets that by capturing the change signal <em>before</em> draining, so a
    /// commit racing the drain still completes the captured task. The <c>DrainThenCapture</c> mutation
    /// reverses the two lines and the property fails.
    /// </summary>
    /// <remarks>
    /// This covers the wake <em>signal</em> ordering only, which the shipped code satisfies. It is not the
    /// whole watch story: <c>WatchAsync</c> emits its one-shot caught-up <c>WatchSyncMessage(CurrentRevision)</c>
    /// sampled on a snapshot separate from the event drain, so it can advertise a revision whose events were
    /// not delivered — a real, current defect (nightshift #197) that neither this test nor the model covers.
    /// </remarks>
    [Fact]
    public async Task NoLostWakeup_SignalCapturedBeforeTheDrainStillFires()
    {
        using KvStore store = Open();

        // Capture first, exactly as the watch loop does, then commit. The pulse must not be missed.
        Task changed = store.WaitForChangeAsync();
        await store.CreateAsync("/k", Bytes("v"));

        await changed.WaitAsync(TimeSpan.FromSeconds(5), Ct);
        Assert.True(changed.IsCompleted);
    }

    /// <summary>
    /// TLA+ <c>BeliefMatchesStoredDeadline</c>: the deadline a holder is handed is the deadline the store
    /// will actually enforce. <c>KvStore</c> satisfies this by returning the value it stored.
    /// <c>RemoteStore</c> does not — it fabricates one from the <em>client</em> clock, which is what the
    /// <c>ClientComputedDeadline</c> mutation models, so a caller cannot tell from
    /// <see cref="LeaseInfo.ExpiresAt"/> alone which of the two it is holding.
    /// </summary>
    [Fact]
    public async Task BeliefMatchesStoredDeadline_LocalLeaseReportsTheDeadlineItEnforces()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 60);

        LeaseView view = Assert.IsType<LeaseView>(store.GetLease(lease.Id));
        long nowSecs = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        // The handed-back deadline and the enforced one describe the same instant, to within the second
        // the clock is truncated to.
        Assert.InRange(lease.ExpiresAt - (nowSecs + view.TtlRemaining), -1, 1);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        GC.SuppressFinalize(this);
    }
}
