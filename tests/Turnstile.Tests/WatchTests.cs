namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

public class WatchTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-watch-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task ReadEvents_ReturnsRowsInRevisionOrder()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));
        await store.CreateAsync("/b", Bytes("2"));
        await store.CreateAsync("/c", Bytes("3"));

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: 0, limit: 0);
        Assert.Equal(["/a", "/b", "/c"], events.Select(e => e.Key).ToArray());
        Assert.Equal([1L, 2L, 3L], events.Select(e => e.Revision).ToArray());
        Assert.All(events, e => Assert.False(e.Deleted));
    }

    [Fact]
    public async Task ReadEvents_ResumesFromCursor()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));
        WriteResult second = await store.CreateAsync("/b", Bytes("2"));
        await store.CreateAsync("/c", Bytes("3"));

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: second.Revision, limit: 0);
        WatchEvent only = Assert.Single(events);
        Assert.Equal("/c", only.Key);
    }

    [Fact]
    public async Task ReadEvents_FiltersByPrefix()
    {
        using KvStore store = Open();
        await store.CreateAsync("/ready/1", Bytes("x"));
        await store.CreateAsync("/order/2", Bytes("y"));
        await store.CreateAsync("/ready/2", Bytes("z"));

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/ready/", fromExclusive: 0, limit: 0);
        Assert.Equal(["/ready/1", "/ready/2"], events.Select(e => e.Key).ToArray());
    }

    [Fact]
    public async Task ReadEvents_IncludesEveryVersion_AndDeleteCarriesPrevValue()
    {
        using KvStore store = Open();
        WriteResult created = await store.CreateAsync("/k", Bytes("1"));
        WriteResult updated = await store.UpdateAsync("/k", Bytes("2"), ifMatch: created.Revision);
        await store.DeleteAsync("/k", ifMatch: updated.Revision);

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: 0, limit: 0);
        Assert.Equal(3, events.Count);
        Assert.False(events[0].Deleted);
        Assert.False(events[1].Deleted);

        WatchEvent delete = events[2];
        Assert.True(delete.Deleted);
        Assert.Equal("2", Encoding.UTF8.GetString(delete.PrevValue!));
    }

    [Fact]
    public async Task WaitForChange_CompletesAfterAWrite()
    {
        using KvStore store = Open();
        Task changed = store.WaitForChangeAsync();
        Assert.False(changed.IsCompleted);

        await store.CreateAsync("/a", Bytes("1"));
        await changed.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(changed.IsCompleted);
    }

    [Fact]
    public async Task LeaseExpiry_ProducesDeleteEvent()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);
        WriteResult created = await store.CreateAsync("/ephemeral", Bytes("v"), lease: lease.Id);

        // Force expiry deterministically rather than waiting on the sweeper's wall-clock tick.
        await Task.Delay(TimeSpan.FromMilliseconds(1100), TestContext.Current.CancellationToken);
        int deleted = await store.SweepExpiredAsync();
        Assert.Equal(1, deleted);

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: created.Revision, limit: 0);
        WatchEvent delete = Assert.Single(events);
        Assert.Equal("/ephemeral", delete.Key);
        Assert.True(delete.Deleted);
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
