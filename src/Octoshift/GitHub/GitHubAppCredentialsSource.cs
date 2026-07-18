namespace Octoshift.GitHub;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Immutable GitHub App credentials loaded by the resident octoshift process.
/// </summary>
internal sealed class GitHubAppCredentials
{
    public GitHubAppCredentials(string appId, long installationId, string privateKeyPem, GitHubActorIdentity actor)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            throw new ArgumentException("GitHub App id must be non-empty.", nameof(appId));
        }

        if (installationId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(installationId), "Installation id must be positive.");
        }

        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new ArgumentException("GitHub App private key PEM must be non-empty.", nameof(privateKeyPem));
        }

        AppId = appId;
        InstallationId = installationId;
        PrivateKeyPem = privateKeyPem;
        Actor = actor;
    }

    /// <summary>App id used as the JWT <c>iss</c> claim.</summary>
    public string AppId { get; }

    /// <summary>Installation id used to mint installation access tokens.</summary>
    public long InstallationId { get; }

    /// <summary>PEM-encoded RSA private key used to sign RS256 app JWTs.</summary>
    public string PrivateKeyPem { get; }

    /// <summary>Configured actor identity attributed in audit records.</summary>
    public GitHubActorIdentity Actor { get; }
}

/// <summary>
/// Source for loading GitHub App credentials from a protected location outside the working tree.
/// </summary>
internal interface IGitHubAppCredentialsSource
{
    /// <summary>Loads and validates immutable GitHub App credentials.</summary>
    GitHubAppCredentials Load();
}

/// <summary>
/// File-backed credential source configured by <c>OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH</c>.
/// </summary>
internal sealed class FileGitHubAppCredentialsSource : IGitHubAppCredentialsSource
{
    public const string CredentialsPathEnvironmentVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";

    private readonly Func<string, string?> _getEnvironmentVariable;
    private readonly Func<string> _getWorkingDirectory;
    private readonly IProtectedFileReader _protectedFileReader;
    private readonly bool _enforceOutsideWorkingTree;

    public FileGitHubAppCredentialsSource()
        : this(
            Environment.GetEnvironmentVariable,
            Directory.GetCurrentDirectory,
            OpenOnceProtectedFileReader.Instance,
            enforceOutsideWorkingTree: true)
    {
    }

