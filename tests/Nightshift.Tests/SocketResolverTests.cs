namespace Nightshift.Tests;

using Nightshift;
using Nightshift.Config;
using Xunit;

/// <summary>
/// The kubeconfig-style socket precedence: flag &gt; NIGHTSHIFT_SOCKET &gt; config file &gt; TURNSTILE_SOCKET
/// &gt; default. Exercised through the pure <see cref="SocketResolver.ResolveFrom"/> seam so the order is
/// pinned without mutating process-wide environment state.
/// </summary>
public class SocketResolverTests
{
    [Fact]
    public void Flag_OutranksEveryOtherSource()
        => Assert.Equal("/flag.sock", SocketResolver.ResolveFrom("/flag.sock", "/env.sock", "/config.sock", "/turnstile.sock"));

    [Fact]
    public void NightshiftEnv_OutranksConfigAndTurnstile()
        => Assert.Equal("/env.sock", SocketResolver.ResolveFrom(flag: null, "/env.sock", "/config.sock", "/turnstile.sock"));

    [Fact]
    public void Config_OutranksTurnstile()
        => Assert.Equal("/config.sock", SocketResolver.ResolveFrom(flag: null, nightshiftSocket: null, "/config.sock", "/turnstile.sock"));

    [Fact]
    public void Turnstile_UsedWhenNoFlagEnvOrConfig()
        => Assert.Equal("/turnstile.sock", SocketResolver.ResolveFrom(flag: null, nightshiftSocket: null, configSocket: null, "/turnstile.sock"));

    [Fact]
    public void Default_UsedWhenNothingSet()
        => Assert.Equal(Paths.DefaultSocket, SocketResolver.ResolveFrom(flag: null, nightshiftSocket: null, configSocket: null, turnstileSocket: null));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void EmptyOrNullSourcesAreSkipped(string? blank)
    {
        // A blank flag/env must not pin an empty path — resolution falls through to the next real source.
        Assert.Equal("/turnstile.sock", SocketResolver.ResolveFrom(blank, blank, blank, "/turnstile.sock"));
        Assert.Equal(Paths.DefaultSocket, SocketResolver.ResolveFrom(blank, blank, blank, blank));
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" \n ")]
    public void WhitespaceOnlySourcesAreSkipped(string blank)
    {
        // A whitespace-only value (e.g. NIGHTSHIFT_SOCKET='   ') must count as unset so it never overrides
        // a valid lower-precedence source and breaks connectivity.
        Assert.Equal("/turnstile.sock", SocketResolver.ResolveFrom(blank, blank, blank, "/turnstile.sock"));
        Assert.Equal("/config.sock", SocketResolver.ResolveFrom(blank, blank, "/config.sock", "/turnstile.sock"));
        Assert.Equal(Paths.DefaultSocket, SocketResolver.ResolveFrom(blank, blank, blank, blank));
    }

    [Fact]
    public void AcceptedValuesAreTrimmed()
        => Assert.Equal("/padded.sock", SocketResolver.ResolveFrom("  /padded.sock  ", null, null, null));

    [Fact]
    public void FullChain_FallsThroughInOrder()
    {
        // Peel one source off at a time; each step surfaces the next-highest precedence.
        Assert.Equal("/flag.sock", SocketResolver.ResolveFrom("/flag.sock", "/env.sock", "/config.sock", "/turnstile.sock"));
        Assert.Equal("/env.sock", SocketResolver.ResolveFrom(null, "/env.sock", "/config.sock", "/turnstile.sock"));
        Assert.Equal("/config.sock", SocketResolver.ResolveFrom(null, null, "/config.sock", "/turnstile.sock"));
        Assert.Equal("/turnstile.sock", SocketResolver.ResolveFrom(null, null, null, "/turnstile.sock"));
        Assert.Equal(Paths.DefaultSocket, SocketResolver.ResolveFrom(null, null, null, null));
    }
}
