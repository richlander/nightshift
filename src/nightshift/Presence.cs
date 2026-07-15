namespace Nightshift;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The client-held record of an agent's shift presence: the lease that keeps its roster entry alive.
/// Distinct from a claim lease (which is per-order) — presence spans the whole shift, across many orders,
/// so a coordinator can see who is on duty and reclaim the roster automatically when an agent goes away.
/// </summary>
internal sealed record PresenceState(string LeaseId);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(PresenceState))]
internal partial class PresenceJson : JsonSerializerContext;

/// <summary>Persists the shift-presence lease for this agent (one agent per worktree), at 0600.</summary>
internal static class Presence
{
    /// <summary>The roster key this agent owns: <c>/agent/{identity}</c>, holding "active" or "standby".</summary>
    public static string Key => $"/agent/{Session.Identity}";

    private static string FilePath => Path.Combine(Paths.RuntimeDir, $"presence-{Session.Identity}.json");

    public static PresenceState? Load()
        => File.Exists(FilePath)
            ? JsonSerializer.Deserialize(File.ReadAllText(FilePath), PresenceJson.Default.PresenceState)
            : null;

    public static void Save(PresenceState state)
        => RuntimeFile.WriteRestricted(FilePath, JsonSerializer.Serialize(state, PresenceJson.Default.PresenceState));

    public static void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }
}
