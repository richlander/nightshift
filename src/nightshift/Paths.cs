namespace Nightshift;

using Nightshift.Config;

/// <summary>Default locations, overridable by environment. The socket default matches Turnstile's.</summary>
internal static class Paths
{
    /// <summary>
    /// The Turnstile socket every command connects through. Resolved via <see cref="SocketResolver"/>
    /// (flag &gt; <c>NIGHTSHIFT_SOCKET</c> &gt; config &gt; <c>TURNSTILE_SOCKET</c> &gt; default), so the
    /// global <c>--socket</c> flag always wins at the call sites without threading it through each verb.
    /// </summary>
    public static string Socket => SocketResolver.Current;

    /// <summary>The bare default socket location (<c>~/.turnstile/turnstile.sock</c>), matching Turnstile's.</summary>
    public static string DefaultSocket => Path.Combine(TurnstileHome, "turnstile.sock");

    private static string TurnstileHome =>
        Environment.GetEnvironmentVariable("TURNSTILE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".turnstile");

    /// <summary>
    /// Per-user runtime dir for the session/lease file. Prefers <c>$XDG_RUNTIME_DIR</c> (tmpfs, 0700);
    /// falls back to <c>~/.nightshift/run</c> where XDG is unset (e.g. macOS).
    /// </summary>
    public static string RuntimeDir
    {
        get
        {
            string? xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            string dir = !string.IsNullOrEmpty(xdg)
                ? Path.Combine(xdg, "nightshift")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nightshift", "run");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
