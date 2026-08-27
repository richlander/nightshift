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
///
/// The same one rule also rejects a control character (U+0000 first): an alias is destined for
/// <c>ProcessStartInfo.ArgumentList</c>, where a NUL cannot survive the OS argument boundary — it
/// truncates the argument on Unix and throws or corrupts on Windows — so a control-bearing alias must be
/// refused before it is ever handed to ssh, not left to fail outside the unavailable contract mid-sweep.
///
/// And it rejects an unpaired UTF-16 surrogate, for the same reason one char up: such a value is not a
/// Unicode scalar and has no UTF-8 encoding, so the target key it would be encoded into collapses onto
/// U+FFFD's — an unrepresentable alias silently taking on the identity of a different one. Rejecting it
/// here keeps the alias from ever reaching key construction or ssh; a valid surrogate pair is a real
/// character and passes.
/// </remarks>
internal static class HostTarget
{
    /// <summary>Returns null when the value is usable, or the usage message explaining why it is not.</summary>
    /// <remarks>
    /// The message quotes the value back, and the value is whatever was typed on a command line, so it is
    /// escaped on the way in: this string is printed to a terminal by both the parser's error path and
    /// the command's own, and an alias carrying an ESC sequence should be reported, not executed.
    /// </remarks>
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
            return $"--host requires a hostname or ssh-config alias; '{DisplayText.Safe(host)}' looks like an option.";
        }

        if (host.Any(char.IsWhiteSpace))
        {
            return $"--host requires a hostname or ssh-config alias; '{DisplayText.Safe(host)}' contains whitespace.";
        }

        // A control character — U+0000 above all — cannot be carried through an OS process argument
        // intact, so an alias containing one must never reach ssh. On Unix a NUL truncates the argument at
        // the null terminator (an alias `a\0b` silently becomes `a`, so a sweep contacts the wrong host);
        // on Windows the same NUL, and other C0/C1 controls, throw or corrupt inside process construction —
        // in every case outside the HistoryUnavailable/PARTIAL contract the caller relies on. None is a
        // legitimate hostname or ssh-config alias, so reject them here, the single gate the CLI, the strict
        // persisted-target load and the pre-scan defence all share. Whitespace controls (tab, newline) are
        // already handled above; this catches NUL, DEL and the remaining non-whitespace control codes.
        if (host.Any(char.IsControl))
        {
            return $"--host requires a hostname or ssh-config alias; '{DisplayText.Safe(host)}' contains a control character that cannot be passed to a process.";
        }

        // An unpaired UTF-16 surrogate is not a Unicode scalar value, so it has no UTF-8 encoding: the
        // default encoder replaces each one with the U+FFFD bytes, which collapses an unrepresentable
        // alias onto the identity of a different one — `\uD800` and a literal U+FFFD both persist under the
        // same target key, so the alias dials, and is remembered as, a host it is not. `char.IsControl`
        // does not catch a lone surrogate, so it is checked here in its own right. A valid surrogate pair
        // — an astral-plane character such as an emoji — is a real scalar and passes.
        if (HasUnpairedSurrogate(host))
        {
            return $"--host requires a hostname or ssh-config alias; '{DisplayText.Safe(host)}' contains an unpaired UTF-16 surrogate that cannot be represented as a target.";
        }

        return null;
    }

    /// <summary>
    /// Whether a value contains a lone UTF-16 surrogate — a high surrogate not followed by a low, or a low
    /// surrogate not preceded by a high. Such a value is not well-formed UTF-16 and has no Unicode scalar
    /// (and so no UTF-8 encoding) for the stray half, which is exactly what lets it collapse onto U+FFFD.
    /// A matched high/low pair is a single supplementary scalar and is accepted.
    /// </summary>
    private static bool HasUnpairedSurrogate(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c))
            {
                // A high surrogate must be immediately followed by a low surrogate; skip the pair.
                if (i + 1 >= value.Length || !char.IsLowSurrogate(value[i + 1]))
                {
                    return true;
                }

                i++;
            }
            else if (char.IsLowSurrogate(c))
            {
                // A low surrogate reached on its own — any valid pair would have skipped its low half.
                return true;
            }
        }

        return false;
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
