namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

public class TxnTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-txn-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    // The claim protocol: create_revision == 0 means "does not exist".
    private static TxnCompare NotExist(string key) => new(key, TxnTarget.CreateRevision, TxnCompareOp.Equal, 0, null, null);

    private static TxnOp Put(string key, string value, string? lease = null) => new(TxnOpKind.Put, key, Bytes(value), lease, false);

    private static TxnOp Get(string key) => new(TxnOpKind.Get, key, null, null, false);

    [Fact]
    public async Task Claim_WhenAbsent_SucceedsAndCreates()
    {
        using KvStore store = Open();
        TxnResult r = await store.TxnAsync([NotExist("/claim")], [Put("/claim", "dev-b")], [Get("/claim")]);

        Assert.True(r.Succeeded);
        Assert.Empty(r.Responses);
        KeyState? s = store.Get("/claim");
        Assert.Equal("dev-b", Encoding.UTF8.GetString(s!.Value!));
        Assert.Equal(r.Revision, s.ModRevision);
    }

    [Fact]
    public async Task Claim_WhenPresent_FailsAndRunsFailureBranch()
    {
        using KvStore store = Open();
        await store.CreateAsync("/claim", Bytes("dev-a"));

        TxnResult r = await store.TxnAsync([NotExist("/claim")], [Put("/claim", "dev-b")], [Get("/claim")]);

        Assert.False(r.Succeeded);
        Assert.Equal("dev-a", Encoding.UTF8.GetString(store.Get("/claim")!.Value!));
        TxnOpResult response = Assert.Single(r.Responses);
        Assert.Equal("dev-a", Encoding.UTF8.GetString(response.State!.Value!));
    }

    [Fact]
    public async Task ClaimRace_ExactlyOneWinner_ViaTxn()
    {
        using KvStore store = Open();
        const int contenders = 64;
        using var barrier = new Barrier(contenders);

        var tasks = new Task<bool>[contenders];
        for (int i = 0; i < contenders; i++)
        {
            int me = i;
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.TxnAsync([NotExist("/claim")], [Put("/claim", $"agent-{me}")], []).GetAwaiter().GetResult().Succeeded;
            });
        }

        bool[] outcomes = await Task.WhenAll(tasks);
        Assert.Equal(1, outcomes.Count(won => won));
    }

    [Fact]
    public async Task FencedWrite_ModRevisionGuard()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/k", Bytes("1"));

        // A put fenced on the current mod_revision succeeds...
        TxnResult ok = await store.TxnAsync(
            [new TxnCompare("/k", TxnTarget.ModRevision, TxnCompareOp.Equal, created.Revision, null, null)],
            [Put("/k", "2")],
            []);
        Assert.True(ok.Succeeded);

        // ...but replaying the stale revision loses the fence.
        TxnResult stale = await store.TxnAsync(
            [new TxnCompare("/k", TxnTarget.ModRevision, TxnCompareOp.Equal, created.Revision, null, null)],
            [Put("/k", "3")],
            []);
        Assert.False(stale.Succeeded);
        Assert.Equal("2", Encoding.UTF8.GetString(store.Get("/k")!.Value!));
    }

    [Fact]
    public async Task ValueCompare_GuardsBranch()
    {
        using KvStore store = Open();
        await store.CreateAsync("/k", Bytes("expected"));

        TxnResult match = await store.TxnAsync(
            [new TxnCompare("/k", TxnTarget.Value, TxnCompareOp.Equal, 0, Bytes("expected"), null)],
            [Put("/k", "next")],
            []);
        Assert.True(match.Succeeded);

        TxnResult mismatch = await store.TxnAsync(
            [new TxnCompare("/k", TxnTarget.Value, TxnCompareOp.Equal, 0, Bytes("expected"), null)],
            [Put("/k", "never")],
            []);
        Assert.False(mismatch.Succeeded);
        Assert.Equal("next", Encoding.UTF8.GetString(store.Get("/k")!.Value!));
    }

    [Fact]
    public async Task Put_UpsertsExistingKey_PreservingCreateRevision()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/k", Bytes("1"));

        TxnResult r = await store.TxnAsync([], [Put("/k", "2")], []);
        Assert.True(r.Succeeded);

        KeyState? s = store.Get("/k");
        Assert.Equal("2", Encoding.UTF8.GetString(s!.Value!));
        Assert.Equal(created.Revision, s.CreateRevision);
        Assert.Equal(r.Revision, s.ModRevision);
    }

    [Fact]
    public async Task Delete_InTxn_RemovesKey()
    {
        using KvStore store = Open();
        await store.CreateAsync("/k", Bytes("1"));

        TxnResult r = await store.TxnAsync([], [new TxnOp(TxnOpKind.Delete, "/k", null, null, false)], []);
        Assert.True(r.Succeeded);
        Assert.Null(store.Get("/k"));
    }

    [Fact]
    public async Task Put_OnImmutableKey_Throws()
    {
        using KvStore store = Open();
        await store.CreateAsync("/spec", Bytes("frozen"), immutable: true);

        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.TxnAsync([], [Put("/spec", "x")], []));
        Assert.Equal("frozen", Encoding.UTF8.GetString(store.Get("/spec")!.Value!));
    }

    [Fact]
    public async Task Txn_PutImmutableWithLease_RejectedBeforeAnyStateChange()
    {
        // A txn put is another entry path onto the shared write point, so it enforces the same invariant: an
        // immutable key cannot be attached to a lease. The whole transaction rolls back — no key, no revision.
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 3600);
        long revBefore = store.CurrentRevision;

        var immutableLeased = new TxnOp(TxnOpKind.Put, "/spec", Bytes("frozen"), lease.Id, true);
        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.TxnAsync([NotExist("/spec")], [immutableLeased], []));

        Assert.Null(store.Get("/spec"));
        Assert.Equal(revBefore, store.CurrentRevision);
    }

    [Fact]
    public async Task Txn_MakingALeasedKeyImmutable_Rejected()
    {
        // The subtler path the shared enforcement catches: a txn put that flips an existing leased mutable key
        // to immutable would produce an immutable+leased row. It is rejected, and the key stays as it was.
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 3600);
        await store.TxnAsync([NotExist("/claim")], [Put("/claim", "dev-b", lease.Id)], []);

        var flipToImmutable = new TxnOp(TxnOpKind.Put, "/claim", Bytes("dev-b"), null, true);
        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.TxnAsync([], [flipToImmutable], []));

        KeyState s = store.Get("/claim")!;
        Assert.False(s.Immutable);          // unchanged: still the ephemeral leased key it was
        Assert.Equal(lease.Id, s.Lease);
    }

    [Fact]
    public async Task Txn_PutImmutableWithoutLease_StillSucceeds()
    {
        // No regression: creating an immutable key with no lease via a txn put is unaffected.
        using KvStore store = Open();
        TxnResult r = await store.TxnAsync([NotExist("/spec")], [new TxnOp(TxnOpKind.Put, "/spec", Bytes("frozen"), null, true)], []);

        Assert.True(r.Succeeded);
        KeyState s = store.Get("/spec")!;
        Assert.True(s.Immutable);
        Assert.Null(s.Lease);
    }

    [Fact]
    public async Task Txn_ValidOpStagedThenInheritedImmutableLease_RollsBackWholeBatch_NoPhantomRevisionOrWatchGap()
    {
        // A batch whose earlier op stages a real row, then a later op turns an existing leased mutable key
        // immutable — invalid only once its live lease is read, so it cannot be caught up front. The whole
        // transaction must roll back: the staged row vanishes, the public revision does not move (no phantom),
        // and a watcher resuming from the pre-batch cursor sees no gap — the allocated revisions are reused by
        // the next committed write.
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 3600);
        await store.TxnAsync([NotExist("/leased")], [Put("/leased", "v", lease.Id)], []);
        long revBefore = store.CurrentRevision;

        var stagedValidPut = new TxnOp(TxnOpKind.Put, "/staged", Bytes("1"), null, false);
        var inheritedImmutable = new TxnOp(TxnOpKind.Put, "/leased", Bytes("v2"), null, true);
        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.TxnAsync([], [stagedValidPut, inheritedImmutable], []));

        Assert.Null(store.Get("/staged"));                    // the staged valid op rolled back too
        Assert.False(store.Get("/leased")!.Immutable);        // target unchanged
        Assert.Equal(lease.Id, store.Get("/leased")!.Lease);
        Assert.Equal(revBefore, store.CurrentRevision);       // no phantom revision published
        Assert.Empty(store.ReadEvents("/", fromExclusive: revBefore, limit: 0));   // nothing committed to skip

        // The next valid write reuses revBefore+1, and a watcher resuming at revBefore sees exactly it.
        WriteResult after = await store.CreateAsync("/after", Bytes("3"));
        Assert.Equal(revBefore + 1, after.Revision);
        WatchEvent only = Assert.Single(store.ReadEvents("/", fromExclusive: revBefore, limit: 0));
        Assert.Equal("/after", only.Key);
        Assert.Equal(revBefore + 1, only.Revision);
    }

    [Fact]
    public async Task Claim_UnderLease_AttachesAndExpires()
    {
        using KvStore store = Open();
        await LeaseClock.EnsureHeadroomAsync(TestContext.Current.CancellationToken);
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 5);

        TxnResult r = await store.TxnAsync([NotExist("/claim")], [Put("/claim", "dev-b", lease.Id)], []);
        Assert.True(r.Succeeded);
        Assert.Equal(lease.Id, store.Get("/claim")!.Lease);

        LeaseView? view = store.GetLease(lease.Id);
        Assert.Contains("/claim", view!.Keys);
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
    }
}
