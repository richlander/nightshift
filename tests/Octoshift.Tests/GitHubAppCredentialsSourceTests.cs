namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// <see cref="FileGitHubAppCredentialsSource"/> must fold every expected malformed-configuration failure
/// into <see cref="InvalidOperationException"/> — the one auth-config exception the runner factory consumes —
/// so a broken credentials file surfaces as an unavailable read rather than a raw crash. The concrete
/// regression is a valid JSON whose <c>private_key_path</c> is a NUL character, which reaches
/// <c>Path.GetFullPath</c> and throws <see cref="ArgumentException"/> outside the rest of the normalization.
/// </summary>
public class GitHubAppCredentialsSourceTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";
    private const string CredentialsPath = "/creds/app.json";

    private sealed class FakeReader(IReadOnlyDictionary<string, ProtectedFileData> files) : IProtectedFileReader
    {
        public ProtectedFileData Read(string path, string label)
            => files.TryGetValue(path, out ProtectedFileData data)
                ? data
                : throw new InvalidOperationException($"octoshift: {label} file '{path}' does not exist.");
    }

    private static FileGitHubAppCredentialsSource SourceReading(string credentialsJson, UnixFileMode? mode = UnixFileMode.UserRead)
        => new(
            name => name == CredentialsPathVariable ? CredentialsPath : null,
            () => "/work",
            new FakeReader(new Dictionary<string, ProtectedFileData>(StringComparer.Ordinal)
            {
                [CredentialsPath] = new ProtectedFileData(mode, credentialsJson),
            }),
            enforceOutsideWorkingTree: false);

    [Fact]
    public void NulCharacterPrivateKeyPath_IsInvalidOperationNotArgumentException()
    {
        // Valid JSON, but private_key_path decodes to a single NUL — a path Path.GetFullPath rejects with an
        // ArgumentException that used to escape Load entirely.
        string json = """{"app_id":123,"installation_id":456,"private_key_path":"\u0000","actor":"nightshift-bot[app]"}""";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => SourceReading(json).Load());
        Assert.Contains("private-key path is not a valid", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MalformedJson_IsInvalidOperation()
        => Assert.Throws<InvalidOperationException>(() => SourceReading("{ not json ]").Load());

    [Theory]
    [InlineData("""{"installation_id":456,"private_key_path":"k.pem","actor":"bot"}""")]           // no app_id
    [InlineData("""{"app_id":123,"private_key_path":"k.pem","actor":"bot"}""")]                    // no installation_id
    [InlineData("""{"app_id":123,"installation_id":456,"actor":"bot"}""")]                         // no private_key_path
    [InlineData("""{"app_id":123,"installation_id":456,"private_key_path":"k.pem"}""")]            // no actor
    public void MissingRequiredField_IsInvalidOperation(string json)
        => Assert.Throws<InvalidOperationException>(() => SourceReading(json).Load());

    [Fact]
    public void GroupOrOtherReadablePermissions_IsInvalidOperation()
    {
        string json = """{"app_id":123,"installation_id":456,"private_key_path":"k.pem","actor":"bot"}""";

        // A credentials file readable by group/other is rejected before its contents are trusted.
        Assert.Throws<InvalidOperationException>(
            () => SourceReading(json, mode: UnixFileMode.UserRead | UnixFileMode.GroupRead).Load());
    }

    [Fact]
    public void UnsetEnvironmentVariable_IsInvalidOperation()
    {
        var source = new FileGitHubAppCredentialsSource(
            _ => null,
            () => "/work",
            new FakeReader(new Dictionary<string, ProtectedFileData>(StringComparer.Ordinal)),
            enforceOutsideWorkingTree: false);

        Assert.Throws<InvalidOperationException>(() => source.Load());
    }
}