    internal FileGitHubAppCredentialsSource(
        Func<string, string?> getEnvironmentVariable,
        Func<string> getWorkingDirectory,
        IProtectedFileReader protectedFileReader,
        bool enforceOutsideWorkingTree)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _getWorkingDirectory = getWorkingDirectory;
        _protectedFileReader = protectedFileReader;
        _enforceOutsideWorkingTree = enforceOutsideWorkingTree;
    }

    public GitHubAppCredentials Load()
    {
        string? configuredPath = _getEnvironmentVariable(CredentialsPathEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            throw new InvalidOperationException($"octoshift: set {CredentialsPathEnvironmentVariable} to the GitHub App credentials file path.");
        }

        string credentialsPath = Path.GetFullPath(configuredPath);
        if (_enforceOutsideWorkingTree)
        {
            EnsureOutsideWorkingTree(credentialsPath, "credentials");
        }

        ProtectedFileData credentialsFile = _protectedFileReader.Read(credentialsPath, "credentials");
        EnsureRestrictedPermissions(credentialsFile.Mode, credentialsPath, "credentials");

        GitHubAppCredentialFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(credentialsFile.Content, GitHubAppCredentialsJsonContext.Default.GitHubAppCredentialFileDto);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"octoshift: credentials file '{credentialsPath}' is not valid JSON.", ex);
        }

        if (dto is null)
        {
            throw new InvalidOperationException($"octoshift: credentials file '{credentialsPath}' was empty.");
        }

        if (dto.AppId is not > 0)
        {
            throw new InvalidOperationException("octoshift: credentials must include a positive app_id.");
        }

        if (dto.InstallationId is not > 0)
        {
            throw new InvalidOperationException("octoshift: credentials must include a positive installation_id.");
        }

        if (string.IsNullOrWhiteSpace(dto.PrivateKeyPath))
        {
            throw new InvalidOperationException("octoshift: credentials must include private_key_path.");
        }

        if (string.IsNullOrWhiteSpace(dto.Actor))
        {
            throw new InvalidOperationException("octoshift: credentials must include actor for audit attribution.");
        }

        string baseDirectory = Path.GetDirectoryName(credentialsPath) ?? _getWorkingDirectory();
        string privateKeyPath = Path.GetFullPath(dto.PrivateKeyPath, baseDirectory);
        if (_enforceOutsideWorkingTree)
        {
            EnsureOutsideWorkingTree(privateKeyPath, "private-key");
        }

        ProtectedFileData privateKeyFile = _protectedFileReader.Read(privateKeyPath, "private-key");
        EnsureRestrictedPermissions(privateKeyFile.Mode, privateKeyPath, "private-key");
        if (string.IsNullOrWhiteSpace(privateKeyFile.Content))
        {
            throw new InvalidOperationException($"octoshift: private key file '{privateKeyPath}' was empty.");
        }

        return new GitHubAppCredentials(
            dto.AppId.Value.ToString(CultureInfo.InvariantCulture),
            dto.InstallationId.Value,
            privateKeyFile.Content,
            new GitHubActorIdentity(dto.Actor));
    }

    private static void EnsureRestrictedPermissions(UnixFileMode? mode, string path, string label)
    {
        if (mode is null)
        {
            return;
        }

        UnixFileMode disallowed =
            UnixFileMode.GroupRead |
            UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherWrite |
            UnixFileMode.OtherExecute;

        if ((mode.Value & disallowed) != 0)
        {
            throw new InvalidOperationException(
                $"octoshift: {label} file '{path}' must not grant group/other permissions.");
        }
    }

    private void EnsureOutsideWorkingTree(string path, string label)
    {
        string workingTree = Path.GetFullPath(_getWorkingDirectory());
        if (IsInDirectory(path, workingTree))
        {
            throw new InvalidOperationException(
                $"octoshift: {label} file '{path}' must be outside working tree '{workingTree}'.");
        }
    }

    private static bool IsInDirectory(string path, string directory)
    {
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        string normalizedDirectory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedPath = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        if (string.Equals(normalizedPath, normalizedDirectory, comparison))
        {
            return true;
        }

        string prefix = normalizedDirectory + Path.DirectorySeparatorChar;
        return normalizedPath.StartsWith(prefix, comparison);
    }
}

/// <summary>The mode and content read from one protected file handle.</summary>
internal readonly record struct ProtectedFileData(UnixFileMode? Mode, string Content);

/// <summary>
/// Injectable seam that opens one protected file once and returns both its permissions and content.
/// </summary>
internal interface IProtectedFileReader
{
    /// <summary>Reads the file identified by <paramref name="path"/> with a single open handle.</summary>
    ProtectedFileData Read(string path, string label);
}

/// <summary>
/// Production protected-file reader: opens exactly once, checks Unix mode from that handle, then reads from it.
/// </summary>
internal sealed class OpenOnceProtectedFileReader : IProtectedFileReader
{
    public static OpenOnceProtectedFileReader Instance { get; } = new();

    private OpenOnceProtectedFileReader()
    {
    }

    public ProtectedFileData Read(string path, string label)
    {
        try
        {
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            UnixFileMode? mode = ReadMode(stream, path, label);
            string content = ReadContent(stream, path, label);
            return new ProtectedFileData(mode, content);
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException($"octoshift: {label} file '{path}' does not exist.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException($"octoshift: {label} file '{path}' does not exist.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"octoshift: {label} file '{path}' is not readable.", ex);
        }
    }

    private static UnixFileMode? ReadMode(FileStream stream, string path, string label)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return File.GetUnixFileMode(stream.SafeFileHandle);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"octoshift: unable to read permissions for {label} file '{path}'.", ex);
        }
    }

    private static string ReadContent(FileStream stream, string path, string label)
    {
        try
        {
            using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"octoshift: unable to read {ReadableLabel(label)} file '{path}'.", ex);
        }
    }

    private static string ReadableLabel(string label)
        => string.Equals(label, "private-key", StringComparison.Ordinal) ? "private key" : label;
}

internal sealed record GitHubAppCredentialFileDto
{
    [JsonPropertyName("app_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? AppId { get; init; }

    [JsonPropertyName("installation_id")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public long? InstallationId { get; init; }

    [JsonPropertyName("private_key_path")]
    public string? PrivateKeyPath { get; init; }

    [JsonPropertyName("actor")]
    public string? Actor { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(GitHubAppCredentialFileDto))]
internal partial class GitHubAppCredentialsJsonContext : JsonSerializerContext
{
}
