namespace Octoshift.Tests;

using System.Security.Cryptography;
using Octoshift.GitHub;
using Xunit;

/// <summary>
/// <see cref="GitHubAppJwtFactory"/> must fold every expected malformed-private-key failure into
/// <see cref="InvalidOperationException"/>. <c>RSA.ImportFromPem</c> throws <see cref="ArgumentException"/>
/// when the content holds no PEM key — outside the <see cref="CryptographicException"/> the factory already
/// normalized — so a malformed key used to escape as a raw crash at token-mint time.
/// </summary>
public class GitHubAppJwtFactoryTests
{
    private static GitHubAppCredentials CredentialsWithKey(string pem)
        => new("123", 456, pem, new GitHubActorIdentity("nightshift-bot[app]"));

    [Theory]
    [InlineData("not a pem at all")]
    [InlineData("-----BEGIN RSA PRIVATE KEY-----\nnot base64!!!\n-----END RSA PRIVATE KEY-----")]
    public void MalformedPrivateKey_IsInvalidOperation(string pem)
        => Assert.Throws<InvalidOperationException>(() => new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(pem)));

    [Fact]
    public void ValidPrivateKey_SignsAThreeSegmentJwt()
    {
        using RSA rsa = RSA.Create(2048);
        string pem = rsa.ExportPkcs8PrivateKeyPem();

        GitHubAppJwt jwt = new GitHubAppJwtFactory().CreateJwt(CredentialsWithKey(pem));

        // The normalization must not swallow a valid signing: a real RS256 JWT is three dot-separated segments.
        Assert.Equal(3, jwt.Token.Split('.').Length);
    }
}
