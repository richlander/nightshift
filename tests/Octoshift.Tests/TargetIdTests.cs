namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The target identity and the composite keys built from it. The properties that matter are collision and
/// parse safety: the local machine and an ssh alias literally named <c>local</c> must never share a key,
/// and an alias containing the composite separator must not let one window's memory be read as another's.
/// </summary>
public class TargetIdTests
{
    private static TmuxPane Pane(string? host, string paneId, string windowName = "w")
        => new()
        {
            PaneId = paneId,
            Target = "s:1",
            Host = host,
            WindowName = windowName,
            SessionAttached = true,
        };

    [Fact]
    public void ActualLocalAndAnAliasNamedLocalAreDistinct()
    {
        TargetId local = TargetId.Local;
        TargetId aliasLocal = TargetId.ForHost("local");

        Assert.NotEqual(local.Key, aliasLocal.Key);
        Assert.True(local.IsLocal);
        Assert.False(aliasLocal.IsLocal);

        // Both display "local" to a reader — the collision this exists to prevent is on the key, not the
        // label — but their keys, and so their history, are separate.
        Assert.Equal("local", local.Display);
        Assert.Equal("local", aliasLocal.Display);
    }

    [Fact]
    public void AnAliasContainingTheSeparatorRoundTripsAndDoesNotBreakACompositeKey()
    {
        TargetId id = TargetId.ForHost("a|b");

        Assert.DoesNotContain('|', id.Key);
        Assert.Equal("a|b", id.Display);

        string composite = id.ComposeWith("%3");
        Assert.Equal(id.Key, TargetId.HostOfComposite(composite)!.Value.Key);
        Assert.Equal("%3", TargetId.IdOfComposite(composite));
        Assert.Equal("a|b", TargetId.HostOfComposite(composite)!.Value.Display);
    }

    [Fact]
    public void SamePaneIdOnLocalAndAnAliasNamedLocalHaveDistinctClaimKeys()
    {
        string localKey = Claim.Key(Pane(null, "%3"));
        string aliasKey = Claim.Key(Pane("local", "%3"));

        Assert.NotEqual(localKey, aliasKey);
    }

    [Fact]
    public void AKeyThisSchemeNeverWroteIsRejected()
    {
        // The raw shapes an older history file used: a bare alias, the null->local sentinel, a raw
        // host|pane composite. None is a valid target key, so its entries are dropped rather than
        // misattributed.
        Assert.False(TargetId.IsValidKey("fernie"));
        Assert.False(TargetId.IsValidKey("local"));
        Assert.False(TargetId.IsValidKey("R"));
        Assert.False(TargetId.IsValidKey("R!"));
        Assert.Null(TargetId.HostOfComposite("fernie|%3"));
        Assert.Null(TargetId.HostOfComposite("local|%3"));

        // The shapes it does write.
        Assert.True(TargetId.IsValidKey(TargetId.Local.Key));
        Assert.True(TargetId.IsValidKey(TargetId.ForHost("fernie").Key));
    }
}
