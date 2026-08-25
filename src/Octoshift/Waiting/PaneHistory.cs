namespace Octoshift.Waiting;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>What is known about one host between runs.</summary>
internal sealed record HostMemory
{
    /// <summary>The tmux server this host's pane ids belong to.</summary>
    [JsonPropertyName("epoch")]
    public string? Epoch { get; init; }

    /// <summary>
    /// When this host was last collected in full. A window with no record on a host that has been swept
    /// before must have appeared since that sweep, which is what lets an unseen claim be ordered after a
    /// seen one without guessing.
    /// </summary>
    [JsonPropertyName("sweptAt")]
    public DateTimeOffset? SweptAt { get; init; }
}

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
    private readonly Dictionary<string, HostMemory> _hosts;

    public PaneHistory(string? path = null)
    {
        _path = path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cache",
            "octoshift",
            "panes.json");

        try
        {
            HistoryFile? file = File.Exists(_path)
                ? JsonSerializer.Deserialize(File.ReadAllText(_path), PaneHistoryJsonContext.Default.HistoryFile)
                : null;

            _entries = file?.Panes ?? [];
            _hosts = file?.Hosts ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Losing the history costs one sweep of silence measurements, never correctness.
            _entries = [];
            _hosts = [];
        }
    }

    /// <summary>
    /// Discards everything remembered about a host whose tmux server has restarted, and reports whether
    /// the host was swept under this same server before.
    /// </summary>
    /// <remarks>
    /// Pane ids restart at <c>%0</c> with the server, so after a reboot the remembered ids name different
    /// windows. Keeping them would not merely be unhelpful — it would hand one window another's
    /// registration time and present the result as observed fact. Dropping them costs a sweep of
    /// measurements and degrades ownership to inferred, which is the honest state to be in.
    /// </remarks>
    public bool AdoptEpoch(string? host, string epoch, DateTimeOffset now)
    {
        string key = host ?? "local";
        _hosts.TryGetValue(key, out HostMemory? known);

        bool continuous = known?.Epoch is { Length: > 0 } && known.Epoch == epoch && known.SweptAt is not null;
        if (!continuous && known?.Epoch != epoch)
        {
            foreach (string pane in _entries.Keys.Where(k => k.StartsWith(key + "|", StringComparison.Ordinal)).ToArray())
            {
                _entries.Remove(pane);
            }
        }

        _hosts[key] = new HostMemory { Epoch = epoch, SweptAt = now };
        return continuous;
    }

    /// <summary>
    /// Hosts this tool has collected before. A run that does not include one of them is looking at less
    /// of the fleet than it has already seen — which is not something the run can work out from its own
    /// arguments, because a host it was not told about is indistinguishable from a host that does not
    /// exist.
    /// </summary>
    public IReadOnlyCollection<string> KnownHosts => _hosts.Keys;

    /// <summary>When this host was last collected in full under the current server, if it was.</summary>
    public DateTimeOffset? SweptAt(string? host)
        => _hosts.TryGetValue(host ?? "local", out HostMemory? known) ? known.SweptAt : null;

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

    /// <summary>
    /// Drops windows that no longer exist and reports which they were.
    /// </summary>
    /// <remarks>
    /// A window vanishing is an event, not housekeeping. It may be an agent finished and reclaimed, or
    /// one that crashed, or a session someone killed by hand — and the difference matters to whoever is
    /// watching. Pruning it silently, as this did, turns every one of those into the same nothing.
    /// </remarks>
    /// <param name="live">Windows collected this sweep.</param>
    /// <param name="hosts">
    /// Hosts collected this sweep. A window on a host that did not answer has not departed; it is merely
    /// unseen, and forgetting it would manufacture a departure on every unreachable sweep.
    /// </param>
    public IReadOnlyList<string> Save(IEnumerable<TmuxPane> live, IEnumerable<string?>? hosts = null)
    {
        var seen = live.ToArray();
        var keep = seen.Select(p => $"{p.Host ?? "local"}|{p.PaneId}").ToHashSet(StringComparer.Ordinal);
        HashSet<string>? collected = hosts is null
            ? null
            : hosts.Select(h => h ?? "local").ToHashSet(StringComparer.Ordinal);

        var departed = new List<string>();
        foreach (string gone in _entries.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            string host = gone[..gone.IndexOf('|', StringComparison.Ordinal)];
            if (collected is not null && !collected.Contains(host))
            {
                continue;
            }

            if (_entries[gone].ClaimedPr is { } pr)
            {
                departed.Add($"{gone.Replace("|", " ", StringComparison.Ordinal)} (was on #{pr})");
            }

            _entries.Remove(gone);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(
                new HistoryFile { Panes = _entries, Hosts = _hosts },
                PaneHistoryJsonContext.Default.HistoryFile));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return departed;
    }
}

/// <summary>The on-disk shape: what is known per window, and per host.</summary>
internal sealed record HistoryFile
{
    [JsonPropertyName("panes")]
    public Dictionary<string, PaneMemory>? Panes { get; init; }

    [JsonPropertyName("hosts")]
    public Dictionary<string, HostMemory>? Hosts { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HistoryFile))]
internal partial class PaneHistoryJsonContext : JsonSerializerContext
{
}
