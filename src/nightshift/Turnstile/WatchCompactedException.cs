namespace Nightshift.Turnstile;

/// <summary>
/// The watched revision has been compacted away (HTTP 410 Gone): controllers must re-range and resume
/// from a fresh revision floor.
/// </summary>
internal sealed class WatchCompactedException : Exception
{
    public WatchCompactedException(string prefix, long fromExclusive, long? compactRevision = null)
        : base(FormatMessage(prefix, fromExclusive, compactRevision))
    {
        Prefix = prefix;
        FromExclusive = fromExclusive;
        CompactRevision = compactRevision;
    }

    public string Prefix { get; }

    public long FromExclusive { get; }

    public long? CompactRevision { get; }

    private static string FormatMessage(string prefix, long fromExclusive, long? compactRevision)
        => compactRevision is long c
            ? $"watch from revision {fromExclusive} under '{prefix}' was compacted (compact_revision={c})"
            : $"watch from revision {fromExclusive} under '{prefix}' was compacted";
}
