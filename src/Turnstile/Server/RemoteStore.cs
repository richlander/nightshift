namespace Turnstile.Server;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Turnstile.Storage;

/// <summary>
/// Daemon-mode <see cref="ITurnstile"/>: speaks the wire protocol (HTTP/JSON/SSE over the Unix socket)
/// to a running <c>turnstile serve</c>. Reconstructs the same model types <see cref="LocalStore"/>
/// returns, so a caller holding an <see cref="ITurnstile"/> cannot tell the two apart. This is the
/// only path that works cross-machine — there is no local file to open on a second host.
/// </summary>
public sealed class RemoteStore : ITurnstile
{
    private readonly HttpClient _http;

    private RemoteStore(HttpClient http) => _http = http;

    /// <summary>Connects to the daemon listening on <paramref name="socketPath"/>.</summary>
    public static RemoteStore Connect(string socketPath)
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

        // A watch is a long-lived stream, so the client cannot impose a wall-clock timeout; unary calls
        // pass their own CancellationToken instead.
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        return new RemoteStore(http);
    }

    public async Task<long> GetRevisionAsync(CancellationToken ct = default)
    {
        StatusResponse status = await GetJsonAsync(await _http.GetAsync("/status", ct), TurnstileJson.Default.StatusResponse, ct);
        return status.CurrentRevision;
    }

    public async Task<KeyState?> GetAsync(string key, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.GetAsync($"/kv{key}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        res.EnsureSuccessStatusCode();
        byte[] value = await res.Content.ReadAsByteArrayAsync(ct);
        long modRev = HeaderLong(res, "ETag");
        long createRev = HeaderLong(res, "X-Turnstile-Create-Revision");
        bool immutable = Header(res, "X-Turnstile-Immutable") == "1";
        string? lease = Header(res, "X-Turnstile-Lease");
        return new KeyState(key, createRev, modRev, lease, immutable, value);
    }

    public async Task<IReadOnlyList<KeyState>> RangeAsync(string prefix, int limit = 0, bool keysOnly = false, CancellationToken ct = default)
    {
        string query = Query(("prefix", prefix), ("limit", limit > 0 ? limit.ToString() : null), ("keys_only", keysOnly ? "true" : null));
        RangeResponse range = await GetJsonAsync(await _http.GetAsync($"/kv{query}", ct), TurnstileJson.Default.RangeResponse, ct);
        var result = new List<KeyState>(range.Kvs.Length);
        foreach (RangeItem item in range.Kvs)
        {
            result.Add(new KeyState(
                item.Key,
                item.CreateRevision,
                item.ModRevision,
                item.Lease,
                item.Immutable,
                item.Value is null ? null : Convert.FromBase64String(item.Value)));
        }

        return result;
    }

    public async Task<WriteResult> CreateAsync(string key, byte[] value, bool immutable = false, string? lease = null, CancellationToken ct = default)
    {
        string query = Query(("immutable", immutable ? "true" : null), ("lease", lease));
        using var content = new ByteArrayContent(value);
        using HttpResponseMessage res = await _http.PostAsync($"/kv{key}{query}", content, ct);
        return await MapWriteAsync(res, key, WriteStatus.Created, WriteStatus.Exists, ct);
    }

    public async Task<WriteResult> UpdateAsync(string key, byte[] value, long? ifMatch, bool unconditional = false, CancellationToken ct = default)
    {
        string query = Query(("unconditional", unconditional ? "true" : null));
        using var content = new ByteArrayContent(value);
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/kv{key}{query}") { Content = content };
        AddIfMatch(req, ifMatch);
        using HttpResponseMessage res = await _http.SendAsync(req, ct);
        return await MapWriteAsync(res, key, WriteStatus.Ok, WriteStatus.Immutable, ct);
    }

    public async Task<WriteResult> DeleteAsync(string key, long? ifMatch, bool unconditional = false, CancellationToken ct = default)
    {
        string query = Query(("unconditional", unconditional ? "true" : null));
        using var req = new HttpRequestMessage(HttpMethod.Delete, $"/kv{key}{query}");
        AddIfMatch(req, ifMatch);
        using HttpResponseMessage res = await _http.SendAsync(req, ct);
        return await MapWriteAsync(res, key, WriteStatus.Deleted, WriteStatus.Immutable, ct);
    }

    public async Task<TxnResult> TxnAsync(IReadOnlyList<TxnCompare> compare, IReadOnlyList<TxnOp> success, IReadOnlyList<TxnOp> failure, CancellationToken ct = default)
    {
        var req = new TxnRequest(
            [.. compare.Select(ToCompareDto)],
            [.. success.Select(ToOpDto)],
            [.. failure.Select(ToOpDto)]);
        string json = JsonSerializer.Serialize(req, TurnstileJson.Default.TxnRequest);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        TxnResponseDto dto = await GetJsonAsync(await _http.PostAsync("/txn", content, ct), TurnstileJson.Default.TxnResponseDto, ct);

        var responses = new List<TxnOpResult>(dto.Responses.Length);
        foreach (TxnOpResponseDto r in dto.Responses)
        {
            KeyState? state = r.Found
                ? new KeyState(r.Key, r.CreateRevision, r.ModRevision, r.Lease, false, r.Value is null ? null : Convert.FromBase64String(r.Value))
                : null;
            responses.Add(new TxnOpResult(ParseOpKind(r.Op), r.Key, state));
        }

        return new TxnResult(dto.Succeeded, dto.Revision, responses);
    }

    public async Task<LeaseInfo> CreateLeaseAsync(long ttlSecs, CancellationToken ct = default)
    {
        using var content = new StringContent($"{{\"ttl\":{ttlSecs}}}", System.Text.Encoding.UTF8, "application/json");
        LeaseCreatedResponse dto = await GetJsonAsync(await _http.PostAsync("/lease", content, ct), TurnstileJson.Default.LeaseCreatedResponse, ct);
        // The wire response omits expires_at; approximate it from the local clock (informational only —
        // keepalive returns authoritative remaining TTL).
        long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + dto.Ttl;
        return new LeaseInfo(dto.Id, dto.Ttl, expiresAt);
    }

    public async Task<long?> KeepAliveAsync(string id, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.PutAsync($"/lease/{id}", null, ct);
        if (res.StatusCode == HttpStatusCode.Gone)
        {
            return null;
        }

        LeaseKeepaliveResponse dto = await GetJsonAsync(res, TurnstileJson.Default.LeaseKeepaliveResponse, ct);
        return dto.TtlRemaining;
    }

    public async Task<bool> RevokeLeaseAsync(string id, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.DeleteAsync($"/lease/{id}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        res.EnsureSuccessStatusCode();
        return true;
    }

    public async Task<LeaseView?> GetLeaseAsync(string id, CancellationToken ct = default)
    {
        using HttpResponseMessage res = await _http.GetAsync($"/lease/{id}", ct);
        if (res.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        LeaseViewResponse dto = await GetJsonAsync(res, TurnstileJson.Default.LeaseViewResponse, ct);
        return new LeaseView(dto.Id, dto.TtlSecs, dto.TtlRemaining, dto.Keys);
    }

    public async IAsyncEnumerable<WatchMessage> WatchAsync(string prefix, long fromExclusive, [EnumeratorCancellation] CancellationToken ct = default)
    {
        string query = Query(("prefix", prefix), ("from", fromExclusive > 0 ? fromExclusive.ToString() : null));
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/watch{query}");
        using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
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
            else if (line.StartsWith("data:", StringComparison.Ordinal) && eventName is not null)
            {
                string data = line["data:".Length..].Trim();
                if (ParseSseMessage(eventName, data) is WatchMessage msg)
                {
                    yield return msg;
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static WatchMessage? ParseSseMessage(string eventName, string data)
    {
        switch (eventName)
        {
            case "put":
            {
                WatchPutEventDto dto = JsonSerializer.Deserialize(data, TurnstileJson.Default.WatchPutEventDto)!;
                return new WatchEventMessage(new WatchEvent(
                    dto.ModRevision, dto.Key, Deleted: false, dto.CreateRevision, dto.Lease,
                    dto.Value is null ? null : Convert.FromBase64String(dto.Value), PrevValue: null));
            }

            case "delete":
            {
                WatchDeleteEventDto dto = JsonSerializer.Deserialize(data, TurnstileJson.Default.WatchDeleteEventDto)!;
                return new WatchEventMessage(new WatchEvent(
                    dto.ModRevision, dto.Key, Deleted: true, CreateRevision: 0, Lease: null,
                    Value: null, dto.PrevValue is null ? null : Convert.FromBase64String(dto.PrevValue)));
            }

            case "sync":
            {
                WatchSyncDto dto = JsonSerializer.Deserialize(data, TurnstileJson.Default.WatchSyncDto)!;
                return new WatchSyncMessage(dto.Revision);
            }

            default:
                return null;
        }
    }

    private async Task<WriteResult> MapWriteAsync(HttpResponseMessage res, string key, WriteStatus onSuccess, WriteStatus onConflict, CancellationToken ct)
    {
        switch ((int)res.StatusCode)
        {
            case 200 or 201:
                WriteResponse w = await GetJsonAsync(res, TurnstileJson.Default.WriteResponse, ct);
                return new WriteResult(onSuccess, w.Revision, null);

            case 404:
                return new WriteResult(WriteStatus.NotFound, 0, null);

            case 409:
                return new WriteResult(onConflict, 0, null);

            case 428:
                return new WriteResult(WriteStatus.PreconditionRequired, 0, null);

            case 412:
                long current = HeaderLong(res, "ETag");
                return new WriteResult(WriteStatus.PreconditionFailed, 0, new KeyState(key, 0, current, null, false, null));

            default:
                res.EnsureSuccessStatusCode();
                throw new InvalidOperationException($"unexpected write status {(int)res.StatusCode}");
        }
    }

    private static async Task<T> GetJsonAsync<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        res.EnsureSuccessStatusCode();
        string body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, type)!;
    }

    private static TxnCompareDto ToCompareDto(TxnCompare c)
        => new(c.Key, TargetToken(c.Target), CompareOpToken(c.Op), c.Revision,
            c.Value is null ? null : Convert.ToBase64String(c.Value), c.Lease);

    private static string TargetToken(TxnTarget target) => target switch
    {
        TxnTarget.CreateRevision => "create_revision",
        TxnTarget.ModRevision => "mod_revision",
        TxnTarget.Value => "value",
        TxnTarget.Lease => "lease",
        _ => "mod_revision",
    };

    private static TxnOpDto ToOpDto(TxnOp o)
        => new(o.Kind.ToString().ToLowerInvariant(), o.Key,
            o.Value is null ? null : Convert.ToBase64String(o.Value), o.Lease, o.Immutable ? true : null);

    private static string CompareOpToken(TxnCompareOp op) => op switch
    {
        TxnCompareOp.Equal => "==",
        TxnCompareOp.NotEqual => "!=",
        TxnCompareOp.Less => "<",
        TxnCompareOp.Greater => ">",
        _ => "==",
    };

    private static TxnOpKind ParseOpKind(string op) => op switch
    {
        "put" => TxnOpKind.Put,
        "delete" => TxnOpKind.Delete,
        _ => TxnOpKind.Get,
    };

    private static void AddIfMatch(HttpRequestMessage req, long? ifMatch)
    {
        if (ifMatch is long rev)
        {
            req.Headers.TryAddWithoutValidation("If-Match", $"\"{rev}\"");
        }
    }

    private static string? Header(HttpResponseMessage res, string name)
        => res.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault()
            : res.Content.Headers.TryGetValues(name, out IEnumerable<string>? cv) ? cv.FirstOrDefault()
            : null;

    private static long HeaderLong(HttpResponseMessage res, string name)
        => long.TryParse(Header(res, name)?.Trim('"'), out long v) ? v : 0;

    private static string Query(params (string Name, string? Value)[] parameters)
    {
        List<string> parts = [];
        foreach ((string name, string? value) in parameters)
        {
            if (value is not null)
            {
                parts.Add($"{name}={Uri.EscapeDataString(value)}");
            }
        }

        return parts.Count == 0 ? string.Empty : "?" + string.Join('&', parts);
    }
}
