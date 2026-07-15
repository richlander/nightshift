namespace Nightshift.Config;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The on-disk Nightshift config — a kubeconfig-analog for pointing the CLI at coordination state. Only
/// <see cref="Socket"/> is honoured today, but the shape is intentionally growable (contexts/sections can
/// be added later without breaking readers). Members are declared optional so a partial file is valid.
/// </summary>
internal sealed class NightshiftConfig
{
    /// <summary>Path to the Turnstile Unix socket, if the file pins one.</summary>
    public string? Socket { get; init; }
}

/// <summary>Source-generated JSON for AOT; camelCase on disk, tolerant of comments and trailing commas.</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(NightshiftConfig))]
internal partial class ConfigJson : JsonSerializerContext;
