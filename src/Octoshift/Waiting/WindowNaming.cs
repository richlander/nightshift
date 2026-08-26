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
    /// <strong>No byte of tmux text enters shell or tmux syntax.</strong> A window's existing name is
    /// agent-controlled arbitrary text, and it flows into <paramref name="renames"/> as the base of the
    /// desired name. Each desired name is encoded byte-for-byte as a <c>printf %b</c> octal-escape string —
    /// only backslashes and the digits 0-7, inert in single quotes — decoded back to the exact bytes and
    /// stashed in a per-window tmux <em>server environment variable</em>. The rename then reads the name
    /// from that variable inside a shell run under <c>IFS=</c> and <c>set -f</c>, where an unquoted
    /// expansion is neither word-split nor globbed and is never re-scanned for shell syntax — so a name
    /// carrying a quote, a semicolon, a newline, backticks or <c>$(...)</c> is data, and it reaches the
    /// rename as one argument after a <c>--</c> so a leading dash cannot become an option either. The name
    /// is never part of a tmux command string, which is the injection surface a batched
    /// <c>rename-window</c> command list would otherwise open.
    ///
    /// <strong>The epoch check and every mutation share one server connection.</strong> The batch is a
    /// single <c>tmux</c> command list: it sets the name variables, then an <c>if-shell -F</c> compares the
    /// server's live <c>#{pid}:#{start_time}</c> to the one the sweep saw and runs the renames only on a
    /// match. Because the comparison and the renames are queued to the same server in one client
    /// invocation, a restart cannot slip between "checked the epoch" and "renamed the id" the way separate
    /// <c>display-message</c> and <c>rename-window</c> clients allowed — a restarted server recycles pane
    /// and window ids, and a stale id could otherwise name a different window. On a mismatch the batch
    /// prints the epoch marker and renames nothing. Each rename is <em>confirmed</em> individually: it
    /// echoes an <c>ok</c> marker naming its window id only when <c>rename-window</c> succeeded, so the
    /// caller reports exactly the renames that happened and names the ones that did not. The name
    /// variables are unset afterward so a long-lived server does not accumulate them.
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
        // value no live server can equal, so the guard fails closed and renames nothing rather than
        // embedding an unvalidated string into the format.
        string epoch = TmuxScanner.IsEpoch(scannedEpoch) ? scannedEpoch : "0:0";

        // Variable names are safe by construction: NS + the hex nonce + an index, an identifier that
        // cannot start with a digit and contains nothing a shell or tmux would interpret.
        string Var(int i) => $"NS{nonce}_{i}";

        var script = new StringBuilder();

        // One tmux command list. First, stash each desired name in a server environment variable as exact
        // bytes: printf %b decodes the octal to the name, the surrounding "$(...)" hands it to
        // set-environment as one argument, and set-environment stores it verbatim.
        script.Append("tmux");
        for (int i = 0; i < renames.Count; i++)
        {
            script.Append(" set-environment -g ").Append(Var(i))
                  .Append(" \"$(printf %b '").Append(ShellEncode(renames[i].Desired)).Append("')\" \\;");
        }

        // Then the atomic gate: the renames run in the then-branch only when the live server generation
        // still matches the sweep's. run-shell executes them in one shell on the server; IFS= and set -f
        // make each unquoted $NS... expand to exactly the stored bytes, `--` stops a leading dash becoming
        // an option, and `&& echo` confirms only a rename tmux accepted. Targeted by window id, which is
        // stable across the pane splits and joins that recycle pane ids.
        script.Append(" if-shell -F '#{==:#{pid}:#{start_time},").Append(epoch).Append("}' \"run-shell 'IFS=;set -f;");
        for (int i = 0; i < renames.Count; i++)
        {
            if (i > 0)
            {
                script.Append(';');
            }

            string windowId = renames[i].Pane.WindowId;
            script.Append("tmux rename-window -t ").Append(windowId).Append(" -- \\$").Append(Var(i))
                  .Append(" && echo ").Append(nonce).Append(":ok:").Append(windowId);
        }

        script.Append("'\" \"display-message -p ").Append(nonce).Append(":epoch\"\n");

        // Clean up the name variables so a long-lived server does not accumulate one per rename per run.
        script.Append("tmux");
        for (int i = 0; i < renames.Count; i++)
        {
            if (i > 0)
            {
                script.Append(" \\;");
            }

            script.Append(" set-environment -gu ").Append(Var(i));
        }

        script.Append('\n');
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
