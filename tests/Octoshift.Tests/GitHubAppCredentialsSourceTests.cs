namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// <see cref="FileGitHubAppCredentialsSource"/> must fold every expected malformed-configuration failure
/// into <see cref="InvalidOperationException"/> — the one auth-config exception the runner factory consumes —
/// so a broken credentials file surfaces as an unavailable read rather than a raw crash. It must also load a
/// well-formed configuration into the immutable credentials, enforce the same restricted permissions on the
/// private key it enforces on the credentials file, and read each file through the single-open
/// <see cref="IProtectedFileReader"/> seam that closes the check-then-read TOCTOU window.
/// </summary>
public class GitHubAppCredentialsSourceTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";
    private const string CredentialsPath = "/creds/app.json";
    private const string PrivateKeyPath = "/creds/k.pem";

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

    private static FileGitHubAppCredentialsSource SourceReadingBoth(
        string credentialsJson,
        string privateKeyContent,
        UnixFileMode? credentialsMode = UnixFileMode.UserRead,
        UnixFileMode? privateKeyMode = UnixFileMode.UserRead,
        bool enforceOutsideWorkingTree = false)
        => new(
            name => name == CredentialsPathVariable ? CredentialsPath : null,
            () => "/work",
            new FakeReader(new Dictionary<string, ProtectedFileData>(StringComparer.Ordinal)
            {
                [CredentialsPath] = new ProtectedFileData(credentialsMode, credentialsJson),
                [PrivateKeyPath] = new ProtectedFileData(privateKeyMode, privateKeyContent),
            }),
            enforceOutsideWorkingTree);

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

    [Fact]
    public void WellFormedConfiguration_LoadsIntoImmutableCredentials()
    {
        string json = """{"app_id":123,"installation_id":456,"private_key_path":"k.pem","actor":"nightshift-bot[app]"}""";

        GitHubAppCredentials credentials = SourceReadingBoth(json, "PEM-BODY").Load();

        Assert.Equal("123", credentials.AppId);
        Assert.Equal(456, credentials.InstallationId);
        Assert.Equal("PEM-BODY", credentials.PrivateKeyPem);
        Assert.Equal("nightshift-bot[app]", credentials.Actor.Value);
    }

    [Fact]
    public void StringTypedIdentifiers_AreAccepted()
    {
        // The DTO allows reading the numeric ids from JSON strings; a credentials file that quotes them still loads.
        string json = """{"app_id":"123","installation_id":"456","private_key_path":"k.pem","actor":"bot"}""";

        GitHubAppCredentials credentials = SourceReadingBoth(json, "PEM-BODY").Load();

        Assert.Equal("123", credentials.AppId);
        Assert.Equal(456, credentials.InstallationId);
    }

    [Fact]
    public void GroupOrOtherReadablePrivateKey_IsInvalidOperation()
    {
        // The private key is held to the same restricted-permissions bar as the credentials file: a
        // group-readable key is rejected even when the credentials file itself is locked down.
        string json = """{"app_id":123,"installation_id":456,"private_key_path":"k.pem","actor":"bot"}""";

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => SourceReadingBoth(json, "PEM-BODY", privateKeyMode: UnixFileMode.UserRead | UnixFileMode.OtherRead).Load());
        Assert.Contains("private-key", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyPrivateKeyFile_IsInvalidOperation()
    {
        string json = """{"app_id":123,"installation_id":456,"private_key_path":"k.pem","actor":"bot"}""";

        Assert.Throws<InvalidOperationException>(() => SourceReadingBoth(json, "   ").Load());
    }

    [Fact]
    public void CredentialsInsideWorkingTree_AreRejectedWhenEnforced()
    {
        // When enforcement is on, a credentials file whose path lexically resolves under the working tree is
        // refused: a secret that lives in the checkout can be committed by accident, which is the whole reason
        // for the boundary. Note this is a lexical containment check on the normalized path only — it does not
        // resolve symlinks, so it is not by itself a defense against a symlink that points from outside the
        // tree back in (tracked separately). This test asserts only the lexical behavior.
        var source = new FileGitHubAppCredentialsSource(
            name => name == CredentialsPathVariable ? "/work/creds/app.json" : null,
            () => "/work",
            new FakeReader(new Dictionary<string, ProtectedFileData>(StringComparer.Ordinal)
            {
                ["/work/creds/app.json"] = new ProtectedFileData(UnixFileMode.UserRead, "{}"),
            }),
            enforceOutsideWorkingTree: true);

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => source.Load());
        Assert.Contains("outside working tree", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OpenOnceReader_ReturnsModeAndContentFromOneHandle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are the seam under test; Windows returns a null mode.");

        // The production reader is the TOCTOU-closing seam: it opens the file exactly once and reports both the
        // permission mode and the content from that single handle, so nothing can swap the file between the
        // permission check and the read. Prove it against a real 0600 file on disk.
        string path = Path.Combine(AppContext.BaseDirectory, $"creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "secret-content");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        try
        {
            ProtectedFileData data = OpenOnceProtectedFileReader.Instance.Read(path, "credentials");

            Assert.Equal("secret-content", data.Content);
            Assert.NotNull(data.Mode);
            UnixFileMode groupOrOther =
                UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
            Assert.Equal((UnixFileMode)0, data.Mode!.Value & groupOrOther);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenOnceReader_ReflectsGroupReadablePermissionsFromTheHandle()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix file modes are the seam under test; Windows returns a null mode.");

        // The same single handle has to surface a too-open mode faithfully, or the permission gate above it is
        // reading nothing. A group-readable file must come back with the group bit set.
        string path = Path.Combine(AppContext.BaseDirectory, $"creds-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "secret-content");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        }
        try
        {
            ProtectedFileData data = OpenOnceProtectedFileReader.Instance.Read(path, "credentials");

            Assert.NotNull(data.Mode);
            Assert.NotEqual((UnixFileMode)0, data.Mode!.Value & UnixFileMode.GroupRead);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void OpenOnceReader_MissingFileIsInvalidOperation()
    {
        string path = Path.Combine(AppContext.BaseDirectory, $"creds-missing-{Guid.NewGuid():N}.json");

        Assert.Throws<InvalidOperationException>(() => OpenOnceProtectedFileReader.Instance.Read(path, "credentials"));
    }
}
