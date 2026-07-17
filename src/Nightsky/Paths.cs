namespace Nightsky;

internal static class Paths
{
    public static string ResolveSocket(string? optionSocket)
    {
        if (!string.IsNullOrWhiteSpace(optionSocket))
        {
            return optionSocket.Trim();
        }

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET")))
        {
            return Environment.GetEnvironmentVariable("NIGHTSHIFT_SOCKET")!.Trim();
        }

        // Nightsky is standalone and read-only, so it intentionally does not read Nightshift's config file.
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("TURNSTILE_SOCKET")))
        {
            return Environment.GetEnvironmentVariable("TURNSTILE_SOCKET")!.Trim();
        }

        return DefaultSocket;
    }

    public static string DefaultSocket => Path.Combine(TurnstileHome, "turnstile.sock");

    private static string TurnstileHome =>
        Environment.GetEnvironmentVariable("TURNSTILE_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".turnstile");
}
