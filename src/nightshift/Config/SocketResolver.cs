namespace Nightshift.Config;

/// <summary>
/// Resolves which Turnstile socket the CLI talks to, kubeconfig-style, from clearest override to broadest
/// default. Precedence, highest first:
/// <list type="number">
///   <item>the explicit <c>--socket</c> flag (a global option on every verb);</item>
///   <item>the <c>NIGHTSHIFT_SOCKET</c> environment variable;</item>
///   <item>the <c>socket</c> setting in the config file (<see cref="ConfigFile"/>);</item>
///   <item>the legacy <c>TURNSTILE_SOCKET</c> environment variable (kept for back-compat);</item>
///   <item>the default <c>~/.turnstile/turnstile.sock</c> (<see cref="Paths.DefaultSocket"/>).</item>
/// </list>
/// The <c>--socket</c> flag is parsed once at dispatch and pinned via <see cref="UseFlag"/> so it reaches
/// the command call sites (which read <see cref="Paths.Socket"/>) and always wins. Every other source is
/// re-read on each <see cref="Current"/> access, so no environment or config change is cached.
/// </summary>
internal static class SocketResolver
{
    private static string? _flagOverride;

    /// <summary>Pins (or clears, when null/empty) the <c>--socket</c> flag for this dispatch.</summary>
    public static void UseFlag(string? flag) => _flagOverride = string.IsNullOrWhiteSpace(flag) ? null : flag.Trim();

    /// <summary>The socket to connect to, honouring the pinned flag then env/config/default.</summary>
    public static string Current => Resolve(_flagOverride);

    /// <summary>Resolves the socket for an explicit flag value (null when unset), applying full precedence.</summary>
    public static string Resolve(string? flag) =>
        ResolveFrom(
            flag,
            Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET"),
            ConfigFile.Load()?.Socket,
            Environment.GetEnvironmentVariable("TURNSTILE_SOCKET"));

    /// <summary>
    /// The pure precedence core over already-read sources — flag &gt; <c>NIGHTSHIFT_SOCKET</c> &gt; config
    /// &gt; <c>TURNSTILE_SOCKET</c> &gt; default. Blank (empty or whitespace-only) values count as unset, so
    /// a stray space never pins an unusable socket path and overrides a valid lower-precedence source.
    /// </summary>
    public static string ResolveFrom(string? flag, string? nightshiftSocket, string? configSocket, string? turnstileSocket)
    {
        if (!string.IsNullOrWhiteSpace(flag))
        {
            return flag.Trim();
        }

        if (!string.IsNullOrWhiteSpace(nightshiftSocket))
        {
            return nightshiftSocket.Trim();
        }

        if (!string.IsNullOrWhiteSpace(configSocket))
        {
            return configSocket.Trim();
        }

        if (!string.IsNullOrWhiteSpace(turnstileSocket))
        {
            return turnstileSocket.Trim();
        }

        return Paths.DefaultSocket;
    }
}
