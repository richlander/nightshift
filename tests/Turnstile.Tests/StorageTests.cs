namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

public class StorageTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-test-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Create_ThenGet_RoundTrips()
    {
        using KvStore store = Open();
        WriteResult r = await store.CreateAsync("/a", Bytes("hello"));
        Assert.Equal(WriteStatus.Created, r.Status);

        KeyState? s = store.Get("/a");
        Assert.NotNull(s);
        Assert.Equal("hello", Encoding.UTF8.GetString(s!.Value!));
        Assert.Equal(r.Revision, s.CreateRevision);
        Assert.Equal(r.Revision, s.ModRevision);
    }

    [Fact]
    public async Task Create_WhenLive_ReturnsExists()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));
        WriteResult second = await store.CreateAsync("/a", Bytes("2"));
        Assert.Equal(WriteStatus.Exists, second.Status);
        Assert.Equal("1", Encoding.UTF8.GetString(store.Get("/a")!.Value!));
    }

    [Fact]
    public async Task Update_WithoutIfMatch_Requires428()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));
        WriteResult r = await store.UpdateAsync("/a", Bytes("2"), ifMatch: null);
        Assert.Equal(WriteStatus.PreconditionRequired, r.Status);
    }

    [Fact]
    public async Task Update_WithStaleIfMatch_Returns412()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/a", Bytes("1"));
        WriteResult r = await store.UpdateAsync("/a", Bytes("2"), ifMatch: created.Revision + 999);
        Assert.Equal(WriteStatus.PreconditionFailed, r.Status);
        Assert.Equal(created.Revision, r.Current!.ModRevision);
    }

    [Fact]
    public async Task Update_WithMatchingIfMatch_Succeeds()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/a", Bytes("1"));
        WriteResult r = await store.UpdateAsync("/a", Bytes("2"), ifMatch: created.Revision);
        Assert.Equal(WriteStatus.Ok, r.Status);

        KeyState? s = store.Get("/a");
        Assert.Equal("2", Encoding.UTF8.GetString(s!.Value!));
        Assert.Equal(created.Revision, s.CreateRevision);
        Assert.Equal(r.Revision, s.ModRevision);
    }

    [Fact]
    public async Task Delete_ThenGet_ReturnsNull_AndCreateReuseWorks()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/a", Bytes("1"));
        WriteResult del = await store.DeleteAsync("/a", ifMatch: created.Revision);
        Assert.Equal(WriteStatus.Deleted, del.Status);
        Assert.Null(store.Get("/a"));

        WriteResult recreate = await store.CreateAsync("/a", Bytes("2"));
        Assert.Equal(WriteStatus.Created, recreate.Status);
        Assert.True(recreate.Revision > del.Revision);
    }

    [Fact]
    public async Task Immutable_CannotBeUpdatedOrDeleted()
    {
        using KvStore store = Open();
        await store.CreateAsync("/spec", Bytes("frozen"), immutable: true);
        Assert.Equal(WriteStatus.Immutable, (await store.UpdateAsync("/spec", Bytes("x"), ifMatch: null, unconditional: true)).Status);
        Assert.Equal(WriteStatus.Immutable, (await store.DeleteAsync("/spec", ifMatch: null, unconditional: true)).Status);
        Assert.Equal("frozen", Encoding.UTF8.GetString(store.Get("/spec")!.Value!));
    }

    [Fact]
    public async Task Range_ReturnsLiveKeysInLexicographicOrder()
    {
        using KvStore store = Open();
        await store.CreateAsync("/order/1234/op/0005", Bytes("a"));
        await store.CreateAsync("/order/1234/op/0010", Bytes("b"));
        await store.CreateAsync("/order/9999/op/0001", Bytes("c"));
        WriteResult toDelete = await store.CreateAsync("/order/1234/op/0007", Bytes("d"));
        await store.DeleteAsync("/order/1234/op/0007", ifMatch: toDelete.Revision);

        IReadOnlyList<KeyState> rows = store.Range("/order/1234/");
        Assert.Equal(["/order/1234/op/0005", "/order/1234/op/0010"], rows.Select(r => r.Key).ToArray());
    }

    [Fact]
    public async Task ClaimRace_ExactlyOneWinner()
    {
        using KvStore store = Open();
        const int contenders = 64;
        using var barrier = new Barrier(contenders);

        var tasks = new Task<WriteStatus>[contenders];
        for (int i = 0; i < contenders; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                return store.CreateAsync("/claim", Bytes("me")).GetAwaiter().GetResult().Status;
            });
        }

        WriteStatus[] outcomes = await Task.WhenAll(tasks);
        Assert.Equal(1, outcomes.Count(s => s == WriteStatus.Created));
        Assert.Equal(contenders - 1, outcomes.Count(s => s == WriteStatus.Exists));
    }

    [Fact]
    public async Task Revisions_AreStrictlyMonotonicAndGapless()
    {
        using KvStore store = Open();
        var revisions = new List<long>();
        for (int i = 0; i < 50; i++)
        {
            WriteResult r = await store.CreateAsync($"/k/{i:D4}", Bytes("v"));
            revisions.Add(r.Revision);
        }

        for (int i = 1; i < revisions.Count; i++)
        {
            Assert.Equal(revisions[i - 1] + 1, revisions[i]);
        }
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
