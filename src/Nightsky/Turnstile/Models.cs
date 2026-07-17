namespace Nightsky.Turnstile;

internal sealed record KvItem(string Key, long CreateRevision, long ModRevision, string? Lease, byte[] Value)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Value);
}

internal sealed record WatchSignal(string Key, bool Deleted, long Revision);

internal sealed class WatchCompactedException : Exception
{
    public WatchCompactedException()
        : base("watch cursor is compacted; snapshot again and re-establish watch")
    {
    }
}
