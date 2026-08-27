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
    public void HumanLabelAndKindDistinguishTheLocalMachineFromAnAliasNamedLocal()
    {
        // The output contracts (fleet list/add/retire/unknown) read these to preserve the target kind, so a
        // consumer can derive whether to pass --local or --host local. The real local machine labels and
        // tags as local with no alias; an ssh alias literally named local labels as `host local` and tags
        // host with the alias carried alongside.
        Assert.Equal("local", TargetId.Local.HumanLabel);
        Assert.Equal("local", TargetId.Local.KindTag);

        TargetId aliasLocal = TargetId.ForHost("local");
        Assert.Equal("host local", aliasLocal.HumanLabel);
        Assert.Equal("host", aliasLocal.KindTag);
        Assert.Equal("local", aliasLocal.Display);

        Assert.Equal("host fernie", TargetId.ForHost("fernie").HumanLabel);
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

    [Theory]
    [InlineData("RA")]     // payload length ≡ 1 mod 4 — impossible base64, would crash Convert
    [InlineData("R_w")]    // canonical base64url of byte 0xFF — decodes, but not valid UTF-8
    [InlineData("RQR")]    // noncanonical: unused trailing bits set, re-encodes to a different payload
    [InlineData("R=")]     // padding is never part of a payload
    [InlineData("R with space")]
    public void ACorruptedRemoteKeyIsRejectedRatherThanCrashingOrResolvingToAnAlias(string key)
    {
        // Validation is total and canonical, not just an alphabet check: every one of these would either
        // throw inside Display or resolve to an alias different from the one it was written as. All are
        // rejected, and none can be turned into a TargetId that could then throw.
        Assert.False(TargetId.IsValidKey(key));
        Assert.False(TargetId.TryFromKey(key, out _));
        Assert.Throws<ArgumentException>(() => TargetId.FromKey(key));
        Assert.Null(TargetId.HostOfComposite(key + "|%3"));
    }

    [Theory]
    [InlineData("fernie")]
    [InlineData("local")]
    [InlineData("a|b")]
    [InlineData("café-日本語")]
    [InlineData("\uD83D\uDE80 rocket")]
    [InlineData("x")]
    public void EveryKeyForHostMintsRoundTripsAndDisplaysWithoutThrowing(string host)
    {
        // The bijection the corrupted-key rejection depends on: a key this scheme produces is always
        // valid, and always displays back the exact alias it encoded, for any UTF-8 input including
        // multibyte and astral characters.
        TargetId id = TargetId.ForHost(host);

        Assert.True(TargetId.IsValidKey(id.Key));
        Assert.True(TargetId.TryFromKey(id.Key, out TargetId parsed));
        Assert.Equal(host, parsed.Display);
        Assert.Equal(host, TargetId.FromKey(id.Key).Display);
    }
}
