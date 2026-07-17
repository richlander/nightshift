namespace Nightsky.Turnstile;

using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

/// <summary>
/// A read-only Turnstile wire client (HTTP/JSON/SSE over a Unix socket).
/// It exposes no mutation surface by construction.
/// </summary>
internal sealed class TurnstileClient : IDisposable
{
    private readonly HttpClient _http;

    private TurnstileClient(HttpClient http) => _http = http;

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

        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost"), Timeout = Timeout.InfiniteTimeSpan };
        return new TurnstileClient(http);
    }

    public async Task<long> CurrentRevisionAsync(CancellationToken ct)
    {
        StatusDto status = await ReadJsonAsync(await _http.GetAsync("/status", ct), WireJson.Default.StatusDto, ct);
        return status.CurrentRevision;
    }

    public async Task<IReadOnlyList<KvItem>> RangeAsync(string prefix, CancellationToken ct)
    {
        RangeDto range = await ReadJsonAsync(await _http.GetAsync($"/kv?prefix={Uri.EscapeDataString(prefix)}", ct), WireJson.Default.RangeDto, ct);
        var items = new List<KvItem>(range.Kvs.Length);
        foreach (RangeItemDto k in range.Kvs)
        {
            items.Add(new KvItem(
                k.Key,
                k.CreateRevision,
                k.ModRevision,
                k.Lease,
                k.Value is null ? [] : Convert.FromBase64String(k.Value)));
        }

        return items;
    }

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
            value);
    }

    public async IAsyncEnumerable<WatchSignal> WatchAsync(string prefix, long fromExclusive, [EnumeratorCancellation] CancellationToken ct)
    {
        string query = $"?prefix={Uri.EscapeDataString(prefix)}" + (fromExclusive > 0 ? $"&from={fromExclusive}" : string.Empty);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/watch{query}");
        using HttpResponseMessage res = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (res.StatusCode == HttpStatusCode.Gone)
        {
            throw new WatchCompactedException();
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
                continue;
            }

            if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                eventName = line["event:".Length..].Trim();
            }
            else if (line.StartsWith("data:", StringComparison.Ordinal) && eventName is not null)
            {
                string data = line["data:".Length..].Trim();
                if (ParseSseSignal(eventName, data) is WatchSignal signal)
                {
                    yield return signal;
                }
            }
        }
    }

    public void Dispose() => _http.Dispose();

    private static WatchSignal? ParseSseSignal(string eventName, string data)
    {
        switch (eventName)
        {
            case "put":
            {
                WatchPutEventDto dto = JsonSerializer.Deserialize(data, WireJson.Default.WatchPutEventDto)!;
                return new WatchSignal(dto.Key, Deleted: false, dto.ModRevision);
            }

            case "delete":
            {
                WatchDeleteEventDto dto = JsonSerializer.Deserialize(data, WireJson.Default.WatchDeleteEventDto)!;
                return new WatchSignal(dto.Key, Deleted: true, dto.ModRevision);
            }

            case "sync":
            {
                _ = JsonSerializer.Deserialize(data, WireJson.Default.WatchSyncDto)!;
                return null;
            }

            default:
                return null;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage res, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct)
    {
        res.EnsureSuccessStatusCode();
        string body = await res.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize(body, type)!;
    }

    private static string? Header(HttpResponseMessage res, string name)
        => res.Headers.TryGetValues(name, out IEnumerable<string>? values) ? values.FirstOrDefault()
            : res.Content.Headers.TryGetValues(name, out IEnumerable<string>? contentValues) ? contentValues.FirstOrDefault()
            : null;

    private static long HeaderLong(HttpResponseMessage res, string name)
        => long.TryParse(Header(res, name)?.Trim('"'), out long parsed) ? parsed : 0;
}
