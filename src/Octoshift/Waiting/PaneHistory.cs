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

    /// <summary>
    /// Whether this host was collected in the immediately preceding sweep — so observation is continuous
    /// up to it, with no gap in which a window could have released and reclaimed a PR unseen.
    /// </summary>
    /// <remarks>
    /// Defaults to <c>false</c>, which is the fail-closed answer: a host with no continuity recorded (a
    /// new host, or one from an older history file that never wrote this field) is treated as though it
    /// had a gap, so its remembered registrations are invalidated on the next collection rather than
    /// trusted for ordering. Only a host actually collected last run carries <c>true</c>.
    /// </remarks>
    [JsonPropertyName("continuous")]
    public bool Continuous { get; init; }
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

    /// <summary>
    /// Whether this registration was <em>witnessed</em>: recorded while its host was already under
    /// continuous observation and the fleet view was complete, so the time is a real appearance rather
    /// than a first look.
    /// </summary>
    /// <remarks>
    /// Persisted with the registration and preserved for as long as the same claim continues, so trust
    /// cannot be recomputed from a later sweep's coverage. A claim first recorded under a narrow view
    /// stays untrusted across every subsequent sweep — fleet expansion must not promote it — until the
    /// window releases or switches and re-registers under a view that can witness it. Missing in an older
    /// history file, which deserialises to <c>false</c>: an unlabelled registration is treated as
    /// untrusted, never silently promoted.
    /// </remarks>
    [JsonPropertyName("witnessed")]
    public bool Witnessed { get; init; }
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

        // Fail closed on any entry not written by this scheme. A host key must be a valid target id and a
        // pane key a target id composed with a pane id; anything else is an older, differently-keyed file
        // whose `fernie|%3` could be misread as some target this scheme would never mint. Dropping it
        // costs a sweep of continuity and degrades ownership to inferred, never misattributes it.
        //
        // The deserializer can also hand back null values, and records this implementation could never
        // have written — a witnessed claim with no PR, a continuous host never swept. A hostile or
        // corrupt file must not crash Save (which reads .ClaimedPr and `with`-copies these) or, worse,
        // confer an observed order. So null values are dropped and every surviving record is normalised to
        // a shape this scheme could have produced, failing closed: an impossible combination loses its
        // claim and its witness rather than keeping them.
        foreach (string key in _hosts.Keys.Where(k => !TargetId.IsValidKey(k) || _hosts[k] is null).ToArray())
        {
            _hosts.Remove(key);
        }

        foreach (string key in _entries.Keys
            .Where(k => TargetId.HostOfComposite(k) is null || TargetId.IdOfComposite(k) is null || _entries[k] is null)
            .ToArray())
        {
            _entries.Remove(key);
        }

        foreach (string key in _entries.Keys.ToArray())
        {
            _entries[key] = SanitizePane(_entries[key]);
        }

        foreach (string key in _hosts.Keys.ToArray())
        {
            _hosts[key] = SanitizeHost(_hosts[key]);
        }
    }

    /// <summary>
    /// Normalises a pane record to a shape this scheme could have written, failing closed. A claim is
    /// well-formed only when both the PR and the time it was first claimed are present; a witness only
    /// when there is a claim to witness. Any other combination — a witnessed record with no PR, a PR with
    /// no claim time, a time with no PR — is a record this implementation never wrote, so its claim and
    /// witness are cleared rather than trusted, keeping only the body digest and silence, which cannot
    /// confer ownership.
    /// </summary>
    private static PaneMemory SanitizePane(PaneMemory pane)
        => pane.ClaimedPr is not null && pane.ClaimedAt is not null
            ? pane
            : pane with { ClaimedPr = null, ClaimedAt = null, Witnessed = false };

    /// <summary>
    /// Normalises a host record, failing closed. Continuity is a claim that the host was collected in the
    /// immediately preceding sweep, which is meaningless without a sweep time — so a record claiming
    /// continuity with no <see cref="HostMemory.SweptAt"/>, or carrying an epoch that is not the canonical
    /// <c>pid:start_time</c> this scheme writes, is not one this implementation produced and cannot be
    /// trusted to preserve a registration across a gap. Its continuity is dropped so the next collection
    /// invalidates rather than trusts whatever it remembered.
    /// </summary>
    private static HostMemory SanitizeHost(HostMemory host)
        => host.Continuous && host.SweptAt is not null && (host.Epoch is null || TmuxScanner.IsEpoch(host.Epoch))
            ? host
            : host with { Continuous = false };

    /// <summary>
    /// Reconciles a host at the start of a sweep: adopts its current tmux epoch, invalidates whatever the
    /// tool can no longer trust, and reports whether the host was under <em>continuous</em> observation up
    /// to and including the last sweep.
    /// </summary>
    /// <remarks>
    /// Two things break continuity, and each invalidates a different amount. A <em>restarted server</em>
    /// (a different epoch) recycles pane ids, so the remembered ids name different windows: everything for
    /// the host is dropped. A <em>gap</em> (the same server, but the host was not collected last sweep)
    /// leaves the pane ids valid but means a window could have released and reclaimed a PR while unseen —
    /// so the digests and silence are kept, but every registration's claim, time and witness are cleared,
    /// and this sweep records them fresh. Only a host with no restart and no gap is continuous; its
    /// registrations, and their witnessed order, survive. This is what stops an owner from being preserved
    /// across a stretch the tool did not watch.
    /// </remarks>
    public bool AdoptEpoch(string? host, string epoch, DateTimeOffset now)
    {
        string key = TargetId.ForHost(host).Key;
        _hosts.TryGetValue(key, out HostMemory? known);

        bool sameEpoch = known?.Epoch is { Length: > 0 } && known.Epoch == epoch;
        bool seenLastSweep = known?.Continuous ?? false;
        bool continuous = sameEpoch && known!.SweptAt is not null && seenLastSweep;

        if (!sameEpoch)
        {
            // A restart (or a host never seen under an epoch): recycled ids, drop everything.
            foreach (string pane in PaneKeysOn(key))
            {
                _entries.Remove(pane);
            }
        }
        else if (!seenLastSweep)
        {
            // Same server, but a gap since the last collection: keep the body/silence, clear the claim
            // registration and its provenance so this sweep records a fresh one.
            foreach (string pane in PaneKeysOn(key))
            {
                _entries[pane] = _entries[pane] with { ClaimedPr = null, ClaimedAt = null, Witnessed = false };
            }
        }

        _hosts[key] = new HostMemory { Epoch = epoch, SweptAt = now, Continuous = true };
        return continuous;
    }

    private IEnumerable<string> PaneKeysOn(string hostKey)
        => _entries.Keys.Where(k => TargetId.HostOfComposite(k)?.Key == hostKey).ToArray();

    /// <summary>
    /// Records that a host was successfully collected this sweep even though it produced no windows. An
    /// empty successful sweep is still evidence the host was observed, so the host must enter <see
    /// cref="KnownHosts"/> — otherwise a later run that omits it cannot tell the fleet narrowed and reads
    /// its view as complete, the exact gap <see cref="AdoptEpoch"/> leaves because it runs only for hosts
    /// that contributed a pane and an epoch.
    /// </summary>
    /// <remarks>
    /// The epoch is left null on purpose. No windows means no tmux server generation was observed, so
    /// there is nothing for a later nonempty sweep to be continuous <em>from</em>: a window appearing next
    /// run is registered fresh rather than inheriting a place in a queue across a gap the tool did not see,
    /// which is why the first later nonempty epoch is not treated as continuous from this one. Any pane
    /// entries the host used to have are pruned by <see cref="Save"/> — the host is in the collected set,
    /// so they are reported departed there rather than swallowed here.
    /// </remarks>
    public void RecordSweptEmpty(string? host, DateTimeOffset now)
        => _hosts[TargetId.ForHost(host).Key] = new HostMemory { Epoch = null, SweptAt = now, Continuous = true };

    /// <summary>
    /// Hosts this tool has collected before, by target key. A run that does not include one of them is
    /// looking at less of the fleet than it has already seen — which is not something the run can work out
    /// from its own arguments, because a host it was not told about is indistinguishable from a host that
    /// does not exist.
    /// </summary>
    public IReadOnlyCollection<string> KnownHosts => _hosts.Keys;

    /// <summary>When this host was last collected in full under the current server, if it was.</summary>
    public DateTimeOffset? SweptAt(string? host)
        => _hosts.TryGetValue(TargetId.ForHost(host).Key, out HostMemory? known) ? known.SweptAt : null;

    /// <summary>
    /// Records the current digest and returns how long the body has been unchanged, or null the first
    /// time a window is seen. Also carries the current claim registration: which PR the window claims now
    /// (or null when it claims none), and whether that registration is witnessed.
    /// </summary>
    /// <remarks>
    /// Called for <em>every</em> collected pane each sweep, claiming or not. A pane that now publishes no
    /// usable identity — absent, malformed, or an issue rather than a PR — is observed with
    /// <paramref name="claimedPr"/> null, which clears its stale registration and provenance while keeping
    /// its digest and silence. Without that, a window that owned a PR, went quiet, and later reclaimed it
    /// would inherit its old registration time and jump the queue ahead of a rival that claimed it in the
    /// meantime.
    /// </remarks>
    public TimeSpan? Observe(TmuxPane pane, DateTimeOffset now, int? claimedPr = null, bool registrationWitnessed = false)
    {
        string key = Key(pane);
        _entries.TryGetValue(key, out PaneMemory? previous);

        // A window keeps its registration — time AND provenance — for as long as it keeps claiming the
        // same PR. Switching PRs, or dropping the claim entirely, is a fresh registration and goes to the
        // back of the queue. Provenance is persisted rather than recomputed: a registration first recorded
        // without prior continuous observation stays untrusted across later sweeps, so fleet expansion
        // cannot promote it; only a genuinely witnessed re-registration earns trust.
        bool sameClaim = claimedPr is not null && previous?.ClaimedPr == claimedPr;
        DateTimeOffset? claimedAt = claimedPr is null ? null
            : sameClaim && previous?.ClaimedAt is { } held ? held
            : now;
        bool witnessed = claimedPr is not null && (sameClaim ? previous?.Witnessed ?? false : registrationWitnessed);

        if (previous is not null && previous.Digest == pane.BodyDigest)
        {
            _entries[key] = previous with { ClaimedPr = claimedPr, ClaimedAt = claimedAt, Witnessed = witnessed };
            return now - previous.Since;
        }

        _entries[key] = new PaneMemory
        {
            Digest = pane.BodyDigest,
            Since = now,
            ClaimedPr = claimedPr,
            ClaimedAt = claimedAt,
            Witnessed = witnessed,
        };

        return previous is null ? null : TimeSpan.Zero;
    }

    /// <summary>When this window first claimed the PR it now claims, or null if it is not registered.</summary>
    public DateTimeOffset? ClaimedAt(TmuxPane pane)
        => _entries.TryGetValue(Key(pane), out PaneMemory? entry) ? entry.ClaimedAt : null;

    /// <summary>
    /// Whether this window's current claim registration was witnessed — recorded while the tool was
    /// already watching its host under a complete view. Consulted by <see cref="Claim.Register"/> so that
    /// trust is read from the persisted registration, not recomputed from the current sweep's coverage.
    /// </summary>
    public bool IsWitnessed(TmuxPane pane)
        => _entries.TryGetValue(Key(pane), out PaneMemory? entry) && entry.Witnessed;

    private static string Key(TmuxPane pane) => TargetId.ForHost(pane.Host).ComposeWith(pane.PaneId);

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
    /// Hosts collected this sweep, by raw alias (null local). A window on a host that did not answer has
    /// not departed; it is merely unseen, and forgetting it would manufacture a departure on every
    /// unreachable sweep. A previously-known host absent from this set has a gap recorded against it, so
    /// its registrations are invalidated when it is next collected.
    /// </param>
    public IReadOnlyList<string> Save(IEnumerable<TmuxPane> live, IEnumerable<string?>? hosts = null)
    {
        var seen = live.ToArray();
        var keep = seen.Select(Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string>? collected = hosts is null
            ? null
            : hosts.Select(h => TargetId.ForHost(h).Key).ToHashSet(StringComparer.Ordinal);

        var departed = new List<string>();
        foreach (string gone in _entries.Keys.Where(k => !keep.Contains(k)).ToArray())
        {
            TargetId? host = TargetId.HostOfComposite(gone);
            if (host is null || (collected is not null && !collected.Contains(host.Value.Key)))
            {
                continue;
            }

            if (_entries[gone].ClaimedPr is { } pr)
            {
                string paneId = TargetId.IdOfComposite(gone) ?? gone;
                departed.Add($"{host.Value.Display} {paneId} (was on #{pr})");
            }

            _entries.Remove(gone);
        }

        // Record a gap against every previously known host not collected this sweep — an omitted host or
        // an unreachable one. Its windows are unseen, not departed (skipped above), but the tool has lost
        // continuity, so the next collection under the same epoch invalidates their registrations.
        if (collected is not null)
        {
            foreach (string key in _hosts.Keys.Where(k => !collected.Contains(k)).ToArray())
            {
                _hosts[key] = _hosts[key] with { Continuous = false };
            }
        }

        // The history is load-bearing: it is where a witnessed order lives between runs, so a sweep whose
        // memory does not reach disk has not narrowed the hosts it failed to see, and a later run could
        // read a stale witnessed ownership as current. A write failure is therefore a real failure that
        // the command surfaces as unavailable, not something to swallow. The write is atomic — a fresh
        // temp file in the same directory, then a rename over the target — so a failure mid-write leaves
        // the previous valid history intact rather than a truncated one. Only the specific I/O exceptions
        // are caught, so a cancellation is never laundered into a persistence error.
        string dir = Path.GetDirectoryName(_path)!;
        string tmp = _path + "." + Environment.ProcessId + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(
                new HistoryFile { Panes = _entries, Hosts = _hosts },
                PaneHistoryJsonContext.Default.HistoryFile));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new HistoryPersistException(_path, ex);
        }

        return departed;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// The pane history could not be written. Persistence is load-bearing — stale witnessed ownership left on
/// disk by a sweep whose memory did not land would be read as current next run — so this surfaces as the
/// unavailable contract rather than being swallowed into a success-shaped report.
/// </summary>
internal sealed class HistoryPersistException(string path, Exception inner)
    : Exception($"could not persist pane history to {path}: {inner.Message}", inner);

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
