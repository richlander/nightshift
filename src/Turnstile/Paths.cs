namespace Turnstile;

/// <summary>Default filesystem locations, overridable by environment for tests and multi-instance use.</summary>
internal static class Paths
{
    public static string Home =>
        Environment.GetEnvironmentVariable("TURNSTILE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".turnstile");

    public static string DefaultSocket =>
        Environment.GetEnvironmentVariable("TURNSTILE_SOCKET") ?? Path.Combine(Home, "turnstile.sock");

    public static string DefaultDb =>
        Environment.GetEnvironmentVariable("TURNSTILE_DB") ?? Path.Combine(Home, "turnstile.db");
}
