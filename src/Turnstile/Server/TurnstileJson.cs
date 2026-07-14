namespace Turnstile.Server;

using System.Text.Json.Serialization;

/// <summary>Metadata + payload for a single key in a range scan. Value is base64 (opaque bytes).</summary>
public sealed record RangeItem(
    string Key,
    long CreateRevision,
    long ModRevision,
    string? Lease,
    bool Immutable,
    string? Value);

/// <summary>A range-scan response: the store revision at scan time plus the matched keys.</summary>
public sealed record RangeResponse(long Revision, RangeItem[] Kvs);

/// <summary>Returned by successful create/update/delete, carrying the new revision.</summary>
public sealed record WriteResponse(long Revision);

/// <summary>Daemon status snapshot.</summary>
public sealed record StatusResponse(long CurrentRevision, long DbSizeBytes, string Socket);

/// <summary>Request body for granting a lease.</summary>
public sealed record LeaseCreateRequest(long Ttl);

/// <summary>Returned when a lease is granted.</summary>
public sealed record LeaseCreatedResponse(string Id, long Ttl);

/// <summary>Returned by a keepalive.</summary>
public sealed record LeaseKeepaliveResponse(long TtlRemaining);

/// <summary>A lease's state and attached keys.</summary>
public sealed record LeaseViewResponse(string Id, long TtlSecs, long TtlRemaining, string[] Keys);

/// <summary>Uniform error envelope.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>A txn compare clause. Value is base64 (opaque bytes); Revision is used for revision targets.</summary>
public sealed record TxnCompareDto(string Key, string Target, string Op, long? Revision, string? Value, string? Lease);

/// <summary>A txn branch op: put (upsert), delete, or get. Value is base64 for put.</summary>
public sealed record TxnOpDto(string Op, string Key, string? Value, string? Lease, bool? Immutable);

/// <summary>Request body for POST /txn: compare clauses (ANDed) select the success or failure branch.</summary>
public sealed record TxnRequest(TxnCompareDto[]? Compare, TxnOpDto[]? Success, TxnOpDto[]? Failure);

/// <summary>One entry in a txn response — populated for get ops. Value is base64.</summary>
public sealed record TxnOpResponseDto(string Op, string Key, bool Found, long CreateRevision, long ModRevision, string? Lease, string? Value);

/// <summary>Response for POST /txn: which branch ran, the store revision, and any get responses.</summary>
public sealed record TxnResponseDto(bool Succeeded, long Revision, TxnOpResponseDto[] Responses);

/// <summary>A watch put event. Value is base64 (opaque bytes).</summary>
public sealed record WatchPutEventDto(string Key, long CreateRevision, long ModRevision, string? Lease, string? Value);

/// <summary>A watch delete event, carrying the previous value (base64) for reverse-index maintenance.</summary>
public sealed record WatchDeleteEventDto(string Key, long ModRevision, string? PrevValue);

/// <summary>The watch sync event: the backlog has drained and the client is caught up to this revision.</summary>
public sealed record WatchSyncDto(long Revision);

/// <summary>Source-generated JSON for AOT. snake_case on the wire.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RangeResponse))]
[JsonSerializable(typeof(WriteResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(LeaseCreateRequest))]
[JsonSerializable(typeof(LeaseCreatedResponse))]
[JsonSerializable(typeof(LeaseKeepaliveResponse))]
[JsonSerializable(typeof(LeaseViewResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(TxnRequest))]
[JsonSerializable(typeof(TxnResponseDto))]
[JsonSerializable(typeof(WatchPutEventDto))]
[JsonSerializable(typeof(WatchDeleteEventDto))]
[JsonSerializable(typeof(WatchSyncDto))]
public partial class TurnstileJson : JsonSerializerContext;
