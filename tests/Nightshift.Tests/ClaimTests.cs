namespace Nightshift.Tests;

using Nightshift.Turnstile;
using Xunit;

/// <summary>The load-bearing guarantee: a claim is a compare-and-put, so exactly one caller wins a key.</summary>
public class ClaimTests : IClassFixture<TurnstileFixture>
{
    private readonly TurnstileFixture _fixture;

    public ClaimTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task SecondClaimOnSameKey_Loses()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = $"/claim-test/{Guid.NewGuid():N}";
        string lease = await client.CreateLeaseAsync(60, ct);

        ClaimResult first = await client.TryClaimAsync(key, lease, "one", ct);
        ClaimResult second = await client.TryClaimAsync(key, lease, "two", ct);

        Assert.True(first.Won);
        Assert.False(second.Won);
    }

    [Fact]
    public async Task ConcurrentClaims_ExactlyOneWins()
    {
        using TurnstileClient client = _fixture.Connect();
        CancellationToken ct = TestContext.Current.CancellationToken;
        string key = $"/claim-test/{Guid.NewGuid():N}";
        string lease = await client.CreateLeaseAsync(60, ct);

        Task<ClaimResult>[] racers =
        [
            .. Enumerable.Range(0, 8).Select(i => client.TryClaimAsync(key, lease, $"a{i}", ct)),
        ];
        ClaimResult[] results = await Task.WhenAll(racers);

        Assert.Equal(1, results.Count(r => r.Won));
    }
}
