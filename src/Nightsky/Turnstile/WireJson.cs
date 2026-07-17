namespace Nightsky.Turnstile;

using System.Text.Json.Serialization;

internal sealed record StatusDto(long CurrentRevision, long DbSizeBytes, string Socket);

internal sealed record RangeItemDto(string Key, long CreateRevision, long ModRevision, string? Lease, bool Immutable, string? Value);

internal sealed record RangeDto(long Revision, RangeItemDto[] Kvs);

internal sealed record WatchPutEventDto(string Key, long CreateRevision, long ModRevision, string? Lease, string? Value);

internal sealed record WatchDeleteEventDto(string Key, long ModRevision, string? PrevValue);

internal sealed record WatchSyncDto(long Revision);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(RangeDto))]
[JsonSerializable(typeof(WatchPutEventDto))]
[JsonSerializable(typeof(WatchDeleteEventDto))]
[JsonSerializable(typeof(WatchSyncDto))]
internal partial class WireJson : JsonSerializerContext;
