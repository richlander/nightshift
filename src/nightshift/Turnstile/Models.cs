namespace Nightshift.Turnstile;

/// <summary>A live key/value pair as Nightshift sees it: opaque bytes plus the revisions that fence it.</summary>
internal sealed record KvItem(string Key, long CreateRevision, long ModRevision, string? Lease, bool Immutable, byte[] Value)
{
    public string Text => System.Text.Encoding.UTF8.GetString(Value);
}

/// <summary>The outcome of a compare-and-put claim attempt.</summary>
internal sealed record ClaimResult(bool Won, long Revision);

/// <summary>A change observed on a watch stream: the changed key, whether it was a deletion, and its revision.</summary>
internal sealed record WatchSignal(string Key, bool Deleted, long Revision);
