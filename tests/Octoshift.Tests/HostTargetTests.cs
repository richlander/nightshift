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
}
