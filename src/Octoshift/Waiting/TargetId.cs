namespace Octoshift.Waiting;

using System.Text;

/// <summary>
/// A collection target's stable identity: the local machine, or a remote ssh alias. Used as the key for
/// everything remembered per host and per pane, so it must never confuse one target for another.
/// </summary>
/// <remarks>
/// The obvious key — the alias, with a <c>"local"</c> sentinel for the null host — has two collisions an
/// ssh alias can trigger on purpose. An alias literally named <c>local</c> is indistinguishable from the
/// real local machine; and an alias containing <c>|</c> breaks any <c>host|pane</c> composite key, so one
/// window's registration can be attributed to another. Both are input: <c>--host local</c> and
/// <c>--host a|b</c> are things ssh accepts.
///
/// So the identity is tagged and encoded, never the raw alias. Local is the single tag <c>L</c>; a remote
/// is <c>R</c> followed by the base64url of the alias's UTF-8 bytes — an alphabet of
/// <c>A-Za-z0-9-_</c> only, so a remote key can never equal <c>L</c> and can never contain <c>|</c>,
/// <c>:</c> or any byte of the raw alias. Composite keys join a target key to a pane or window id with
/// <c>|</c>, which neither side can contain, and the raw alias is recovered only by decoding, never by
/// parsing a key.
/// </remarks>
internal readonly record struct TargetId
{
    private const string LocalKey = "L";
    private const char RemoteTag = 'R';

    /// <summary>The opaque, collision-free key: <c>L</c>, or <c>R</c> + base64url(alias).</summary>
    public string Key { get; }

    private TargetId(string key) => Key = key;

    /// <summary>The local machine.</summary>
    public static TargetId Local { get; } = new(LocalKey);

    /// <summary>The identity of a collection target: null is local, anything else is a remote alias.</summary>
    public static TargetId ForHost(string? host)
        => host is null ? Local : new(RemoteTag + Base64Url.Encode(Encoding.UTF8.GetBytes(host)));

    /// <summary>True for the local machine, never for a remote alias — even one named <c>local</c>.</summary>
    public bool IsLocal => Key == LocalKey;

    /// <summary>Wraps a key this scheme produced (e.g. a <see cref="PaneHistory.KnownHosts"/> entry) so it
    /// can be shown; the caller supplies a key it knows to be valid.</summary>
    public static TargetId FromKey(string key) => new(key);

    /// <summary>The alias for display, decoded from the key. <c>local</c> only for the real local machine.</summary>
    public string Display => IsLocal ? "local" : Encoding.UTF8.GetString(Base64Url.Decode(Key[1..]));

    /// <summary>The composite key for a pane or window id on this target, joined by a byte neither can contain.</summary>
    public string ComposeWith(string id) => Key + "|" + id;

    /// <summary>
    /// Whether a string is a well-formed target key. Anything else — including every key an older,
    /// differently-shaped history file wrote — is rejected so its entries are dropped rather than
    /// misattributed to a target this scheme would never have produced.
    /// </summary>
    public static bool IsValidKey(string key)
        => key == LocalKey || (key.Length > 1 && key[0] == RemoteTag && Base64Url.IsValid(key[1..]));

    /// <summary>The target portion of a composite <c>key|id</c>, or null when the string is not one this
    /// scheme wrote. A remote key contains no <c>|</c>, so the first <c>|</c> is always the composite's
    /// separator.</summary>
    public static TargetId? HostOfComposite(string composite)
    {
        int bar = composite.IndexOf('|', StringComparison.Ordinal);
        if (bar <= 0)
        {
            return null;
        }

        string host = composite[..bar];
        return IsValidKey(host) ? new TargetId(host) : null;
    }

    /// <summary>The pane or window id portion of a composite <c>key|id</c>, or null when it is not one.</summary>
    public static string? IdOfComposite(string composite)
    {
        int bar = composite.IndexOf('|', StringComparison.Ordinal);
        return bar > 0 && bar < composite.Length - 1 ? composite[(bar + 1)..] : null;
    }
}

/// <summary>URL-safe base64 with no padding, over the alphabet <c>A-Za-z0-9-_</c>.</summary>
internal static class Base64Url
{
    public static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    public static byte[] Decode(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(padded);
    }

    public static bool IsValid(string value)
    {
        if (value.Length == 0)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_')
            {
                return false;
            }
        }

        return true;
    }
}
