namespace Nightshift;

/// <summary>Default locations, overridable by environment. The socket default matches Turnstile's.</summary>
internal static class Paths
{
    public static string Socket =>
        Environment.GetEnvironmentVariable("TURNSTILE_SOCKET")
        ?? Path.Combine(TurnstileHome, "turnstile.sock");

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
