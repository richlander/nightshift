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
    /// Builds one shell script per host that renames only the windows whose name is wrong — one local
    /// <c>tmux if-shell</c> per window inside a single ssh shell, so the whole host still costs one round
    /// trip rather than one per window, which is what made the collection itself unusable before it was
    /// batched.
    /// </summary>
    /// <remarks>
    /// <strong>No byte of tmux text enters shell or tmux syntax.</strong> A window's existing name is
    /// agent-controlled arbitrary text, and it flows into <paramref name="renames"/> as the base of the
    /// desired name. Each desired name is encoded scalar-by-scalar as tmux string escapes — <c>\uXXXX</c>
    /// for the basic plane, <c>\UXXXXXXXX</c> above it — inside a double-quoted <c>rename-window</c>
    /// argument, which tmux's own parser decodes back to the exact characters as one argument. The encoded
    /// form is nothing but a backslash, <c>u</c>/<c>U</c> and hex digits, so it cannot contain a quote, a
    /// space, a semicolon, a newline or a backslash of its own: a name carrying any of those is data both
    /// to the surrounding shell single-quotes and to tmux. A <c>--</c> precedes the name so a leading dash
    /// cannot become an option.
    ///
    /// <strong>The epoch check and the mutation share one server connection — per window.</strong> Each
    /// rename is its own <c>tmux if-shell -F</c> invocation: the format compares the server's live
    /// <c>#{pid}:#{start_time}</c> to the one the sweep saw, and only on a match does its true branch — a
    /// tmux command string, run in that same client's command queue, no nested <c>tmux</c> client and no
    /// <c>run-shell</c> — rename the window and print its confirmation. There is therefore no gap between
    /// "checked the epoch" and "renamed the id" for a restart to slip into: a restart before the
    /// invocation makes its guard false, and a restart between two windows makes the next guard false, so
    /// a recycled id is never renamed. Each invocation prints exactly one marker naming its own window —
    /// <c>&lt;nonce&gt;:ok:@id</c> on a confirmed rename, <c>&lt;nonce&gt;:epoch:@id</c> on a mismatch — so
    /// the caller accounts for every window independently and a restart between windows leaves the earlier
    /// successes reported rather than discarded. The confirmation is a <c>display-message</c> after the
    /// <c>rename-window</c> in the same branch; tmux abandons the branch if the rename fails (a vanished
    /// window), so the ok marker is printed only for a rename that happened. <c>|| :</c> keeps one failed
    /// invocation from aborting the rest.
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

        // The epoch is a structural token (pid:start_time, digits and one colon) validated at collection,
        // so it is safe inside the single-quoted tmux format. Anything that is not one is replaced with a
        // value no live server can equal, so every guard fails closed and renames nothing rather than
        // embedding an unvalidated string into the format.
        string epoch = TmuxScanner.IsEpoch(scannedEpoch) ? scannedEpoch : "0:0";
        string guard = $"'#{{==:#{{pid}}:#{{start_time}},{epoch}}}'";

        var script = new StringBuilder();
        foreach ((TmuxPane pane, string desired) in renames)
        {
            // One if-shell per window. The true branch renames and then confirms, both in this one tmux
            // client's queue against one server generation; the false branch reports the epoch mismatch.
            // The window id (@8) is stable across the pane recycling a restart causes, and is a fixed
            // token; the name is fully escaped, so the single-quoted branch strings carry no raw byte.
            string windowId = pane.WindowId;
            script.Append("tmux if-shell -F ").Append(guard)
                  .Append(" 'rename-window -t ").Append(windowId).Append(" -- \"").Append(TmuxEscape(desired))
                  .Append("\" ; display-message -p ").Append(nonce).Append(":ok:").Append(windowId)
                  .Append("' 'display-message -p ").Append(nonce).Append(":epoch:").Append(windowId).Append("' || :\n");
        }

        return script.ToString();
    }

    /// <summary>
    /// Encodes arbitrary text for a double-quoted tmux argument: each Unicode scalar becomes a tmux string
    /// escape — <c>\uXXXX</c> for scalars in the basic multilingual plane, <c>\UXXXXXXXX</c> above it. The
    /// result is only a backslash, <c>u</c>/<c>U</c> and hex digits, so it contains no quote, space,
    /// semicolon, newline or backslash of the input; tmux decodes it back to the exact characters as one
    /// argument, and nothing in it can reach either shell or tmux command syntax.
    /// </summary>
    internal static string TmuxEscape(string value)
    {
        var sb = new StringBuilder(value.Length * 6);
        foreach (System.Text.Rune rune in value.EnumerateRunes())
        {
            sb.Append(rune.Value <= 0xFFFF
                ? $"\\u{rune.Value:x4}"
                : $"\\U{rune.Value:x8}");
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"^([\s\S]*)-([A-Za-z]+)\z")]
    private static partial Regex Suffixed();
}
