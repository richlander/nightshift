namespace Octoshift.Tests;

using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The one <c>--host</c> validation rule the CLI, the strict persisted-target load and the pre-scan
/// defence all share (#173). A value that cannot be carried through <c>ProcessStartInfo.ArgumentList</c>
/// intact — U+0000 above all — must be rejected here rather than reaching ssh, where a NUL truncates the
/// argument on Unix and throws inside process construction on Windows, in every case outside the
/// HistoryUnavailable/PARTIAL contract.
/// </summary>
public sealed class HostTargetTests
{
    [Theory]
    [InlineData("fernie")]
    [InlineData("build-1")]
    [InlineData("web-2.example.com")]
    [InlineData("rich@web-2.example.com")]
    [InlineData("münchen.example.com")]  // an internationalised hostname is ordinary text, not a control char
    public void Validate_AcceptsOrdinaryAliases(string host)
    {
        Assert.Null(HostTarget.Validate(host));
    }

    [Theory]
    [InlineData("\u0000")]        // NUL, the reported alias
    [InlineData("a\u0000b")]      // NUL embedded mid-alias
    [InlineData("\u0001")]        // SOH
    [InlineData("\u001b")]        // ESC — a terminal control sequence has no place in an alias either
    [InlineData("\u007f")]        // DEL
    [InlineData("\u009c")]        // a C1 control
    [InlineData("host\u0007")]    // BEL after otherwise-ordinary text
    public void Validate_RejectsControlCharactersWithAProcessSafetyMessage(string host)
    {
        string? message = HostTarget.Validate(host);

        Assert.NotNull(message);
        Assert.Contains("control character", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_KeepsTheOptionAndWhitespaceMessagesIntact()
    {
        // The control-character rule is additive: the existing option-injection and whitespace diagnostics
        // are unchanged, so the messages a consumer already branches on still appear.
        Assert.Contains("looks like an option", HostTarget.Validate("-V"), StringComparison.Ordinal);
        Assert.Contains("contains whitespace", HostTarget.Validate("two words"), StringComparison.Ordinal);
        Assert.Contains("empty", HostTarget.Validate("   "), StringComparison.Ordinal);
    }

    [Fact]
    public void TargetId_TheRAAKeyDecodesToNulAndTheSharedRuleRejectsIt()
    {
        // The exact mapping from the report: the canonical target key RAA round-trips through base64url — so
        // IsValidKey accepts it — yet decodes to the one-character NUL alias, which the shared validation
        // rule refuses. Canonical UTF-8/base64url encoding is intact; the alias is caught at Validate, not
        // by mangling the key.
        TargetId nul = TargetId.ForHost("\u0000");
        Assert.Equal("RAA", nul.Key);
        Assert.True(TargetId.IsValidKey("RAA"));
        Assert.Equal("\u0000", nul.Display);
        Assert.NotNull(HostTarget.Validate(nul.Display));
    }

    [Fact]
    public void Validate_RejectsUnpairedSurrogates()
    {
        // The lone-surrogate strings are built here rather than via InlineData: xUnit serialises theory
        // arguments, and a lone surrogate does not survive that round trip — it is silently replaced with
        // U+FFFD, which would defeat the very case under test. Constructed in-body, each carries a real
        // unpaired surrogate.
        string[] hosts =
        [
            "\ud800",        // a lone high surrogate on its own
            "\udbff",        // the top of the high-surrogate range
            "\udc00",        // a lone low surrogate on its own
            "\udfff",        // the top of the low-surrogate range
            "host\ud800",    // a trailing lone high surrogate
            "\udc00host",    // a leading lone low surrogate
            "a\ud800b",      // a lone high surrogate embedded between ordinary text
            "\ud800\ud800",  // two highs — the first is not paired with a low
            "\udc00\udc00",  // two lows — neither is preceded by a high
        ];

        foreach (string host in hosts)
        {
            string? message = HostTarget.Validate(host);
            Assert.NotNull(message);
            Assert.Contains("unpaired UTF-16 surrogate", message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("\ud800\udc00")]         // U+10000, the first supplementary scalar
    [InlineData("\ud83d\ude00")]         // U+1F600 😀, a well-formed pair
    [InlineData("host-\ud83d\ude00")]    // a valid pair embedded in an otherwise ordinary alias
    public void Validate_AcceptsValidSurrogatePairs(string host)
    {
        // A matched high/low pair is a single Unicode scalar with a real UTF-8 encoding, so it is accepted:
        // the rule rejects only the unpaired halves, not every non-BMP character. A valid pair is a real
        // scalar, so it survives InlineData serialisation intact.
        Assert.Null(HostTarget.Validate(host));
    }

    [Fact]
    public void TargetId_ForHost_IsNonLossyForALoneSurrogateRatherThanCollidingWithReplacement()
    {
        // The gap this closes: the default UTF-8 encoder replaces a lone surrogate with the U+FFFD bytes,
        // so `\uD800` and a literal U+FFFD would mint the same target key — an unrepresentable alias taking
        // a different host's identity. Key construction is now non-lossy: a lone surrogate fails fast with
        // an ArgumentException instead of colliding.
        Assert.Throws<ArgumentException>(() => TargetId.ForHost("\ud800"));
        Assert.Throws<ArgumentException>(() => TargetId.ForHost("\udc00"));

        // A real U+FFFD is a valid scalar and gets its own key; a valid surrogate pair does too, and the two
        // are distinct — no path collapses one alias onto another's identity.
        string replacementKey = TargetId.ForHost("\ufffd").Key;
        string pairKey = TargetId.ForHost("\ud83d\ude00").Key;
        Assert.NotEqual(replacementKey, pairKey);
        Assert.True(TargetId.IsValidKey(replacementKey));
        Assert.True(TargetId.IsValidKey(pairKey));
        Assert.Equal("\ufffd", TargetId.FromKey(replacementKey).Display);
        Assert.Equal("\ud83d\ude00", TargetId.FromKey(pairKey).Display);
    }
}
