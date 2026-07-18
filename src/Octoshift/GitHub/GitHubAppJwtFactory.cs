namespace Octoshift.GitHub;

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

/// <summary>A GitHub App JWT plus its expiry instant.</summary>
internal readonly record struct GitHubAppJwt(string Token, DateTimeOffset ExpiresAt);

/// <summary>
/// Creates RS256-signed GitHub App JWTs (<c>iss</c>, <c>iat</c>, <c>exp</c>) from immutable credentials.
/// </summary>
internal sealed class GitHubAppJwtFactory
{
    private static ReadOnlySpan<byte> HeaderJson => "{\"alg\":\"RS256\",\"typ\":\"JWT\"}"u8;

    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _jwtLifetime;
    private readonly TimeSpan _clockSkewBackdate;

    public GitHubAppJwtFactory()
        : this(() => DateTimeOffset.UtcNow, TimeSpan.FromMinutes(9), TimeSpan.FromSeconds(30))
    {
    }

    internal GitHubAppJwtFactory(Func<DateTimeOffset> clock, TimeSpan jwtLifetime, TimeSpan clockSkewBackdate)
    {
        if (jwtLifetime <= TimeSpan.Zero || jwtLifetime > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(jwtLifetime), "GitHub App JWT lifetime must be > 0 and <= 10 minutes.");
        }

        if (clockSkewBackdate < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(clockSkewBackdate), "Clock skew backdate must be non-negative.");
        }

        _clock = clock;
        _jwtLifetime = jwtLifetime;
        _clockSkewBackdate = clockSkewBackdate;
    }

    /// <summary>Mints one signed JWT for the configured app identity.</summary>
    public GitHubAppJwt CreateJwt(GitHubAppCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);

        DateTimeOffset issuedAt = _clock().Subtract(_clockSkewBackdate);
        DateTimeOffset expiresAt = issuedAt.Add(_jwtLifetime);

        string headerSegment = Base64UrlEncode(HeaderJson);
        string payloadSegment = Base64UrlEncode(BuildClaimsPayload(credentials.AppId, issuedAt, expiresAt));
        string signingInput = string.Concat(headerSegment, ".", payloadSegment);

        byte[] signature = Sign(signingInput, credentials.PrivateKeyPem);
        string signatureSegment = Base64UrlEncode(signature);
        string jwt = string.Concat(signingInput, ".", signatureSegment);

        return new GitHubAppJwt(jwt, expiresAt);
    }

    private static byte[] BuildClaimsPayload(string appId, DateTimeOffset issuedAt, DateTimeOffset expiresAt)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("iat", issuedAt.ToUnixTimeSeconds());
            writer.WriteNumber("exp", expiresAt.ToUnixTimeSeconds());
            writer.WriteString("iss", appId);
            writer.WriteEndObject();
            writer.Flush();
        }

        return buffer.WrittenMemory.ToArray();
    }

    private static byte[] Sign(string signingInput, string privateKeyPem)
    {
        try
        {
            using RSA rsa = RSA.Create();
            rsa.ImportFromPem(privateKeyPem);
            return rsa.SignData(
                Encoding.ASCII.GetBytes(signingInput),
                HashAlgorithmName.SHA256,
                RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("octoshift: failed to sign GitHub App JWT with configured private key.", ex);
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace("+", "-", StringComparison.Ordinal)
            .Replace("/", "_", StringComparison.Ordinal);
}
