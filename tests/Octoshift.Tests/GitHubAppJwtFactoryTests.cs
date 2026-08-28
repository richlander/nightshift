namespace Octoshift.Tests;

using System.Security.Cryptography;
using System.Text.Json;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// <see cref="GitHubAppJwtFactory"/> must fold every expected malformed-private-key failure into
/// <see cref="InvalidOperationException"/>. <c>RSA.ImportFromPem</c> throws <see cref="ArgumentException"/>
/// when the content holds no PEM key — outside the <see cref="CryptographicException"/> the factory already
/// normalized — so a malformed key used to escape as a raw crash at token-mint time. Beyond that, the token
/// it emits has to be a real RS256 JWT: base64url segments (no <c>+ / =</c>), an RS256 header, and
/// <c>iss/iat/exp</c> claims that follow the configured clock, lifetime and skew backdate.
/// </summary>
public class GitHubAppJwtFactoryTests
{
    private static GitHubAppCredentials CredentialsWithKey(string pem)
        => new("123", 456, pem, new GitHubActorIdentity("nightshift-bot[app]"));

    private static string FreshPrivateKeyPem()
    {
        using RSA rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }
    [Theory]
    [InlineData("not a pem at all")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nnot base64!!!\n-----END RSA PRIVATE KEY-----")]
    public void MalformedPrivateKey_IsInvalidOperation(string pem)
        => Assert.Throws<InvalidOperationException>(() => new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(pem)));

    [Fact]
    public void ValidPrivateKey_SignsAThreeSegmentJwt()
    {
        GitHubAppJwt jwt = new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(FreshPrivateKeyPem()));

        // The normalization must not swallow a valid signing: a real RS256 JWT is three dot-separated segments.
        Assert.Equal(3, jwt.Token.Split('.').Length);
    }

    [Fact]
    public void EverySegmentIsBase64UrlWithNoPaddingOrStandardAlphabet()
    {
        // base64url is the JWT wire alphabet: '-' and '_' for '+' and '/', and no '=' padding. A token that
        // leaked a standard-alphabet character would be rejected by GitHub, so assert it on every segment —
        // including the signature, whose 256 random bytes reliably exercise both substituted characters.
        string token = new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(FreshPrivateKeyPem())).Token;

        foreach (string segment in token.Split('.'))
        {
            Assert.DoesNotContain('+', segment);
            Assert.DoesNotContain('/', segment);
            Assert.DoesNotContain('=', segment);

            // And it must still decode as base64url back to bytes — proving the substitution is a real
            // re-encoding, not just stripped characters.
            _ = DecodeBase64Url(segment);
        }
    }

    [Fact]
    public void HeaderDeclaresRs256Jwt()
    {
        string token = new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(FreshPrivateKeyPem())).Token;

        using JsonDocument header = JsonDocument.Parse(DecodeBase64Url(token.Split('.')[0]));
        Assert.Equal("RS256", header.RootElement.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.RootElement.GetProperty("typ").GetString());
    }

    [Fact]
    public void ClaimsFollowTheConfiguredClockLifetimeAndSkewBackdate()
    {
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        TimeSpan lifetime = TimeSpan.FromMinutes(9);
        TimeSpan backdate = TimeSpan.FromSeconds(30);
        var factory = new GitHubAppJwtFactory(() => now, lifetime, backdate);

        GitHubAppJwt jwt = factory.CreateJwt(new GitHubAppCredentials("789", 456, FreshPrivateKeyPem(), new GitHubActorIdentity("bot[app]")));

        using JsonDocument payload = JsonDocument.Parse(DecodeBase64Url(jwt.Token.Split('.')[1]));
        long expectedIat = now.Subtract(backdate).ToUnixTimeSeconds();
        long expectedExp = now.Subtract(backdate).Add(lifetime).ToUnixTimeSeconds();

        Assert.Equal("789", payload.RootElement.GetProperty("iss").GetString());
        Assert.Equal(expectedIat, payload.RootElement.GetProperty("iat").GetInt64());
        Assert.Equal(expectedExp, payload.RootElement.GetProperty("exp").GetInt64());

        // The returned expiry mirrors the exp claim (issued-at, backdated, plus lifetime).
        Assert.Equal(expectedExp, jwt.ExpiresAt.ToUnixTimeSeconds());
    }

    [Theory]
    [InlineData(0)]        // zero lifetime
    [InlineData(-1)]       // negative lifetime
    [InlineData(11)]       // past GitHub's 10-minute ceiling
    public void OutOfRangeLifetime_IsRejected(int minutes)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new GitHubAppJwtFactory(() => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(minutes), TimeSpan.FromSeconds(30)));

    [Fact]
    public void NegativeSkewBackdate_IsRejected()
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => new GitHubAppJwtFactory(() => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(-1)));

    private static byte[] DecodeBase64Url(string segment)
    {
        string standard = segment.Replace('-', '+').Replace('_', '/');
        standard += (segment.Length % 4) switch
        {
            2 => "==",
            3 => "=",
            _ => string.Empty,
        };

        return Convert.FromBase64String(standard);
    }
}
