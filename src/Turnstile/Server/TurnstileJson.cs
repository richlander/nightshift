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

/// <summary>Uniform error envelope.</summary>
public sealed record ErrorResponse(string Error);

/// <summary>Source-generated JSON for AOT. snake_case on the wire.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RangeResponse))]
[JsonSerializable(typeof(WriteResponse))]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
public partial class TurnstileJson : JsonSerializerContext;
