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

    /// <summary>
    /// The identity of a collection target: null is local, anything else is a remote alias. The alias is
    /// encoded strictly (see <see cref="Base64Url.EncodeText"/>): an alias that is not well-formed
    /// UTF-16 — a lone surrogate — has no UTF-8 encoding and would otherwise collapse onto U+FFFD's key, so
    /// key construction fails fast rather than minting a colliding identity. Callers validate first with
    /// <see cref="HostTarget.Validate"/>, which rejects the same values, so in practice only a bypassed
    /// caller ever trips the guard.
    /// </summary>
    public static TargetId ForHost(string? host)
        => host is null ? Local : new(RemoteTag + Base64Url.EncodeText(host));

    /// <summary>True for the local machine, never for a remote alias — even one named <c>local</c>.</summary>
    public bool IsLocal => Key == LocalKey;

    /// <summary>
    /// The target kind, as a machine-readable tag: <c>local</c> for the real local machine, <c>host</c>
    /// for any ssh alias. This is the distinction <see cref="Display"/> deliberately collapses — the local
    /// machine and an alias literally named <c>local</c> both display <c>local</c> — so every output
    /// contract that a consumer reads to decide between <c>--local</c> and <c>--host</c> must carry the tag
    /// beside the alias rather than the alias alone.
    /// </summary>
    public string KindTag => IsLocal ? "local" : "host";

    /// <summary>
    /// An unambiguous human label that preserves the target kind: <c>local</c> for the real local machine,
    /// and <c>host &lt;alias&gt;</c> for every ssh alias — including one literally named <c>local</c>,
    /// which renders <c>host local</c> and so can never be read as the local machine. Every fleet surface
    /// (list, add, retire, unknown) labels a target this way, on success and on failure alike, so a reader
    /// can always derive whether it was <c>--local</c> or <c>--host &lt;alias&gt;</c> that named it.
    /// </summary>
    public string HumanLabel => IsLocal ? "local" : $"host {Display}";

    /// <summary>
    /// Wraps a key this scheme produced (e.g. a <see cref="PaneHistory.KnownHosts"/> entry) so it can be
    /// shown, validating it first. A key that <see cref="IsValidKey"/> rejects is not a target this scheme
    /// would ever have minted, so it is a caller error rather than something to display — use <see
    /// cref="TryFromKey"/> at a trust boundary where an untrusted key must fail closed instead.
    /// </summary>
    public static TargetId FromKey(string key)
        => TryFromKey(key, out TargetId id) ? id : throw new ArgumentException($"not a target key: '{key}'", nameof(key));

    /// <summary>Wraps a key only if it is one this scheme produced, so an untrusted key fails closed rather
    /// than becoming a <see cref="TargetId"/> whose <see cref="Display"/> would throw.</summary>
    public static bool TryFromKey(string key, out TargetId id)
    {
        if (IsValidKey(key))
        {
            id = new TargetId(key);
            return true;
        }

        id = default;
        return false;
    }

    /// <summary>The alias for display, decoded from the key. <c>local</c> only for the real local machine.
    /// Total for every key <see cref="IsValidKey"/> accepts — which is every key any instance can hold,
    /// since all of them are minted by <see cref="ForHost"/>, <see cref="TryFromKey"/> or a validated
    /// <see cref="HostOfComposite"/> — so it never throws in practice.</summary>
    public string Display
        => IsLocal ? "local"
            : Base64Url.TryDecodeText(Key[1..], out string alias) ? alias
            : throw new InvalidOperationException($"target key is not canonical: '{Key}'");

    /// <summary>The composite key for a pane or window id on this target, joined by a byte neither can contain.</summary>
    public string ComposeWith(string id) => Key + "|" + id;

    /// <summary>
    /// Whether a string is a well-formed target key. Anything else — an older, differently-shaped history
    /// file's key, or a corrupted one — is rejected so its entries are dropped rather than misattributed
    /// to a target this scheme would never have produced. The check is total and canonical: a remote key's
    /// payload must be the exact base64url this scheme would emit for some UTF-8 alias, so a wrong-length
    /// encoding (<c>RA</c>), a noncanonical one, or bytes that are not valid UTF-8 are all rejected rather
    /// than surviving to crash <see cref="Display"/> or resolve to a different alias than they were written
    /// as.
    /// </summary>
    public static bool IsValidKey(string key)
        => key == LocalKey || (key.Length > 1 && key[0] == RemoteTag && Base64Url.TryDecodeText(key[1..], out _));

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

        return TryFromKey(composite[..bar], out TargetId id) ? id : null;
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
    // Strict: invalid bytes throw rather than being replaced with U+FFFD, so a payload that is not valid
    // UTF-8 is rejected instead of decoding to a mangled alias that differs from what it was written as.
    // The same strictness holds on the encode side (GetBytes): an unpaired surrogate has no UTF-8 encoding
    // and throws rather than being substituted with the U+FFFD bytes.
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static string Encode(byte[] bytes)
        => Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    /// <summary>
    /// Encodes a string's canonical UTF-8 bytes as base64url. Strict on the encode side: a string that is
    /// not well-formed UTF-16 — a lone surrogate — has no UTF-8 encoding, and the default encoder would
    /// silently substitute the U+FFFD bytes, collapsing two distinct aliases onto one identity. Throwing an
    /// <see cref="ArgumentException"/> instead makes target-key construction non-lossy: a caller that has
    /// not validated its input fails fast here rather than minting a colliding key. The catch is narrow —
    /// only the encoder's own <see cref="EncoderFallbackException"/> — so no other failure is masked.
    /// </summary>
    public static string EncodeText(string text)
    {
        try
        {
            return Encode(StrictUtf8.GetBytes(text));
        }
        catch (EncoderFallbackException ex)
        {
            throw new ArgumentException($"value is not well-formed UTF-16 and cannot be encoded as a target key: {ex.Message}", nameof(text), ex);
        }
    }

    /// <summary>
    /// Decodes a payload to the alias it encodes, but only when the payload is exactly the canonical
    /// base64url this class would emit for some UTF-8 string. Returns false — never throws — for anything
    /// else: a wrong-length encoding (a length ≡ 1 mod 4 is impossible base64), a non-alphabet or
    /// malformed one, a <em>noncanonical</em> one (unused trailing bits set, so a different payload decodes
    /// to the same bytes), or bytes that are not valid UTF-8. Requiring <c>Encode(bytes) == payload</c> is
    /// what makes the mapping a bijection, so a corrupted key can never round-trip to a valid-looking
    /// alias.
    /// </summary>
    public static bool TryDecodeText(string payload, out string text)
    {
        text = string.Empty;
        if (!IsAlphabet(payload) || payload.Length % 4 == 1)
        {
            return false;
        }

        byte[] bytes;
        try
        {
            string padded = payload.Replace('-', '+').Replace('_', '/');
            padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
            bytes = Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return false;
        }

        if (!string.Equals(Encode(bytes), payload, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }

        return true;
    }

    private static bool IsAlphabet(string value)
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
