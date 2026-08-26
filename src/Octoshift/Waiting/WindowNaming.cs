namespace Octoshift.Waiting;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Keeps the tmux window name honest about what the tool can see.
/// </summary>
/// <remarks>
/// The suffix convention is sound and its maintenance is not: an agent sets <c>-blocked</c> on becoming
/// blocked and must remember to clear it later, which is a wall-clock obligation of exactly the kind
/// agents reliably miss. Measured across one fleet, six windows carried <c>-blocked</c> and three of
/// them had no prompt open at all — so the suffix was worse than absent, because it was believed.
///
/// Renaming a window is not talking to an agent. It edits tmux metadata in the operator's own view: it
/// cannot reach an agent's input, cannot be consumed as a prompt, and is idempotent. That places it on
/// the safe side of the line that <c>send-keys</c> sits on the far side of.
/// </remarks>
internal static partial class WindowNaming
{
    /// <summary>Every suffix the tool owns. Any of these is stripped before a new one is applied.</summary>
    private static readonly string[] Owned = ["blocked", "conflict", "merged", "ready", "stale", "ask", "follows"];

    /// <summary>The suffix a verdict earns, or null when the window should carry none.</summary>
    /// <remarks>
    /// Deliberately sparse. A name is read at a glance and believed, so it carries only states that are
    /// unambiguous and worth acting on; everything else leaves the base name clean rather than crowding
    /// the status bar with detail the row already gives.
    /// </remarks>
    internal static string? SuffixFor(WaitingVerdict verdict, Claim claim = default)
    {
        // A follower is second-class and must stay visible as such. The status bar is where the operator
        // notices which window to talk to, so the standing belongs there rather than only in a report.
        if (claim.IsFollower)
        {
            return "follows";
        }

        // A low-confidence verdict must not be published as a fact in the one place that is read without
        // context. The row can say "probably"; a window name cannot.
        if (verdict.Assurance.Level == Confidence.Low)
        {
            return null;
        }

        return verdict.State switch
        {
            WaitingState.NeedsOperator => "ask",
            WaitingState.Merged => "merged",
            WaitingState.Conflicting or WaitingState.Contradicted => "conflict",
            WaitingState.Stale => "stale",
            WaitingState.Ready or WaitingState.Unblocked => "ready",
            _ => null,
        };
    }

    /// <summary>Applies a suffix to a window name, replacing any the tool owns.</summary>
    internal static string Apply(string windowName, string? suffix)
    {
        string basis = Strip(windowName);
        return suffix is null ? basis : $"{basis}-{suffix}";
    }

    /// <summary>Removes a tool-owned suffix, leaving the agent's base name.</summary>
    internal static string Strip(string windowName)
    {
        Match match = Suffixed().Match(windowName);
        return match.Success && Owned.Contains(match.Groups[2].Value, StringComparer.OrdinalIgnoreCase)
            ? match.Groups[1].Value
            : windowName;
    }

    /// <summary>
    /// Builds one command per host that renames only the windows whose name is wrong. Batched because a
    /// rename per window would cost an ssh round trip per window, which is what made the collection
    /// itself unusable before it was batched.
    /// </summary>
    /// <remarks>
    /// <strong>No byte of tmux text enters shell syntax.</strong> A window's existing name is
    /// agent-controlled arbitrary text, and it flows into <paramref name="renames"/> as the base of the
    /// desired name; interpolating it into a quoted string is an injection, since a single quote closes
    /// the quote and the rest is shell. So every target and desired name is encoded byte-for-byte as a
    /// <c>printf %b</c> octal-escape string — only backslashes and the digits 0-7, which are inert in
    /// single quotes — and decoded back to the exact bytes as a command-substitution <em>argument</em>,
    /// never re-parsed as syntax. A name carrying a quote, a semicolon, a newline, backticks or
    /// <c>$(...)</c> is data.
    ///
    /// Two guards make the mutation safe and its result trustworthy. First an <em>epoch guard</em>: the
    /// host's current tmux server generation is recomputed and compared to the one the sweep saw, and the
    /// whole batch aborts on a mismatch, because a restarted server recycles pane ids and a stale id could
    /// name a different window. Then each rename is <em>confirmed</em>: it prints an <c>ok</c> marker only
    /// when tmux reports success, so the caller reports exactly the renames that happened and names the
    /// ones that did not.
    /// </remarks>
    internal static string? BuildRenameScript(
        IReadOnlyList<(TmuxPane Pane, string Desired)> renames,
        string scannedEpoch,
        string nonce)
    {
        if (renames.Count == 0)
        {
            return null;
        }

        var script = new StringBuilder();

        // Epoch guard. Recompute the server generation exactly as the collection script did — the server's
        // pid and its own start time — and abort the batch on a mismatch rather than rename a possibly
        // recycled id. Skipped only when the sweep recorded no epoch (nothing to compare against).
        if (scannedEpoch.Length > 0)
        {
            script.Append("__e=$(tmux display-message -p '#{pid}:#{start_time}' 2>/dev/null)\n");
            script.Append("if [ \"$__e\" != \"$(printf %b '").Append(ShellEncode(scannedEpoch))
                  .Append("')\" ]; then printf '").Append(nonce).Append(":epoch\\n'; exit 0; fi\n");
        }

        foreach ((TmuxPane pane, string desired) in renames)
        {
            // Targeted by window id (@8), not pane id: the window id is stable for the life of the window,
            // so the rename lands on the window the sweep saw even if its active pane has since changed.
            // The confirmation echoes the same window id so the caller can match it back.
            script.Append("if tmux rename-window -t \"$(printf %b '").Append(ShellEncode(pane.WindowId))
                  .Append("')\" \"$(printf %b '").Append(ShellEncode(desired))
                  .Append("')\" 2>/dev/null; then printf '").Append(nonce).Append(":ok %s\\n' \"$(printf %b '")
                  .Append(ShellEncode(pane.WindowId)).Append("')\"; fi\n");
        }

        return script.ToString();
    }

    /// <summary>
    /// Encodes arbitrary text as a POSIX <c>printf %b</c> octal-escape string: each UTF-8 byte becomes
    /// <c>\0ooo</c> with exactly three octal digits. The result contains only backslashes and the digits
    /// 0-7, all inert inside single quotes, so no byte of the input can reach shell syntax; <c>printf %b</c>
    /// decodes it back to the exact bytes.
    /// </summary>
    internal static string ShellEncode(string value)
    {
        var sb = new StringBuilder(value.Length * 4);
        foreach (byte b in Encoding.UTF8.GetBytes(value))
        {
            sb.Append("\\0").Append(Convert.ToString(b, 8).PadLeft(3, '0'));
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^(.*)-([A-Za-z]+)$")]
    private static partial Regex Suffixed();
}
