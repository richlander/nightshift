namespace Nightshift.Tests;

using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The self-cleaning roster primitive behind join/standby: a lease-attached upsert that overwrites in
/// place and vanishes when its lease is revoked (so a departed agent leaves no stale presence).
/// </summary>
public class RosterTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public RosterTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PutLeased_CreatesKeyAttachedToLease()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = $"/agent/{Guid.NewGuid():N}";
        string lease = await client.CreateLeaseAsync(60, ct);

        await client.PutLeasedAsync(key, "active", lease, ct);

        KvItem? item = await client.GetAsync(key, ct);
        Assert.NotNull(item);
        Assert.Equal("active", item!.Text);
        Assert.Equal(lease, item.Lease);
    }

    [Fact]
    public async Task PutLeased_OverwritesValueInPlace()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = $"/agent/{Guid.NewGuid():N}";
        string lease = await client.CreateLeaseAsync(60, ct);

        await client.PutLeasedAsync(key, "active", lease, ct);
        await client.PutLeasedAsync(key, "standby", lease, ct);

        KvItem? item = await client.GetAsync(key, ct);
        Assert.NotNull(item);
        Assert.Equal("standby", item!.Text);
        Assert.Equal(lease, item.Lease);
    }

    [Fact]
    public async Task RevokingLease_RemovesRosterEntry()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = $"/agent/{Guid.NewGuid():N}";
        string lease = await client.CreateLeaseAsync(60, ct);
        await client.PutLeasedAsync(key, "active", lease, ct);

        await client.RevokeLeaseAsync(lease, ct);

        Assert.Null(await client.GetAsync(key, ct));
    }
}
