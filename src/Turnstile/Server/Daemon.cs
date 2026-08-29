namespace Turnstile.Server;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using Turnstile.Storage;

/// <summary>
/// The Turnstile daemon: HTTP/JSON over a Unix domain socket. Deliberately boring so a watch is a
/// <c>curl -N</c> and tools can be written in any language in an afternoon.
/// </summary>
public sealed class Daemon
{
    /// <summary>Builds and runs the daemon until the socket is closed or the process is signalled.</summary>
    public static Task<int> RunAsync(string socketPath, string dbPath, CancellationToken ct = default)
        => RunAsync(socketPath, dbPath, options: null, ct);

    /// <summary>
    /// Test seam: same daemon, but with an injectable per-instance logging provider (to observe request-handler
    /// logging without touching process-global state) and watch hooks (to drive a deterministic failure on the
    /// watch path). Production callers use the two-argument overload; <paramref name="options"/> stays null there.
    /// </summary>
    internal static async Task<int> RunAsync(string socketPath, string dbPath, DaemonOptions? options, CancellationToken ct = default)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(dbPath));
        if (dir is not null)
        {
            Directory.CreateDirectory(dir);
        }

        // Take exclusive database ownership before touching anything else — the socket file included. A second
        // daemon (or any open direct store) fails here, before we would otherwise delete a live daemon's socket
        // or open a second writer behind an existing watch. Exclusive ownership is the invariant the daemon's
        // live watch rests on: with no direct store able to open the file, every commit flows through this
        // store's change signal (#202). Held for the daemon's whole lifetime; the OS drops it if we crash.
        using ModeLock modeLock = ModeLock.AcquireExclusive(dbPath);

        // A Unix socket bind fails if a stale file is present; clear it first.
        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        using KvStore store = KvStore.Open(dbPath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        if (options?.LoggerProvider is ILoggerProvider provider)
        {
            builder.Logging.AddProvider(provider);
        }

        builder.Services.Configure<KestrelServerOptions>(o => o.ListenUnixSocket(socketPath));
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, TurnstileJson.Default));
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(new DaemonInfo(socketPath, dbPath));
        builder.Services.AddSingleton(options?.WatchHooks ?? WatchHooks.None);

        WebApplication app = builder.Build();
        MapEndpoints(app);

        using var sweeper = new LeaseSweeper(store);
        sweeper.Start(ct);

        Console.WriteLine($"turnstile: listening on {socketPath} (db: {dbPath})");
        await app.RunAsync(ct);
        return 0;
    }

    private static void MapEndpoints(WebApplication app)
    {
        // Validation failures surface as 400 with a uniform envelope.
        app.Use(async (ctx, next) =>
        {
            try
            {
                await next();
            }
            catch (TurnstileValidationException ex)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse(ex.Message), TurnstileJson.Default.ErrorResponse);
            }
            catch (TurnstileRevisionExhaustedException ex)
            {
                // Server-side resource exhaustion, not client input: a stable 500 with the uniform envelope.
                ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await ctx.Response.WriteAsJsonAsync(new ErrorResponse(ex.Message), TurnstileJson.Default.ErrorResponse);
            }
        });

        app.MapGet("/status", (KvStore store, DaemonInfo info) =>
        {
            long size = File.Exists(info.DbPath) ? new FileInfo(info.DbPath).Length : 0;
            return Results.Json(new StatusResponse(store.CurrentRevision, size, info.SocketPath), TurnstileJson.Default.StatusResponse);
        });

        app.MapGet("/kv", (KvStore store, string? prefix, int? limit, bool? keys_only) =>
        {
            RangeReadResult range = store.RangeSnapshot(prefix ?? "/", limit ?? 0, keys_only ?? false);
            RangeItem[] items = new RangeItem[range.Items.Count];
            for (int i = 0; i < range.Items.Count; i++)
            {
                KeyState r = range.Items[i];
                items[i] = new RangeItem(r.Key, r.CreateRevision, r.ModRevision, r.Lease, r.Immutable,
                    r.Value is null ? null : Convert.ToBase64String(r.Value));
            }

            return Results.Json(new RangeResponse(range.Revision, items), TurnstileJson.Default.RangeResponse);
        });

        app.MapGet("/kv/{**key}", (string key, HttpContext ctx, KvStore store) =>
        {
            KeyState? s = store.Get(Key(key));
            if (s is null)
            {
                return Results.StatusCode(StatusCodes.Status404NotFound);
            }

            SetKeyHeaders(ctx, s);
            return Results.Bytes(s.Value ?? [], "application/octet-stream");
        });

        app.MapPost("/kv/{**key}", async (string key, HttpContext ctx, KvStore store, bool? immutable, string? lease) =>
        {
            byte[] body = await ReadBodyAsync(ctx);
            WriteResult r = await store.CreateAsync(Key(key), body, immutable ?? false, lease);
            return ToResult(ctx, Key(key), r);
        });

        app.MapPut("/kv/{**key}", async (string key, HttpContext ctx, KvStore store, bool? unconditional) =>
        {
            byte[] body = await ReadBodyAsync(ctx);
            WriteResult r = await store.UpdateAsync(Key(key), body, ParseIfMatch(ctx), unconditional ?? false);
            return ToResult(ctx, Key(key), r);
        });

        app.MapDelete("/kv/{**key}", async (string key, HttpContext ctx, KvStore store, bool? unconditional) =>
        {
            WriteResult r = await store.DeleteAsync(Key(key), ParseIfMatch(ctx), unconditional ?? false);
            return ToResult(ctx, Key(key), r);
        });

        app.MapPost("/lease", async (KvStore store, LeaseCreateRequest req) =>
        {
            LeaseInfo lease = await store.CreateLeaseAsync(req.Ttl);
            return Results.Json(new LeaseCreatedResponse(lease.Id, lease.TtlSecs), TurnstileJson.Default.LeaseCreatedResponse, statusCode: StatusCodes.Status201Created);
        });

        app.MapPut("/lease/{id}", async (string id, KvStore store) =>
        {
            long? remaining = await store.KeepAliveAsync(id);
            return remaining is null
                ? Error(StatusCodes.Status410Gone, "lease expired or unknown; stop, do not re-acquire")
                : Results.Json(new LeaseKeepaliveResponse(remaining.Value), TurnstileJson.Default.LeaseKeepaliveResponse);
        });

        app.MapDelete("/lease/{id}", async (string id, KvStore store) =>
        {
            bool revoked = await store.RevokeLeaseAsync(id);
            return revoked ? Results.NoContent() : Error(StatusCodes.Status404NotFound, "lease does not exist");
        });

        app.MapGet("/lease/{id}", (string id, KvStore store) =>
        {
            LeaseView? v = store.GetLease(id);
            return v is null
                ? Error(StatusCodes.Status404NotFound, "lease does not exist")
                : Results.Json(new LeaseViewResponse(v.Id, v.TtlSecs, v.TtlRemaining, [.. v.Keys]), TurnstileJson.Default.LeaseViewResponse);
        });

        app.MapPost("/txn", async (KvStore store, TxnRequest req) =>
        {
            TxnResult result = await store.TxnAsync(
                MapCompares(req.Compare),
                MapOps(req.Success),
                MapOps(req.Failure));

            TxnOpResponseDto[] responses = new TxnOpResponseDto[result.Responses.Count];
            for (int i = 0; i < responses.Length; i++)
            {
                TxnOpResult r = result.Responses[i];
                KeyState? s = r.State;
                responses[i] = new TxnOpResponseDto(
                    Op: r.Kind.ToString().ToLowerInvariant(),
                    Key: r.Key,
                    Found: s is not null,
                    CreateRevision: s?.CreateRevision ?? 0,
                    ModRevision: s?.ModRevision ?? 0,
                    Lease: s?.Lease,
                    Immutable: s?.Immutable ?? false,
                    Value: s?.Value is null ? null : Convert.ToBase64String(s.Value));
            }

            return Results.Json(new TxnResponseDto(result.Succeeded, result.Revision, responses), TurnstileJson.Default.TxnResponseDto);
        });

        app.MapGet("/watch", WatchAsync);
    }

    // SSE watch: stream the change log from ?from in revision order, emit `sync` when caught up, then
    // stream live events; heartbeat every 30s. The log-structured store makes "everything after N" a
    // WHERE id > N scan on short-lived connections, so a watcher never pins the WAL.
    private static async Task WatchAsync(HttpContext ctx, KvStore store, WatchHooks hooks, string? prefix, long? from)
    {
        ctx.Response.Headers.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-cache";
        ctx.Response.Headers["X-Accel-Buffering"] = "no";

        CancellationToken ct = ctx.RequestAborted;
        string p = prefix ?? "/";
        TimeSpan heartbeat = hooks.HeartbeatInterval ?? TimeSpan.FromSeconds(30);

        // The enumeration runs under a token we own: linking it to RequestAborted still propagates a client
        // disconnect, and cancelling it ourselves on the way out unblocks a pending MoveNextAsync so we can
        // observe it before disposing. Disposing an async enumerator while a move is in flight is illegal — the
        // generated state machine throws a NotSupportedException ("Specified method is not supported.") from
        // DisposeAsync — so the observe-then-dispose ordering in the finally is what keeps teardown clean,
        // rather than a catch that masks the symptom.
        using var enumCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        IAsyncEnumerator<WatchMessage> events = store.WatchAsync(p, from ?? 0, enumCts.Token).GetAsyncEnumerator(enumCts.Token);
        Task<bool> moveTask = events.MoveNextAsync().AsTask();
        try
        {
            while (true)
            {
                // Race the next event against a heartbeat timer on its own token, never linked to
                // RequestAborted. On a disconnect the cancelled move wins this race — never the delay — so the
                // heartbeat write never runs on the cancellation path; a live event cancels the timer so idle
                // heartbeats do not accumulate.
                using var heartbeatCts = new CancellationTokenSource();
                if (await Task.WhenAny(moveTask, Task.Delay(heartbeat, heartbeatCts.Token)) != moveTask)
                {
                    hooks.BeforeHeartbeatWrite?.Invoke();
                    await ctx.Response.WriteAsync(": heartbeat\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    continue;
                }

                heartbeatCts.Cancel();
                if (!await moveTask)
                {
                    break;
                }

                hooks.BeforeMessageWrite?.Invoke();
                await WriteWatchMessageAsync(ctx, events.Current, ct);
                moveTask = events.MoveNextAsync().AsTask();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client disconnected and the abort surfaced cleanly: a watch ending is normal, not an error.
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException)
        {
            // The same disconnect, surfaced as a connection reset (IOException) or a write against the
            // already-disposed response (ObjectDisposedException) that raced the abort. Normal only once the
            // request is actually aborted; anything thrown while the watch is still live propagates untouched
            // so real failures stay visible.
        }
        finally
        {
            // Unblock and observe the pending move before disposing, so DisposeAsync never races an in-flight
            // MoveNextAsync. Its result and any cancellation are deliberately discarded: a genuine live error
            // already propagated from the loop body above and must not be masked by teardown.
            enumCts.Cancel();
            try
            {
                await moveTask;
            }
            catch
            {
                // Expected during teardown: the move is cancelled, or observed the same disconnect.
            }

            await events.DisposeAsync();
        }
    }

    private static Task WriteWatchMessageAsync(HttpContext ctx, WatchMessage msg, CancellationToken ct)
        => msg switch
        {
            WatchEventMessage ev => WriteWatchEventAsync(ctx, ev.Event, ct),
            WatchSyncMessage sync => WriteSseAsync(ctx, "sync", JsonSerializer.Serialize(new WatchSyncDto(sync.Revision), TurnstileJson.Default.WatchSyncDto), ct),
            _ => Task.CompletedTask,
        };

    private static Task WriteWatchEventAsync(HttpContext ctx, WatchEvent e, CancellationToken ct)
    {
        if (e.Deleted)
        {
            var dto = new WatchDeleteEventDto(e.Key, e.Revision, e.PrevValue is null ? null : Convert.ToBase64String(e.PrevValue));
            return WriteSseAsync(ctx, "delete", JsonSerializer.Serialize(dto, TurnstileJson.Default.WatchDeleteEventDto), ct);
        }

        var put = new WatchPutEventDto(e.Key, e.CreateRevision, e.Revision, e.Lease, e.Immutable, e.Value is null ? null : Convert.ToBase64String(e.Value));
        return WriteSseAsync(ctx, "put", JsonSerializer.Serialize(put, TurnstileJson.Default.WatchPutEventDto), ct);
    }

    private static async Task WriteSseAsync(HttpContext ctx, string eventName, string data, CancellationToken ct)
    {
        await ctx.Response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    private static IReadOnlyList<TxnCompare> MapCompares(TxnCompareDto[]? compares)
    {
        if (compares is null || compares.Length == 0)
        {
            return [];
        }

        var result = new List<TxnCompare>(compares.Length);
        foreach (TxnCompareDto c in compares)
        {
            result.Add(new TxnCompare(c.Key, ParseTarget(c.Target), ParseCompareOp(c.Op), c.Revision ?? 0, DecodeValue(c.Value), c.Lease));
        }

        return result;
    }

    private static IReadOnlyList<TxnOp> MapOps(TxnOpDto[]? ops)
    {
        if (ops is null || ops.Length == 0)
        {
            return [];
        }

        var result = new List<TxnOp>(ops.Length);
        foreach (TxnOpDto o in ops)
        {
            result.Add(new TxnOp(ParseOpKind(o.Op), o.Key, DecodeValue(o.Value), o.Lease, o.Immutable ?? false));
        }

        return result;
    }

    private static TxnTarget ParseTarget(string target) => target switch
    {
        "create_revision" => TxnTarget.CreateRevision,
        "mod_revision" => TxnTarget.ModRevision,
        "value" => TxnTarget.Value,
        "lease" => TxnTarget.Lease,
        _ => throw new TurnstileValidationException($"unknown compare target '{target}'"),
    };

    private static TxnCompareOp ParseCompareOp(string op) => op switch
    {
        "==" => TxnCompareOp.Equal,
        "!=" => TxnCompareOp.NotEqual,
        "<" => TxnCompareOp.Less,
        ">" => TxnCompareOp.Greater,
        _ => throw new TurnstileValidationException($"unknown compare op '{op}'"),
    };

    private static TxnOpKind ParseOpKind(string op) => op switch
    {
        "put" => TxnOpKind.Put,
        "delete" => TxnOpKind.Delete,
        "get" => TxnOpKind.Get,
        _ => throw new TurnstileValidationException($"unknown txn op '{op}'"),
    };

    private static byte[]? DecodeValue(string? value)
    {
        if (value is null)
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(value);
        }
        catch (FormatException)
        {
            throw new TurnstileValidationException("value must be base64-encoded");
        }
    }

    // Route captures the key without its leading slash; keys are canonically rooted at '/'.
    private static string Key(string routeKey) => "/" + routeKey;

    private static long? ParseIfMatch(HttpContext ctx)
    {
        string? header = ctx.Request.Headers.IfMatch;
        if (string.IsNullOrEmpty(header))
        {
            return null;
        }

        header = header.Trim().Trim('"');
        return long.TryParse(header, out long rev) ? rev : null;
    }

    private static void SetKeyHeaders(HttpContext ctx, KeyState s)
    {
        ctx.Response.Headers.ETag = s.ModRevision.ToString();
        ctx.Response.Headers["X-Turnstile-Create-Revision"] = s.CreateRevision.ToString();
        ctx.Response.Headers["X-Turnstile-Immutable"] = s.Immutable ? "1" : "0";
        if (s.Lease is not null)
        {
            ctx.Response.Headers["X-Turnstile-Lease"] = s.Lease;
        }
    }

    private static IResult ToResult(HttpContext ctx, string key, WriteResult r)
    {
        switch (r.Status)
        {
            case WriteStatus.Created:
                ctx.Response.Headers.ETag = r.Revision.ToString();
                ctx.Response.Headers.Location = $"/kv{key}";
                return Results.Json(new WriteResponse(r.Revision), TurnstileJson.Default.WriteResponse, statusCode: StatusCodes.Status201Created);

            case WriteStatus.Ok:
                ctx.Response.Headers.ETag = r.Revision.ToString();
                return Results.Json(new WriteResponse(r.Revision), TurnstileJson.Default.WriteResponse);

            case WriteStatus.Deleted:
                return Results.Json(new WriteResponse(r.Revision), TurnstileJson.Default.WriteResponse);

            case WriteStatus.Exists:
                return Error(StatusCodes.Status409Conflict, "key already exists");

            case WriteStatus.NotFound:
                return Error(StatusCodes.Status404NotFound, "key does not exist");

            case WriteStatus.PreconditionRequired:
                return Error(StatusCodes.Status428PreconditionRequired, "conditional write requires If-Match or ?unconditional");

            case WriteStatus.PreconditionFailed:
                if (r.Current is not null)
                {
                    ctx.Response.Headers.ETag = r.Current.ModRevision.ToString();
                }

                return Error(StatusCodes.Status412PreconditionFailed, "If-Match revision is stale");

            case WriteStatus.Immutable:
                return Error(StatusCodes.Status409Conflict, "key is immutable");

            default:
                return Error(StatusCodes.Status500InternalServerError, "unexpected write status");
        }
    }

    private static IResult Error(int statusCode, string message)
        => Results.Json(new ErrorResponse(message), TurnstileJson.Default.ErrorResponse, statusCode: statusCode);

    private static async Task<byte[]> ReadBodyAsync(HttpContext ctx)
    {
        using var ms = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(ms);
        return ms.ToArray();
    }
}

/// <summary>Immutable daemon configuration, injected into endpoints.</summary>
internal sealed record DaemonInfo(string SocketPath, string DbPath);

/// <summary>
/// Test-only knobs for a single daemon instance. Never populated on the production path (the two-argument
/// <see cref="Daemon.RunAsync(string, string, CancellationToken)"/> passes null), so it introduces no
/// process-global state and no runtime behaviour change for real callers.
/// </summary>
internal sealed class DaemonOptions
{
    /// <summary>A per-instance logging provider so a test can observe this daemon's request-handler logging.</summary>
    public ILoggerProvider? LoggerProvider { get; init; }

    /// <summary>Hooks that let a test drive a deterministic failure on the watch path.</summary>
    public WatchHooks? WatchHooks { get; init; }
}

/// <summary>
/// Injection points on the watch handler, defaulting to no-ops (<see cref="None"/>). A test overrides the
/// heartbeat interval and/or throws from a write to exercise a live failure or the enumerator-teardown
/// ordering; production resolves <see cref="None"/> from DI and every callback is null.
/// </summary>
internal sealed class WatchHooks
{
    public static readonly WatchHooks None = new();

    /// <summary>Overrides the 30s heartbeat cadence so a test does not have to wait for a real heartbeat.</summary>
    public TimeSpan? HeartbeatInterval { get; init; }

    /// <summary>Invoked just before a data message is written (the pending move is already observed here).</summary>
    public Action? BeforeMessageWrite { get; init; }

    /// <summary>Invoked just before a heartbeat is written (a move is in flight here — the teardown-race window).</summary>
    public Action? BeforeHeartbeatWrite { get; init; }
}
