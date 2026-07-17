namespace Nightshift.Turnstile;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

/// <summary>
/// A hand-rolled Turnstile client speaking the wire protocol (HTTP/JSON/SSE) over a Unix domain socket.
/// It links no Turnstile code — it is the proof that a controller in any language needs nothing but the
/// documented protocol. Every Nightshift interaction with coordination state goes through here.
/// </summary>
internal sealed class TurnstileClient : IDisposable
{
    private readonly HttpClient _http;

    internal TurnstileClient(HttpClient http) => _http = http;

    public static TurnstileClient Connect(string socketPath)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, ct) =>
            {
                var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), ct);
                return new NetworkStream(socket, ownsSocket: true);
            },
        };

        // Watches are long-lived streams, so no wall-clock timeout; unary calls carry a CancellationToken.
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        return new TurnstileClient(http);
    }

    /// <summary>The store's current revision; also a liveness probe for the daemon.</summary>
    public async Task<long> CurrentRevisionAsync(CancellationToken ct)
    {
        StatusDto status = await ReadJsonAsync(await _http.GetAsync("/status", ct), WireJson.Default.StatusDto, ct);
        return status.CurrentRevision;
    }

    /// <summary>Lists live keys under a prefix in key order.</summary>
    public async Task<IReadOnlyList<KvItem>> RangeAsync(string prefix, CancellationToken ct)
    {
        RangeDto range = await ReadJsonAsync(await _http.GetAsync($"/kv?prefix={Uri.EscapeDataString(prefix)}", ct), WireJson.Default.RangeDto, ct);
        var items = new List<KvItem>(range.Kvs.Length);
        foreach (RangeItemDto k in range.Kvs)
        {
            items.Add(new KvItem(k.Key, k.CreateRevision, k.ModRevision, k.Lease, k.Immutable,
                k.Value is null ? [] : Convert.FromBase64String(k.Value)));
        }

        return items;
    }

    /// <summary>Reads a single key, or null if it does not exist.</summary>
    public async Task<KvItem?> GetAsync(string key, CancellationToken ct)
    {
        using HttpResponseMessage res = await _http.GetAsync($"/kv{key}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        res.EnsureSuccessStatusCode();
        byte[] value = await res.Content.ReadAsByteArrayAsync(ct);
        return new KvItem(
            key,
            HeaderLong(res, "X-Turnstile-Create-Revision"),
            HeaderLong(res, "ETag"),
            Header(res, "X-Turnstile-Lease"),
            Header(res, "X-Turnstile-Immutable") == "1",
            value);
    }

    /// <summary>Grants a lease with the given TTL (seconds); returns its id.</summary>
    public async Task<string> CreateLeaseAsync(long ttlSecs, CancellationToken ct)
    {
        using var content = new StringContent($"{{\"ttl\":{ttlSecs}}}", Encoding.UTF8, "application/json");
        LeaseCreatedDto dto = await ReadJsonAsync(await _http.PostAsync("/lease", content, ct), WireJson.Default.LeaseCreatedDto, ct);
        return dto.Id;
    }

    /// <summary>Renews a lease; returns false if it is already gone (stop — do not re-acquire).</summary>
    public async Task<bool> KeepAliveAsync(string leaseId, CancellationToken ct)
    {
        using HttpResponseMessage res = await _http.PutAsync($"/lease/{leaseId}", null, ct);
        return res.StatusCode != HttpStatusCode.Gone && res.IsSuccessStatusCode;
    }

    /// <summary>Revokes a lease, deleting every key attached to it (e.g. the agent's claim).</summary>
    public async Task RevokeLeaseAsync(string leaseId, CancellationToken ct)
    {
        using HttpResponseMessage res = await _http.DeleteAsync($"/lease/{leaseId}", ct);
        if (res.StatusCode != HttpStatusCode.NotFound)
        {
            res.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Creates an immutable key. Returns true if newly created, false if it already exists (idempotent —
    /// re-seeding an unchanged work order is a no-op). Immutable keys can never be altered thereafter.
    /// </summary>
    public async Task<bool> CreateImmutableAsync(string key, string value, CancellationToken ct)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        using HttpResponseMessage res = await _http.PostAsync($"/kv{key}?immutable=true", content, ct);
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>Blind-writes an owned key: creates it, or overwrites unconditionally if it exists.</summary>
    public async Task SetAsync(string key, string value, CancellationToken ct)
    {
        var bytes = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        using (bytes)
        {
            using HttpResponseMessage created = await _http.PostAsync($"/kv{key}", bytes, ct);
            if (created.StatusCode != HttpStatusCode.Conflict)
            {
                created.EnsureSuccessStatusCode();
                return;
            }
        }

        using var overwrite = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        using HttpResponseMessage put = await _http.PutAsync($"/kv{key}?unconditional=true", overwrite, ct);
        put.EnsureSuccessStatusCode();
    }

    /// <summary>Unconditionally deletes a key (idempotent — a missing key is not an error).</summary>
    public async Task DeleteAsync(string key, CancellationToken ct)
    {
        using HttpResponseMessage res = await _http.DeleteAsync($"/kv{key}?unconditional=true", ct);
        if (res.StatusCode != HttpStatusCode.NotFound)
        {
            res.EnsureSuccessStatusCode();
        }
    }

    /// <summary>
    /// Writes a lease-attached key, replacing any existing value. Turnstile only attaches a lease on
    /// create, so an existing key is deleted first — safe here because each roster key has a single writer
    /// (the agent that owns it). When its lease expires the key vanishes, so the roster self-heals.
    /// </summary>
    public async Task PutLeasedAsync(string key, string value, string leaseId, CancellationToken ct)
    {
        if (await CreateLeasedAsync(key, value, leaseId, ct))
        {
            return;
        }

        await DeleteAsync(key, ct);
        await CreateLeasedAsync(key, value, leaseId, ct);
    }

    private async Task<bool> CreateLeasedAsync(string key, string value, string leaseId, CancellationToken ct)
    {
        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes(value));
        using HttpResponseMessage res = await _http.PostAsync($"/kv{key}?lease={Uri.EscapeDataString(leaseId)}", content, ct);
        if (res.StatusCode == HttpStatusCode.Conflict)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        return true;
    }

    /// <summary>
    /// Claims <paramref name="key"/> for exactly one caller: a txn that puts it (under
    /// <paramref name="leaseId"/>) iff it does not yet exist (create_revision == 0). The returned
    /// revision is the claim's fence.
    /// </summary>
    public async Task<ClaimResult> TryClaimAsync(string key, string leaseId, string value, CancellationToken ct)
    {
        var request = new TxnRequestDto(
            [new TxnCompareDto(key, "create_revision", "==", 0, null, null)],
            [new TxnOpDto("put", key, Convert.ToBase64String(Encoding.UTF8.GetBytes(value)), leaseId, null)],
            null);

        string json = JsonSerializer.Serialize(request, WireJson.Default.TxnRequestDto);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        TxnResponseDto dto = await ReadJsonAsync(await _http.PostAsync("/txn", content, ct), WireJson.Default.TxnResponseDto, ct);
        return new ClaimResult(dto.Succeeded, dto.Revision);
    }

    /// <summary>Streams change events under a prefix from <paramref name="fromExclusive"/>; sync/heartbeat frames are skipped.</summary>
    public async IAsyncEnumerable<WatchSignal> WatchAsync(string prefix, long fromExclusive, [EnumeratorCancellation] CancellationToken ct)
    {
        string query = $"?prefix={Uri.EscapeDataString(prefix)}" + (fromExclusive > 0 ? $"&from={fromExclusive}" : string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/watch{query}");
        using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Gone)
        {
            throw new WatchCompactedException(prefix, fromExclusive, await ReadCompactRevisionAsync(res, ct));
        }

        res.EnsureSuccessStatusCode();

        await using Stream stream = await res.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        string? eventName = null;
        while (await reader.ReadLineAsync(ct) is string line)
        {
            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }

            if (line[0] == ':')
            {
                continue; // heartbeat comment
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) && eventName is "put" or "delete")
            {
                using JsonDocument doc = JsonDocument.Parse(line["data:".Length..].Trim());
                JsonElement root = doc.RootElement;
                yield return new WatchSignal(
                    root.GetProperty("key").GetString() ?? string.Empty,
                    Deleted: eventName == "delete",
                    root.GetProperty("mod_revision").GetInt64());
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        res.EnsureSuccessStatusCode();
        string body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, type)!;
    }

    private static async Task<long?> ReadCompactRevisionAsync(HttpResponseMessage res, CancellationToken ct)
    {
        if (long.TryParse(Header(res, "X-Turnstile-Compact-Revision"), out long fromHeader))
        {
            return fromHeader;
        }

        string body = await res.Content.ReadAsStringAsync(ct);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(body);
            JsonElement root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("compact_revision", out JsonElement compact)
                && compact.TryGetInt64(out long fromBody))
            {
                return fromBody;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string? Header(HttpResponseMessage res, string name)
        => res.Headers.TryGetValues(name, out IEnumerable<string>? v) ? v.FirstOrDefault()
            : res.Content.Headers.TryGetValues(name, out IEnumerable<string>? cv) ? cv.FirstOrDefault()
            : null;

    private static long HeaderLong(HttpResponseMessage res, string name)
        => long.TryParse(Header(res, name)?.Trim('"'), out long v) ? v : 0;
}
