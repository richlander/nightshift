namespace Nightshift.Turnstile;

using System.Text.Json.Serialization;

// Wire DTOs mirroring Turnstile's HTTP/JSON surface (snake_case). These are Nightshift's OWN copy of
// the protocol shapes — Nightshift never links the Turnstile assembly, it speaks the wire. Anything a
// Go or Python controller would hand-roll, we hand-roll too, on purpose.

internal sealed record StatusDto(long CurrentRevision, long DbSizeBytes, string Socket);

internal sealed record RangeItemDto(string Key, long CreateRevision, long ModRevision, string? Lease, bool Immutable, string? Value);

internal sealed record RangeDto(long Revision, RangeItemDto[] Kvs);

internal sealed record LeaseCreatedDto(string Id, long Ttl);

internal sealed record LeaseKeepaliveDto(long TtlRemaining);

internal sealed record TxnCompareDto(string Key, string Target, string Op, long? Revision, string? Value, string? Lease);

internal sealed record TxnOpDto(string Op, string Key, string? Value, string? Lease, bool? Immutable);

internal sealed record TxnRequestDto(TxnCompareDto[]? Compare, TxnOpDto[]? Success, TxnOpDto[]? Failure);

internal sealed record TxnOpResponseDto(string Op, string Key, bool Found, long CreateRevision, long ModRevision, string? Lease, string? Value);

internal sealed record TxnResponseDto(bool Succeeded, long Revision, TxnOpResponseDto[] Responses);

/// <summary>Source-generated JSON for AOT; snake_case on the wire, matching Turnstile.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(RangeDto))]
[JsonSerializable(typeof(LeaseCreatedDto))]
[JsonSerializable(typeof(LeaseKeepaliveDto))]
[JsonSerializable(typeof(TxnRequestDto))]
[JsonSerializable(typeof(TxnResponseDto))]
internal partial class WireJson : JsonSerializerContext;
