namespace Turnstile.Storage;

/// <summary>
/// The materialized state of a live key: a view over the latest log row.
/// <see cref="CreateRevision"/> == 0 means the key does not exist.
/// </summary>
public sealed record KeyState(
    string Key,
    long CreateRevision,
    long ModRevision,
    string? Lease,
    bool Immutable,
    byte[]? Value);

/// <summary>Outcome of a write. Maps to HTTP status in the server layer.</summary>
public enum WriteStatus
{
    /// <summary>Key created (HTTP 201).</summary>
    Created,

    /// <summary>Key updated (HTTP 200).</summary>
    Ok,

    /// <summary>Key deleted (HTTP 200).</summary>
    Deleted,

    /// <summary>Create attempted but the key already exists (HTTP 409).</summary>
    Exists,

    /// <summary>Update/delete attempted on a key that does not exist (HTTP 404).</summary>
    NotFound,

    /// <summary>Conditional write required but no precondition supplied (HTTP 428).</summary>
    PreconditionRequired,

    /// <summary>Precondition supplied but stale (HTTP 412).</summary>
    PreconditionFailed,

    /// <summary>Mutation attempted on an immutable key (HTTP 409).</summary>
    Immutable,
}

/// <summary>
/// The result of a write. <see cref="Current"/> carries the present key state on a failed
/// precondition so the caller learns the revision it must present next.
/// </summary>
public sealed record WriteResult(WriteStatus Status, long Revision, KeyState? Current)
{
    public bool Succeeded => Status is WriteStatus.Created or WriteStatus.Ok or WriteStatus.Deleted;
}

/// <summary>The latest log row for a key, as read inside a write transaction.</summary>
internal readonly record struct LatestRow(
    long Id,
    bool Deleted,
    bool Immutable,
    long CreateRev,
    string? Lease,
    byte[]? Value);
