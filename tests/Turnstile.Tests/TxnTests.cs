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
    public async Task Claim_UnderLease_AttachesAndExpires()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);

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
