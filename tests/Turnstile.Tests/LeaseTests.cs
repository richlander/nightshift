namespace Turnstile.Tests;

using System.Text;
using Turnstile.Storage;
using Xunit;

public class LeaseTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-lease-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Sweep_ExpiredLease_DeletesAttachedKeys()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 1);
        await store.CreateAsync("/agent/dev-b", Bytes("host"), lease: lease.Id);
        await store.CreateAsync("/order/1/claim", Bytes("dev-b"), lease: lease.Id);
        Assert.NotNull(store.Get("/agent/dev-b"));

        // Force the deadline into the past by waiting past the TTL, then sweep.
        await Task.Delay(1200, TestContext.Current.CancellationToken);
        int deleted = await store.SweepExpiredAsync();

        Assert.Equal(2, deleted);
        Assert.Null(store.Get("/agent/dev-b"));
        Assert.Null(store.Get("/order/1/claim"));
        Assert.Null(store.GetLease(lease.Id));
    }

    [Fact]
    public async Task KeepAlive_PreventsExpiry()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 5);
        await store.CreateAsync("/agent/dev-c", Bytes("host"), lease: lease.Id);

        long? remaining = await store.KeepAliveAsync(lease.Id);
        Assert.Equal(5, remaining);

        int deleted = await store.SweepExpiredAsync();
        Assert.Equal(0, deleted);
        Assert.NotNull(store.Get("/agent/dev-c"));
    }

    [Fact]
    public async Task KeepAlive_OnUnknownLease_ReturnsNull()
    {
        using KvStore store = Open();
        Assert.Null(await store.KeepAliveAsync("deadbeefdeadbeefdeadbeefdeadbeef"));
    }

    [Fact]
    public async Task Revoke_DeletesAttachedKeysAtomically()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 3600);
        await store.CreateAsync("/a", Bytes("1"), lease: lease.Id);
        await store.CreateAsync("/b", Bytes("2"), lease: lease.Id);

        Assert.True(await store.RevokeLeaseAsync(lease.Id));
        Assert.Null(store.Get("/a"));
        Assert.Null(store.Get("/b"));
        Assert.False(await store.RevokeLeaseAsync(lease.Id));
    }

    [Fact]
    public async Task Create_WithUnknownLease_Rejected()
    {
        using KvStore store = Open();
        await Assert.ThrowsAsync<TurnstileValidationException>(
            () => store.CreateAsync("/x", Bytes("1"), lease: "0000000000000000feedfacefeedface"));
    }

    [Fact]
    public async Task GetLease_ReportsAttachedKeys()
    {
        using KvStore store = Open();
        LeaseInfo lease = await store.CreateLeaseAsync(ttlSecs: 3600);
        await store.CreateAsync("/agent/dev-d", Bytes("host"), lease: lease.Id);

        LeaseView? view = store.GetLease(lease.Id);
        Assert.NotNull(view);
        Assert.Equal(["/agent/dev-d"], view!.Keys);
        Assert.True(view.TtlRemaining > 0);
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
