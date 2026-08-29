namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Each test here corresponds to a property in <c>docs/model/Turnstile.tla</c>, checked against the real
/// store rather than the model.
/// </summary>
/// <remarks>
/// A model that has been checked exhaustively proves things about the model. It says nothing about the
/// code unless the correspondence is demonstrated, and an unchecked correspondence is how a specification
/// ends up describing a system nobody built. Most tests here exercise the state-level property directly
/// (a rejected write consumes no revision; an expiry tombstones through the log).
///
/// One correspondence is not state-level and is not claimed to be: <c>WatchAsync</c>'s capture-before-drain
/// loop order is a source-order fact, exercised at the level of the <c>ChangeSignal</c> primitive it relies
/// on (<see cref="ChangeSignal_PulseCompletesAnAlreadyCapturedWaiter"/>) and by the model's
/// <c>DrainThenCapture</c> mutation — not by an outcome-level test of the loop itself, which would need
/// timing or a product seam this does not add. So not every named test demonstrates the full production
/// property; where it does not, the summary says so.
///
/// The model is the authority on what a lease holder may conclude; these tests are the evidence that the
/// C# agrees where a test can carry that weight.
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
        // Construct — with no live clock anywhere — the exact durable state that "create a key attached to a
        // lease, then let the lease expire" leaves behind: a created, live /ephemeral row whose lease row is
        // already past its deadline. Building it directly means no step depends on the lease being live at any
        // wall-clock instant (there is no CreateLeaseAsync + leased CreateAsync sequence to outrun). The only
        // "now" left is inside SweepExpiredAsync, comparing a long-past expires_at against the real clock —
        // which is the behaviour under test, not a setup precondition.
        const long baseline = 1;
        SeedExpiredLeasedKey(key: "/ephemeral", revision: baseline);

        using KvStore store = Open();
        Assert.Equal(baseline, store.CurrentRevision);
        Assert.NotNull(store.Get("/ephemeral"));   // the constructed key is live until an eager sweep removes it

        long before = store.CurrentRevision;
        Assert.Equal(1, await store.SweepExpiredAsync());

        // The key is gone, the log grew by exactly one, and that one row is the delete.
        Assert.Null(store.Get("/ephemeral"));
        Assert.Equal(before + 1, store.CurrentRevision);

        WatchEvent removal = Assert.Single(store.ReadEvents("/", fromExclusive: baseline, limit: 0));
        Assert.Equal("/ephemeral", removal.Key);
        Assert.True(removal.Deleted);
    }

    /// <summary>
    /// Establishes, entirely through direct SQLite state, the durable snapshot that ordinary elapsed time
    /// would leave: a key created and attached to a lease whose <c>expires_at</c> is already in the past. The
    /// key's sole log row has exactly the shape <c>InsertRow</c> writes for a leased create (created, live,
    /// <c>lease</c> set, <c>create_rev = id</c>), and the durable <c>committed_revision</c> is advanced in the
    /// same transaction as its row, so the store opens onto a consistent, product-reachable state. No lease is
    /// ever live at any wall-clock instant, so there is no timing precondition to lose. Production lease
    /// semantics (#189) are untouched.
    /// </summary>
    private void SeedExpiredLeasedKey(string key, long revision)
    {
        using (KvStore _ = Open())
        {
            // Create the real schema (committed_revision = 0, empty log).
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // A fixed 128-bit hex id, the shape CreateLeaseAsync mints; the value only has to match the kv row.
        const string leaseId = "00000000000000000000000000000001";

        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using Microsoft.Data.Sqlite.SqliteTransaction tx = conn.BeginTransaction();

            using (Microsoft.Data.Sqlite.SqliteCommand lease = conn.CreateCommand())
            {
                lease.Transaction = tx;
                lease.CommandText = "INSERT INTO lease (id, ttl_secs, expires_at) VALUES ($id, 1, 0);";
                lease.Parameters.AddWithValue("$id", leaseId);
                lease.ExecuteNonQuery();
            }

            using (Microsoft.Data.Sqlite.SqliteCommand row = conn.CreateCommand())
            {
                row.Transaction = tx;
                row.CommandText = """
                    INSERT INTO kv (id, key, created, deleted, immutable, create_rev, prev_rev, lease, value, old_value)
                    VALUES ($id, $key, 1, 0, 0, $id, 0, $lease, $value, NULL);
                    """;
                row.Parameters.AddWithValue("$id", revision);
                row.Parameters.AddWithValue("$key", key);
                row.Parameters.AddWithValue("$lease", leaseId);
                row.Parameters.AddWithValue("$value", Bytes("v"));
                row.ExecuteNonQuery();
            }

            using (Microsoft.Data.Sqlite.SqliteCommand meta = conn.CreateCommand())
            {
                meta.Transaction = tx;
                meta.CommandText = "UPDATE meta SET v = $v WHERE k = 'committed_revision';";
                meta.Parameters.AddWithValue("$v", revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
                meta.ExecuteNonQuery();
            }

            tx.Commit();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
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
    /// The <c>ChangeSignal</c> primitive that TLA+ <c>NoLostWakeup</c> abstracts: a pulse completes a waiter
    /// that was captured before the pulse — the task the watcher parks on is already complete by the time it
    /// awaits, so a pulse racing an already-captured waiter is never lost. This is the primitive
    /// <c>WatchAsync</c> relies on by capturing <c>WaitForChangeAsync()</c> before it drains.
    /// </summary>
    /// <remarks>
    /// It tests the primitive directly and synchronously — it constructs a <c>ChangeSignal</c>, not a store,
    /// and never calls <c>WatchAsync</c> or commits anything. It therefore proves neither store commit wiring
    /// nor <c>WatchAsync</c>'s loop order: reversing that loop to drain-then-capture would leave it green. The
    /// capture-before-drain ordering is a source-order correspondence, verified by reading <c>WatchAsync</c>
    /// and by the model's <c>DrainThenCapture</c> mutation — not proven here. The separate sync-boundary
    /// property (nightshift #197) — that the one-shot sync is the committed revision read in the same snapshot
    /// as the events, so it cannot advertise an undelivered one — is now fixed and is exercised at outcome
    /// level in <c>SnapshotConsistencyTests</c>.
    /// </remarks>
    [Fact]
    public void ChangeSignal_PulseCompletesAnAlreadyCapturedWaiter()
    {
        var signal = new ChangeSignal();

        // Capture the waiter first, exactly as the watch loop does before draining, then pulse.
        Task changed = signal.WaitAsync();
        Assert.False(changed.IsCompleted);   // nothing has pulsed yet

        signal.Pulse();

        // The pulse completes the already-captured waiter synchronously: the wakeup cannot be lost. A
        // regression that drops the Pulse leaves this task uncompleted, so the assertion fails here
        // immediately — deterministic, with no wall-clock bound, and it can never hang.
        Assert.True(changed.IsCompletedSuccessfully);
    }

    /// <summary>
    /// TLA+ <c>BeliefMatchesStoredDeadline</c>: the deadline a holder is handed is the deadline the store
    /// will actually enforce. <c>KvStore</c> satisfies this by returning the value it stored.
    /// <c>RemoteStore</c> does not — it fabricates one from the <em>client</em> clock (<c>clientNow + ttl</c>,
    /// computed after the POST returns), which is what the <c>ClientComputedDeadline</c> mutation models as a
    /// numeric mismatch. Because that value is also later than the enforced deadline by the response delay —
    /// even at zero clock skew — it is informational only, never the enforced server deadline; a caller cannot
    /// tell from <see cref="LeaseInfo.ExpiresAt"/> alone which of the two it holds. This test pins the local
    /// side that does honour the contract.
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
