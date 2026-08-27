namespace Octoshift.Waiting;

using System.Diagnostics;
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
internal sealed class PaneHistory : IDisposable
{
    private readonly string _path;
    private readonly Dictionary<string, PaneMemory> _entries;
    private readonly Dictionary<string, HostMemory> _hosts;

    /// <summary>
    /// Every host this tool has ever <em>attempted</em> to collect — targeted over ssh (or the local
    /// machine), whether or not it answered. This is the persistent fleet membership, kept apart from the
    /// hosts that actually answered (<see cref="_hosts"/>, which carries epoch, continuity and sweep time).
    /// A target attempted for the very first time and failing before it ever collected leaves nothing in
    /// <see cref="_hosts"/>, so without this set it would be forgotten and a later sweep that omits it would
    /// read as complete — granting a sole claim while a rival may still run on the unreached host. It grows
    /// monotonically and is used only to decide whether a later view is narrower than the fleet already
    /// known; continuity, epochs, panes and witnesses are never keyed on it.
    /// </summary>
    private readonly HashSet<string> _attempted;

    private FileStream? _lock;

    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LockRetry = TimeSpan.FromMilliseconds(50);

    /// <summary>Where the history lives when no path is given: one file per user, shared by every
    /// octoshift process on the machine, which is why the transaction lock exists.</summary>
    internal static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache",
        "octoshift",
        "panes.json");

    /// <summary>
    /// Loads the history <em>without</em> the cross-process transaction lock. For tests only: product code
    /// must use <see cref="OpenAsync"/>, so a concurrent <c>waiting</c> and <c>pr</c> cannot interleave one
    /// process's load-reconcile-save with another's and lose an update. Kept as the public constructor so
    /// the unit tests that new one up directly do not each have to acquire a real lock; it loads
    /// forgivingly (a malformed or unreadable existing file becomes empty) because those tests seed
    /// partial and corrupt files on purpose. Every product call site goes through <see cref="OpenAsync"/>,
    /// which loads strictly.
    /// </summary>
    public PaneHistory(string? path = null)
        : this(path ?? DefaultPath, null, strictLoad: false)
    {
    }

    private PaneHistory(string path, FileStream? lockStream, bool strictLoad)
    {
        _path = path;
        _lock = lockStream;
        (_entries, _hosts, _attempted) = Load(path, strictLoad);

        // A strict (product) load has already reconciled its structure in Load — a missing map is a
        // rejection there. Here it rejects the whole file, bytes untouched, the moment any single record
        // is one this scheme could not have written: an invalid or null key, a null value, or a
        // semantically impossible record. Sanitising instead — dropping the bad entry and keeping the
        // rest — is exactly the laundering the load-bearing history must not do: a corrupted entry for a
        // known host would be silently forgotten, so a narrowed sweep reads its view as complete and then
        // overwrites the evidence. Only the forgiving loader below drops key by key, and it is test-only.
        if (strictLoad)
        {
            ValidateStrict(_entries, _hosts, _attempted);
            return;
        }

        // Forgiving (test-only): fail closed on any entry not written by this scheme, but by dropping it
        // rather than rejecting the file, so the sanitisation unit tests can seed a corrupt file and
        // observe that the survivors are normalised. A host key must be a valid target id and a pane key a
        // target id composed with a canonical pane id — validated with the exact IsPaneId the ids were
        // written under, not merely a non-empty suffix, so an impossible id like `%01` cannot slip
        // through. The deserializer can also hand back null values and records this implementation could
        // never have written — a witnessed claim with no PR, a continuous host never swept — which are
        // dropped or normalised to a shape this scheme could have produced.
        foreach (string key in _hosts.Keys.Where(k => !TargetId.IsValidKey(k) || _hosts[k] is null).ToArray())
        {
            _hosts.Remove(key);
        }

        foreach (string key in _entries.Keys
            .Where(k => TargetId.HostOfComposite(k) is null
                     || TargetId.IdOfComposite(k) is not { } id
                     || !TmuxScanner.IsPaneId(id)
                     || _entries[k] is null)
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

        // Attempted membership: drop any key not one this scheme minted, and fold in every collected host
        // so the persisted invariant (a host that answered was, by definition, attempted) holds even for a
        // hand-seeded fixture.
        foreach (string key in _attempted.Where(k => !TargetId.IsValidKey(k)).ToArray())
        {
            _attempted.Remove(key);
        }

        _attempted.UnionWith(_hosts.Keys);
    }

    /// <summary>
    /// Rejects the entire history — bytes untouched — if any record is one this scheme could not have
    /// written. Unlike the forgiving sanitiser, which drops a bad entry and keeps the rest, a strict load
    /// treats a single corrupt or impossible record as evidence the whole file is not trustworthy:
    /// dropping one host's entry would forget that host, letting a run that collects a narrower fleet read
    /// its view as complete and own a sole claim, then overwrite the evidence with an empty-derived
    /// snapshot. So it throws, the command reports the unavailable contract, and the file is left as it
    /// was for a human to inspect. The invariants below are exactly the shapes <see cref="AdoptEpoch"/>,
    /// <see cref="RecordSweptEmpty"/>, <see cref="Observe"/> and <see cref="Save"/> can produce.
    /// </summary>
    private static void ValidateStrict(
        Dictionary<string, PaneMemory> entries,
        Dictionary<string, HostMemory> hosts,
        HashSet<string> attempted)
    {
        foreach ((string key, HostMemory host) in hosts)
        {
            if (!TargetId.IsValidKey(key))
            {
                throw new HistoryUnavailableException($"pane history has an invalid host key '{key}', so it was not written by this scheme");
            }

            if (host is null)
            {
                throw new HistoryUnavailableException($"pane history has a null record for host '{key}'");
            }

            if (!IsWriterProducedHost(host))
            {
                throw new HistoryUnavailableException($"pane history has an impossible record for host '{key}'");
            }

            // A host that answered was, by definition, attempted: the writer records both on the same
            // sweep. A collected host missing from the attempted set is a shape this scheme never wrote,
            // and reading past it would let the attempted-membership invariant it relies on go unchecked.
            if (!attempted.Contains(key))
            {
                throw new HistoryUnavailableException($"pane history has collected host '{key}' absent from the attempted set");
            }
        }

        // Attempted membership is a set of canonical target keys. An invalid one is not a target this
        // scheme minted, so the whole file is untrustworthy rather than one key to drop.
        foreach (string key in attempted)
        {
            if (!TargetId.IsValidKey(key))
            {
                throw new HistoryUnavailableException($"pane history has an invalid attempted host key '{key}', so it was not written by this scheme");
            }
        }

        foreach ((string key, PaneMemory pane) in entries)
        {
            if (TargetId.HostOfComposite(key) is not { } host || TargetId.IdOfComposite(key) is not { } id || !TmuxScanner.IsPaneId(id))
            {
                throw new HistoryUnavailableException($"pane history has an invalid pane key '{key}', so it was not written by this scheme");
            }

            if (pane is null)
            {
                throw new HistoryUnavailableException($"pane history has a null record for pane '{key}'");
            }

            if (!IsWriterProducedPane(pane))
            {
                throw new HistoryUnavailableException($"pane history has an impossible record for pane '{key}'");
            }

            // Every pane belongs to a host, and the writer records that host in the hosts map on the same
            // sweep. A pane whose host key is absent is impossible — and dangerous: it carries a
            // registration for a host that never enters KnownHosts, so a narrowed sweep could not tell the
            // fleet was wider than it saw. Reject rather than let a pane smuggle in an invisible host.
            if (!hosts.ContainsKey(host.Key))
            {
                throw new HistoryUnavailableException($"pane history has pane '{key}' on host '{host.Key}', which is not in the hosts map");
            }
        }
    }

    /// <summary>
    /// The host shapes the writer can produce: <see cref="AdoptEpoch"/> and <see cref="RecordSweptEmpty"/>
    /// always stamp a <see cref="HostMemory.SweptAt"/>, and an epoch is either absent (an empty/no-server
    /// sweep) or the canonical <c>pid:start_time</c>. Both <see cref="HostMemory.Continuous"/> values are
    /// legitimate — <see cref="Save"/> flips it to false for a host it did not collect — so continuity is
    /// not constrained here. A record missing its sweep time, or carrying a non-canonical epoch, is one
    /// this implementation never wrote.
    /// </summary>
    private static bool IsWriterProducedHost(HostMemory host)
        => host.SweptAt is not null && (host.Epoch is null || TmuxScanner.IsEpoch(host.Epoch));

    /// <summary>
    /// The pane shapes <see cref="Observe"/> can produce: a real <see cref="PaneMemory.Since"/>; a claim
    /// that is present in both its number and its time or absent in both; a positive PR and a real
    /// registration time when it claims; and a witness only when it claims. A witnessed record with no
    /// claim, a claim with only half its fields, a non-positive PR, or a default timestamp is impossible
    /// and confers an ownership this implementation would never have recorded.
    /// </summary>
    private static bool IsWriterProducedPane(PaneMemory pane)
    {
        if (pane.Since == default)
        {
            return false;
        }

        bool claims = pane.ClaimedPr is not null;
        if (claims != (pane.ClaimedAt is not null))
        {
            return false;
        }

        if (claims && (pane.ClaimedPr <= 0 || pane.ClaimedAt == default))
        {
            return false;
        }

        return !pane.Witnessed || claims;
    }

    /// <summary>
    /// Reads the two dictionaries and the attempted-host set off disk. Absence of the file is a first
    /// run — a genuinely empty history. An existing file that cannot be read or parsed, or that is a null
    /// JSON document, is NOT empty: it is a history whose contents are unknown, and treating it as empty
    /// would forget the known hosts and witnessed orders it held — letting a narrowed sweep read as
    /// complete and then overwrite the evidence. So under a strict (product) load that case throws and the
    /// transaction is unavailable; the forgiving loader used by unit tests tolerates it, since those seed
    /// corrupt files deliberately. A well-formed file with entries this scheme never wrote is not a load
    /// failure — it parses — and is left to the sanitiser above to drop key by key.
    /// </summary>
    private static (Dictionary<string, PaneMemory>, Dictionary<string, HostMemory>, HashSet<string>) Load(string path, bool strict)
    {
        if (!File.Exists(path))
        {
            return ([], [], []);
        }

        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (strict)
            {
                throw new HistoryUnavailableException($"could not read pane history from {path}: {ex.Message}", ex);
            }

            return ([], [], []);
        }

        // A strict (product) load validates the raw JSON shape before deserializing it, because the
        // source-generated deserializer is deliberately lenient in ways this file must not be: it matches
        // property names case-insensitively and silently ignores unknown members, so a hand-tampered
        // `{"panes":{},"hosts":{},"version":2}` or an uppercase `Panes` would be read, its unexpected parts
        // dropped, and the result rewritten — laundering exactly the corruption the strict load exists to
        // refuse. Deserialization then enforces value kinds (a bool where a number is written throws), and
        // the semantic validator enforces the record invariants. The forgiving loader skips this and
        // tolerates whatever parses.
        if (strict)
        {
            ValidateRawSchema(text, path);
        }

        HistoryFile? file;
        try
        {
            file = JsonSerializer.Deserialize(text, PaneHistoryJsonContext.Default.HistoryFile);
        }
        catch (JsonException ex)
        {
            if (strict)
            {
                throw new HistoryUnavailableException($"could not read pane history from {path}: {ex.Message}", ex);
            }

            return ([], [], []);
        }

        if (file is null)
        {
            if (strict)
            {
                throw new HistoryUnavailableException($"pane history at {path} is a null document, not an empty history");
            }

            return ([], [], []);
        }

        // The writer always emits all three members (an empty first run writes
        // `{"panes":{},"hosts":{},"attempted":[]}`), so a file missing any — `{}`, `{"panes":{}}`,
        // `{"hosts":null}`, a file with no attempted array — was not written by this scheme. Under a strict
        // load that is a rejection, not an empty history: reading it as empty would forget whatever the
        // real file held. The forgiving loader treats an absent member as empty. (Strict never reaches here
        // with a missing member — ValidateRawSchema already rejected it — but the guard stays as defence in
        // depth.)
        if (strict && (file.Panes is null || file.Hosts is null || file.Attempted is null))
        {
            throw new HistoryUnavailableException($"pane history at {path} is missing its panes, hosts or attempted member, so it was not written by this scheme");
        }

        return (file.Panes ?? [], file.Hosts ?? [], [.. file.Attempted ?? []]);
    }

    private static readonly string[] RootMembers = ["panes", "hosts", "attempted"];
    private static readonly string[] HostMembers = ["epoch", "sweptAt", "continuous"];
    private static readonly string[] PaneMembers = ["digest", "since", "pr", "claimedAt", "witnessed"];

    /// <summary>
    /// Validates the raw JSON shape of a strict load with <see cref="JsonDocument"/> — AOT-safe, no
    /// reflection — before the lenient source-generated deserializer sees it. The root must be an object
    /// with exactly a <c>panes</c> object, a <c>hosts</c> object and an <c>attempted</c> array, exact
    /// casing, no unknown or duplicate members; each host record exactly
    /// <c>epoch</c>/<c>sweptAt</c>/<c>continuous</c> and each pane record exactly
    /// <c>digest</c>/<c>since</c>/<c>pr</c>/<c>claimedAt</c>/<c>witnessed</c>, again with no unknown or
    /// duplicate members; no dictionary key may repeat; and the attempted array must be strings only, none
    /// repeated. Anything else is not a file this scheme wrote, so it is rejected here — bytes untouched —
    /// rather than deserialized into a rewritten approximation of itself.
    /// </summary>
    private static void ValidateRawSchema(string json, string path)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new HistoryUnavailableException($"could not read pane history from {path}: {ex.Message}", ex);
        }

        using (doc)
        {
            RequireExactMembers(doc.RootElement, RootMembers, path, "the root");
            ValidateDictionary(doc.RootElement.GetProperty("hosts"), HostMembers, path, "host");
            ValidateDictionary(doc.RootElement.GetProperty("panes"), PaneMembers, path, "pane");
            ValidateAttemptedArray(doc.RootElement.GetProperty("attempted"), path);
        }
    }

    /// <summary>The attempted-host set on disk: a JSON array of unique strings. The writer emits target
    /// keys; a non-array, a non-string element, or a repeated key is a shape it never produced, so the
    /// whole file is rejected rather than one element dropped.</summary>
    private static void ValidateAttemptedArray(JsonElement attempted, string path)
    {
        if (attempted.ValueKind != JsonValueKind.Array)
        {
            throw new HistoryUnavailableException($"pane history at {path} has an attempted member that is not an array");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement element in attempted.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                throw new HistoryUnavailableException($"pane history at {path} has a non-string attempted host key");
            }

            if (!seen.Add(element.GetString()!))
            {
                throw new HistoryUnavailableException($"pane history at {path} has a duplicate attempted host key '{element.GetString()}'");
            }
        }
    }

    /// <summary>A dictionary object whose keys must be unique and whose every value is a record with
    /// exactly the given members.</summary>
    private static void ValidateDictionary(JsonElement dictionary, string[] recordMembers, string path, string kind)
    {
        if (dictionary.ValueKind != JsonValueKind.Object)
        {
            throw new HistoryUnavailableException($"pane history at {path} has a {kind} map that is not an object");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty entry in dictionary.EnumerateObject())
        {
            if (!seen.Add(entry.Name))
            {
                throw new HistoryUnavailableException($"pane history at {path} has a duplicate {kind} key '{entry.Name}'");
            }

            RequireExactMembers(entry.Value, recordMembers, path, $"{kind} '{entry.Name}'");
        }
    }

    /// <summary>An object with exactly the allowed members — no unknown member, no duplicate, none
    /// missing.</summary>
    private static void RequireExactMembers(JsonElement element, string[] allowed, string path, string what)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new HistoryUnavailableException($"pane history at {path}: {what} is not an object");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty member in element.EnumerateObject())
        {
            if (!seen.Add(member.Name))
            {
                throw new HistoryUnavailableException($"pane history at {path}: {what} has a duplicate '{member.Name}' member");
            }

            if (Array.IndexOf(allowed, member.Name) < 0)
            {
                throw new HistoryUnavailableException($"pane history at {path}: {what} has an unexpected '{member.Name}' member");
            }
        }

        foreach (string name in allowed)
        {
            if (!seen.Contains(name))
            {
                throw new HistoryUnavailableException($"pane history at {path}: {what} is missing its '{name}' member");
            }
        }
    }

    /// <summary>
    /// Opens the history for a serialized transaction: acquires a cross-process lock — a sidecar file no
    /// other process can share — and only then loads, strictly. The lock is released by <see cref="Save"/>
    /// (the commit point) or by <see cref="Dispose"/> (any earlier exit). A lock that cannot be taken
    /// within the timeout, a lock I/O failure, or an existing history file that cannot be read or parsed
    /// all surface as <see cref="HistoryUnavailableException"/> — the unavailable contract — never as a
    /// silent success that would forget known hosts and overwrite the file; a genuine caller cancellation
    /// escapes carrying the caller's own token.
    /// </summary>
    public static async Task<PaneHistory> OpenAsync(string? path, CancellationToken ct)
    {
        string resolved = path ?? DefaultPath;
        FileStream lockStream = await AcquireLockAsync(resolved, ct);
        try
        {
            return new PaneHistory(resolved, lockStream, strictLoad: true);
        }
        catch
        {
            lockStream.Dispose();
            throw;
        }
    }

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken ct)
    {
        string lockPath = path + ".lock";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new HistoryUnavailableException($"could not lock pane history at {path}: {ex.Message}", ex);
        }

        // A monotonic clock for the retry budget. Wall-clock arithmetic (DateTimeOffset.UtcNow + timeout)
        // can be stretched or shortened by an NTP correction or a manual clock change mid-wait, so the
        // nominal 30-second window would not be honest — a backward step could extend it well past 30s.
        // Stopwatch measures elapsed time from a monotonic source a clock step cannot move.
        long startTimestamp = Stopwatch.GetTimestamp();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // FileShare.None: while one process holds this handle no other can open it, which is what
                // serializes the transaction across processes. OpenOrCreate so the first writer makes it.
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException) when (Stopwatch.GetElapsedTime(startTimestamp) < LockTimeout)
            {
                // Held by another octoshift process (a sharing violation), or a transient I/O hiccup: wait
                // and retry until it frees up or the elapsed budget passes. Task.Delay carries the caller's
                // token, so a cancellation here escapes as the caller's own.
                await Task.Delay(LockRetry, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The budget passed with the lock still unavailable, or a non-transient failure: the
                // transaction cannot be serialized, so it is unavailable rather than a bypassed write.
                throw new HistoryUnavailableException($"could not lock pane history at {path}: {ex.Message}", ex);
            }
        }
    }

    /// <summary>Releases the transaction lock if it is still held — the safety net for any path that opened
    /// the history but did not reach <see cref="Save"/>.</summary>
    public void Dispose()
    {
        _lock?.Dispose();
        _lock = null;
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
    /// <summary>
    /// Hosts this tool has ever <em>attempted</em> — every target it tried to reach, whether or not the
    /// host answered, unioned with every host that actually answered. This is the persistent fleet
    /// membership: a run that does not include one of these is looking at less of the fleet than it has
    /// already seen — which is not something the run can work out from its own arguments, because a host it
    /// was not told about is indistinguishable from a host that does not exist. A target attempted once and
    /// never yet collected is here even though it has no epoch or continuity, which is exactly what stops a
    /// first-time failure from being forgotten and a later omission from reading as a complete view.
    /// </summary>
    public IReadOnlyCollection<string> KnownHosts
    {
        get
        {
            var known = new HashSet<string>(_attempted, StringComparer.Ordinal);
            known.UnionWith(_hosts.Keys);
            return known;
        }
    }

    /// <summary>When this host was last collected in full under the current server, if it was.</summary>
    public DateTimeOffset? SweptAt(string? host)
        => _hosts.TryGetValue(TargetId.ForHost(host).Key, out HostMemory? known) ? known.SweptAt : null;

    /// <summary>
    /// The timestamp a transaction stamps its observations with: the sampled wall clock, but never
    /// earlier than the greatest time already persisted in this loaded history. Registration order is the
    /// whole of contested ownership, so a timestamp that moved backwards would invert it — and two things
    /// can move it backwards. Lock acquisition is not fair, so a transaction that started waiting first can
    /// acquire the lock second, and a wall clock read at the wrong moment (or after an NTP step) can be
    /// earlier than one a prior, already-committed transaction wrote. Both are defended the same way:
    /// sample after the lock is held (so the read reflects when this transaction actually runs), then clamp
    /// the sample up to the greatest persisted <see cref="HostMemory.SweptAt"/>, <see
    /// cref="PaneMemory.ClaimedAt"/> or <see cref="PaneMemory.Since"/>. Because every serialized
    /// transaction sees the previous one's writes before it stamps, a later sweep can never receive an
    /// earlier timestamp; equal is allowed, and an equal registration time is an inferred, not observed,
    /// order — exactly the outcome for two claims the tool cannot distinguish in time.
    /// </summary>
    public DateTimeOffset TransactionTime(DateTimeOffset sampled)
    {
        DateTimeOffset floor = DateTimeOffset.MinValue;
        foreach (HostMemory host in _hosts.Values)
        {
            if (host.SweptAt is { } swept && swept > floor)
            {
                floor = swept;
            }
        }

        foreach (PaneMemory pane in _entries.Values)
        {
            if (pane.Since > floor)
            {
                floor = pane.Since;
            }

            if (pane.ClaimedAt is { } claimed && claimed > floor)
            {
                floor = claimed;
            }
        }

        return sampled >= floor ? sampled : floor;
    }

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
    /// <param name="attempted">
    /// Hosts this sweep <em>attempted</em>, by raw alias (null local) — every target it tried, whether or
    /// not it answered. Folded into the persistent <see cref="KnownHosts"/> membership so a target that
    /// failed before it ever collected is still remembered, and a later run that omits it can tell its view
    /// narrowed rather than reading it as complete. Grows monotonically and never carries an epoch,
    /// continuity or pane on its own — those belong only to a host that answered (<paramref name="hosts"/>).
    /// Defaults to the collected set when a caller does not distinguish the two (the tests that pass only
    /// one host set), and the collected hosts are always folded in regardless, since a host that answered
    /// was by definition attempted.
    /// </param>
    public IReadOnlyList<string> Save(
        IEnumerable<TmuxPane> live,
        IEnumerable<string?>? hosts = null,
        IEnumerable<string?>? attempted = null)
    {
        var seen = live.ToArray();
        var keep = seen.Select(Key).ToHashSet(StringComparer.Ordinal);
        HashSet<string>? collected = hosts is null
            ? null
            : hosts.Select(h => TargetId.ForHost(h).Key).ToHashSet(StringComparer.Ordinal);

        // Fleet membership grows with every attempted target — the last unit of state that must survive a
        // host that failed on its very first attempt. It is recorded here, at the commit point, so it
        // reaches disk on any sweep that gets far enough to Save, including a total or partial failure. The
        // collected set (and every collected host already on disk) is folded in unconditionally, keeping
        // the persisted invariant that a host which answered is also in attempted.
        IEnumerable<string?> attemptedHosts = attempted ?? hosts ?? seen.Select(p => p.Host).Distinct();
        foreach (string? host in attemptedHosts)
        {
            _attempted.Add(TargetId.ForHost(host).Key);
        }

        if (collected is not null)
        {
            _attempted.UnionWith(collected);
        }

        _attempted.UnionWith(_hosts.Keys);

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
                new HistoryFile { Panes = _entries, Hosts = _hosts, Attempted = [.. _attempted] },
                PaneHistoryJsonContext.Default.HistoryFile));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(tmp);
            throw new HistoryUnavailableException($"could not persist pane history to {_path}: {ex.Message}", ex);
        }

        // Save is the commit: the write has landed, so the transaction lock is released here rather than
        // held for whatever the caller does next (a report, a rename, a GitHub read). A failed write above
        // keeps the lock, and the caller's dispose releases it.
        _lock?.Dispose();
        _lock = null;

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
/// The pane history transaction could not proceed — its lock could not be taken, an existing file could
/// not be read or parsed, or a write did not land. The history is load-bearing (a stale witnessed order
/// left on disk, or a known host forgotten because the file was unreadable, would be believed next run),
/// so any of these surfaces as the unavailable contract rather than being swallowed into a success-shaped
/// report.
/// </summary>
internal sealed class HistoryUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>The on-disk shape: what is known per window, per host, and every host ever attempted.</summary>
internal sealed record HistoryFile
{
    [JsonPropertyName("panes")]
    public Dictionary<string, PaneMemory>? Panes { get; init; }

    [JsonPropertyName("hosts")]
    public Dictionary<string, HostMemory>? Hosts { get; init; }

    /// <summary>Every host ever targeted, answering or not — the persistent fleet membership, as a list of
    /// canonical target keys.</summary>
    [JsonPropertyName("attempted")]
    public List<string>? Attempted { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(HistoryFile))]
internal partial class PaneHistoryJsonContext : JsonSerializerContext
{
}
