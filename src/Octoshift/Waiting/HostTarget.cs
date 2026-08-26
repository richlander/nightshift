namespace Octoshift.Waiting;

/// <summary>
/// Validation and normalisation for the repeatable <c>--host</c> values.
/// </summary>
/// <remarks>
/// A <c>--host</c> value becomes an <c>ssh</c> argument, so a value that looks like an option is not a
/// hostname at all: <c>--host=-V</c> asks ssh for its version, which succeeds with no output and reads as
/// a quiet fleet. Bare <c>--host --json</c> is the same failure from the other end — the flag is consumed
/// as the hostname and the sweep silently loses its output mode. Both are usage errors, and the check
/// belongs here so the parser and the command enforce one rule rather than two.
/// </remarks>
internal static class HostTarget
{
    /// <summary>Returns null when the value is usable, or the usage message explaining why it is not.</summary>
    public static string? Validate(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "--host requires a hostname or ssh-config alias; the value is empty.";
        }

        // Only a leading dash is rejected: aliases like `build-1` and `web-2.example.com` are ordinary,
        // and anything ssh would read as an option is not.
        if (host.StartsWith('-'))
        {
            return $"--host requires a hostname or ssh-config alias; '{host}' looks like an option.";
        }

        if (host.Any(char.IsWhiteSpace))
        {
            return $"--host requires a hostname or ssh-config alias; '{host}' contains whitespace.";
        }

        return null;
    }

    /// <summary>
    /// Drops repeats while keeping first-seen order, so naming an alias twice costs one connection and
    /// produces one row rather than two of each. Ordinal, because ssh matches config aliases literally.
    /// </summary>
    public static IReadOnlyList<string> Distinct(IReadOnlyList<string> hosts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var ordered = new List<string>(hosts.Count);
        foreach (string host in hosts)
        {
            if (seen.Add(host))
            {
                ordered.Add(host);
            }
        }

        return ordered;
    }
}
