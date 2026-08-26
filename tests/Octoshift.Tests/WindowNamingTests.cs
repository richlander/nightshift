namespace Octoshift.Tests;

using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The window-name suffix rules: applying a suffix replaces any the tool already owns rather than
/// stacking a second one, and stripping spans the whole base name — including bytes a naive <c>.</c>
/// pattern would stop at — while only ever removing a suffix the tool owns.
/// </summary>
public class WindowNamingTests
{
    [Fact]
    public void Apply_ReplacesAnOwnedSuffixRatherThanAccumulating()
    {
        string once = WindowNaming.Apply("pr4448", "blocked");
        Assert.Equal("pr4448-blocked", once);

        // Applying a second owned suffix replaces the first, so repeated rename passes converge rather
        // than growing pr4448-blocked-ready-...; the result equals applying it once to the base.
        string twice = WindowNaming.Apply(once, "ready");
        Assert.Equal("pr4448-ready", twice);
        Assert.Equal(WindowNaming.Apply("pr4448", "ready"), twice);

        // Applying null clears the tool's suffix back to the agent's base name.
        Assert.Equal("pr4448", WindowNaming.Apply(once, null));
    }

    [Fact]
    public void Apply_PreservesTheBaseNameVerbatimIncludingEdgeSpaces()
    {
        Assert.Equal("  pr4448  -blocked", WindowNaming.Apply("  pr4448  ", "blocked"));
        Assert.Equal("  pr4448  ", WindowNaming.Apply("  pr4448  -blocked", null));
    }

    [Fact]
    public void Strip_SpansAnInternalNewlineWhenRemovingAnOwnedSuffix()
    {
        // The base is matched with [\s\S]* / \z, not `.`/$, so a base carrying an internal newline still
        // has exactly its owned terminal suffix removed and nothing else.
        Assert.Equal("a\nb", WindowNaming.Strip("a\nb-blocked"));
        Assert.Equal("a\nb-notowned", WindowNaming.Strip("a\nb-notowned"));
    }

    [Fact]
    public void Strip_LeavesANameWithNoOwnedSuffixUntouched()
    {
        Assert.Equal("pr4448", WindowNaming.Strip("pr4448"));
        Assert.Equal("pr4448-custom", WindowNaming.Strip("pr4448-custom"));
    }
}
