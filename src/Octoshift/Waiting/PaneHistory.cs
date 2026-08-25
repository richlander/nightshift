namespace Octoshift.Waiting;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>What a window's body looked like last time, and when it last differed.</summary>
internal sealed record PaneMemory
{
    [JsonPropertyName("digest")]
    public string? Digest { get; init; }

    [JsonPropertyName("since")]
    public DateTimeOffset Since { get; init; }

    /// <summary>The PR this window was claiming when first seen to claim it.</summary>
    [JsonPropertyName("pr")]
    public int? ClaimedPr { get; init; }

    /// <summary>
    /// When this window first claimed that PR. This is the registration order two claims are ranked by,
    /// so it has to be remembered rather than derived: anything computed fresh each sweep — window index,
    /// last activity, position in the collected list — can reorder between runs, and an ownership that
    /// flips is worse than none.
    /// </summary>
    [JsonPropertyName("claimedAt")]
    public DateTimeOffset? ClaimedAt { get; init; }
}

/// <summary>
/// Remembers each window's body digest between runs, so silence can be measured rather than guessed.
/// </summary>
/// <remarks>
/// A single sweep can only say whether a window is drawing a spinner right now. Whether it has produced
/// anything takes two observations, and the interval between them is however long it has been since the
/// tool last ran — which is why this accumulates rather than sampling twice inside one run. A window
/// with no history yet reports nothing rather than a fabricated zero.
/// </remarks>
internal sealed class PaneHistory
{
    private readonly string _path;
    private readonly Dictionary<string, PaneMemory> _entries;

    public PaneHistory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "octoshift",
            "panes.json");

        try
        {
            _entries = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), PaneHistoryJsonContext.Default.DictionaryStringPaneMemory) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Losing the history costs one sweep of silence measurements, never correctness.
            _entries = [];
        }
    }

    /// <summary>
    /// Records the current digest and returns how long the body has been unchanged, or null the first
    /// time a window is seen.
    /// </summary>
    public TimeSpan? Observe(TmuxPane pane, DateTimeOffset now, int? claimedPr = null)
    {
        string key = Key(pane);
        _entries.TryGetValue(key, out PaneMemory? previous);

        // A window keeps its registration for as long as it keeps claiming the same PR. Switching PRs is
        // a fresh registration, and goes to the back of the queue.
        bool sameClaim = previous?.ClaimedPr == claimedPr;
        DateTimeOffset? claimedAt = claimedPr is null ? null
            : sameClaim && previous?.ClaimedAt is { } held ? held
            : now;

        if (previous is not null && previous.Digest == pane.BodyDigest)
        {
            _entries[key] = previous with { ClaimedPr = claimedPr, ClaimedAt = claimedAt };
            return now - previous.Since;
        }

        _entries[key] = new PaneMemory
        {
            Digest = pane.BodyDigest,
            Since = now,
            ClaimedPr = claimedPr,
            ClaimedAt = claimedAt,
        };

        return previous is null ? null : TimeSpan.Zero;
    }

    /// <summary>When this window first claimed the PR it now claims, or null if it is not registered.</summary>
    public DateTimeOffset? ClaimedAt(TmuxPane pane)
        => _entries.TryGetValue(Key(pane), out PaneMemory? entry) ? entry.ClaimedAt : null;

    private static string Key(TmuxPane pane) => $"{pane.Host ?? "local"}|{pane.PaneId}";

    /// <summary>Drops windows that no longer exist, so a long-lived file does not grow without bound.</summary>
    public void Save(IEnumerable<TmuxPane> live)
    {
        var keep = live.Select(p => $"{p.Host ?? "local"}|{p.PaneId}").ToHashSet(StringComparer.Ordinal);
        foreach (string gone in _entries.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            _entries.Remove(gone);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, PaneHistoryJsonContext.Default.DictionaryStringPaneMemory));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(Dictionary<string, PaneMemory>))]
internal partial class PaneHistoryJsonContext : JsonSerializerContext
{
}
