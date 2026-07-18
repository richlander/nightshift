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
    private readonly Func<string, string> _readAllText;
    private readonly Func<string> _getWorkingDirectory;
    private readonly Func<string, UnixFileMode?> _getUnixFileMode;
    private readonly bool _enforceOutsideWorkingTree;

    public FileGitHubAppCredentialsSource()
        : this(
            Environment.GetEnvironmentVariable,
            File.ReadAllText,
            Directory.GetCurrentDirectory,
            TryGetUnixFileMode,
            enforceOutsideWorkingTree: true)
    {
    }

    internal FileGitHubAppCredentialsSource(
        Func<string, string?> getEnvironmentVariable,
        Func<string, string> readAllText,
        Func<string> getWorkingDirectory,
        Func<string, UnixFileMode?> getUnixFileMode,
        bool enforceOutsideWorkingTree)
    {
        _getEnvironmentVariable = getEnvironmentVariable;
        _readAllText = readAllText;
        _getWorkingDirectory = getWorkingDirectory;
        _getUnixFileMode = getUnixFileMode;
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
        EnsureReadableFile(credentialsPath, "credentials");
        EnsureRestrictedPermissions(credentialsPath, "credentials");
        if (_enforceOutsideWorkingTree)
        {
            EnsureOutsideWorkingTree(credentialsPath, "credentials");
        }

        string json;
        try
        {
            json = _readAllText(credentialsPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"octoshift: unable to read credentials file '{credentialsPath}'.", ex);
        }

        GitHubAppCredentialFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(json, GitHubAppCredentialsJsonContext.Default.GitHubAppCredentialFileDto);
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
        EnsureReadableFile(privateKeyPath, "private-key");
        EnsureRestrictedPermissions(privateKeyPath, "private-key");
        if (_enforceOutsideWorkingTree)
        {
            EnsureOutsideWorkingTree(privateKeyPath, "private-key");
        }

        string privateKeyPem;
        try
        {
            privateKeyPem = _readAllText(privateKeyPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"octoshift: unable to read private key file '{privateKeyPath}'.", ex);
        }

        if (string.IsNullOrWhiteSpace(privateKeyPem))
        {
            throw new InvalidOperationException($"octoshift: private key file '{privateKeyPath}' was empty.");
        }

        return new GitHubAppCredentials(
            dto.AppId.Value.ToString(CultureInfo.InvariantCulture),
            dto.InstallationId.Value,
            privateKeyPem,
            new GitHubActorIdentity(dto.Actor));
    }

    private static void EnsureReadableFile(string path, string label)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"octoshift: {label} file '{path}' does not exist.");
        }

        try
        {
            using FileStream _ = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException($"octoshift: {label} file '{path}' is not readable.", ex);
        }
    }

    private static UnixFileMode? TryGetUnixFileMode(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        return File.GetUnixFileMode(path);
    }

    private void EnsureRestrictedPermissions(string path, string label)
    {
        UnixFileMode? mode;
        try
        {
            mode = _getUnixFileMode(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            throw new InvalidOperationException($"octoshift: unable to read permissions for {label} file '{path}'.", ex);
        }

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
