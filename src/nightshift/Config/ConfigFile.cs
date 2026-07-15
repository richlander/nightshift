namespace Nightshift.Config;

using System.Text.Json;

/// <summary>
/// Loads <see cref="NightshiftConfig"/> from disk. The path is <c>NIGHTSHIFT_CONFIG</c> when set, else
/// <c>~/.nightshift/config</c>. Loading is deliberately tolerant: a missing, empty, or malformed file
/// yields <c>null</c> rather than throwing, so a broken config never wedges the CLI — resolution simply
/// falls through to the next source.
/// </summary>
internal static class ConfigFile
{
    /// <summary>The resolved config file path (env override, else the default under the user profile).</summary>
    public static string Path =>
        Environment.GetEnvironmentVariable("NIGHTSHIFT_CONFIG") is { Length: > 0 } configPath
            ? configPath
            : System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightshift", "config");

    /// <summary>Reads and parses the config, or returns <c>null</c> when absent, empty, or unreadable.</summary>
    public static NightshiftConfig? Load() => Load(Path);

    /// <summary>Reads and parses the config at <paramref name="path"/>, tolerating absence and corruption.</summary>
    public static NightshiftConfig? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            return JsonSerializer.Deserialize(json, ConfigJson.Default.NightshiftConfig);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            // A partial or corrupt config must never wedge the CLI — treat it as absent.
            return null;
        }
    }
}
