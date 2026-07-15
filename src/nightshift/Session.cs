namespace Nightshift;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The persisted state the CLIENT owns on the agent's behalf: the lease keeping the agent alive and the
/// fence for its active claim. The agent never sees the lease — it lives here, keyed by worktree, at 0600.
/// One agent per worktree.
/// </summary>
internal sealed record SessionState(string LeaseId, long Fence, string ClaimKey, string OrderBase, string ReadyKey);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal partial class SessionJson : JsonSerializerContext;

internal static class Session
{
    /// <summary>Stable identity of this agent = a hash of its worktree root (one agent per worktree).</summary>
    public static string Identity { get; } = ComputeIdentity();

    private static string FilePath => Path.Combine(Paths.RuntimeDir, $"session-{Identity}.json");

    public static SessionState? Load()
    {
        if (!File.Exists(FilePath))
        {
            return null;
        }

        return JsonSerializer.Deserialize(File.ReadAllText(FilePath), SessionJson.Default.SessionState);
    }

    public static void Save(SessionState state)
    {
        string json = JsonSerializer.Serialize(state, SessionJson.Default.SessionState);
        // Create at 0600 before writing so the lease id is never briefly world-readable.
        RuntimeFile.WriteRestricted(FilePath, json);
    }

    public static void Clear()
    {
        if (File.Exists(FilePath))
        {
            File.Delete(FilePath);
        }
    }

    private static string ComputeIdentity()
    {
        string root = Git.WorktreeRoot();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(root));
        return Convert.ToHexStringLower(hash)[..16];
    }
}
