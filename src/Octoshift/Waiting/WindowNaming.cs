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
    /// Builds one shell script per host that renames only the windows whose name is wrong — three local
    /// <c>tmux</c> invocations per window inside a single ssh shell, so the whole host still costs one
    /// round trip rather than one per window, which is what made the collection itself unusable before it
    /// was batched.
    /// </summary>
    /// <remarks>
    /// <strong>No byte of tmux text enters shell or tmux syntax.</strong> A window's existing name and its
    /// published <c>@agent_state</c> are agent-controlled arbitrary text. Each flows into the script only
    /// as a tmux string escaped scalar-by-scalar — <c>\uXXXX</c> for the basic plane, <c>\UXXXXXXXX</c>
    /// above it — inside a double-quoted argument, which tmux's own parser decodes back to the exact
    /// characters as one argument. The encoded form is nothing but a backslash, <c>u</c>/<c>U</c> and hex
    /// digits, so it cannot contain a quote, a space, a semicolon, a newline or a backslash of its own: a
    /// value carrying any of those is data both to the surrounding shell single-quotes and to tmux. A
    /// <c>--</c> precedes the desired name so a leading dash cannot become an option.
    ///
    /// <strong>Each mutation is guarded on the whole scanned identity, atomically.</strong> A rename is
    /// planned against a sweep that is already stale by the time it runs: the history lock is released and
    /// GitHub is read before rename, and a second concurrent sweep may act in between. Guarding on the
    /// server epoch alone is not enough — the same window may have changed its published PR or its name, or
    /// a newer sweep may have already renamed it, all under an unchanged server. So each window's guard
    /// compares, in one tmux client's command queue, the live server generation
    /// (<c>#{pid}:#{start_time}</c>), the live <c>#{window_name}</c>, and the live <c>#{@agent_state}</c>
    /// against the exact values the sweep saw. The scanned name and state cannot be embedded in a format
    /// literal (they are arbitrary bytes), so a first tmux invocation stages them into two per-window,
    /// per-run user options (keyed by the batch nonce, so a concurrent rename process cannot read or clash
    /// with them) using the same escaping; the guard then compares against <c>#{@…}</c>. A third invocation
    /// unsets them, so no run leaks options.
    ///
    /// <strong>The epoch check and the mutation share one server connection.</strong> The guard is a
    /// <c>tmux if-shell -F</c>: the format is evaluated and, only on a full match, its true branch — a tmux
    /// command string run in that same client's queue, no nested <c>tmux</c> client and no
    /// <c>run-shell</c> — renames the window and prints its confirmation. There is no gap between "checked"
    /// and "renamed" for a restart or a concurrent rename to slip into. Each invocation prints exactly one
    /// marker naming its own window: <c>&lt;nonce&gt;:ok:@id</c> on a confirmed rename;
    /// <c>&lt;nonce&gt;:stale:@id</c> when the server is unchanged but the window's name or state has moved
    /// since the sweep (its false branch tests the epoch to tell this from a restart); and
    /// <c>&lt;nonce&gt;:epoch:@id</c> when the server generation itself changed. The caller accounts for
    /// every window independently, so a change to one window leaves the others' outcomes intact.
    /// <c>|| :</c> keeps one failed invocation from aborting the rest.
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
        const string ServerGen = "#{pid}:#{start_time}";
        string epochEq = $"#{{==:{ServerGen},{epoch}}}";

        // Per-run, per-window user option keys the scanned name and state are staged under. The nonce is
        // hex, so the names are valid option identifiers, and keying by it means a second rename process
        // running concurrently on the same host stages under different names — it cannot read this run's
        // expectations or have this run read its.
        string nameKey = $"@o{nonce}n";
        string stateKey = $"@o{nonce}s";

        var script = new StringBuilder();
        foreach ((TmuxPane pane, string desired) in renames)
        {
            string id = pane.WindowId;

            // 1. Stage the scanned name and raw state into this window's option, escaped so no raw byte
            //    reaches shell or tmux syntax. if-shell -F 1 is only a way to have tmux lex the command
            //    string, which is what decodes the escapes; there is no false branch.
            script.Append("tmux if-shell -F 1 'set-option -w -t ").Append(id).Append(' ').Append(nameKey)
                  .Append(" \"").Append(TmuxEscape(pane.WindowName)).Append("\" ; set-option -w -t ").Append(id).Append(' ').Append(stateKey)
                  .Append(" \"").Append(TmuxEscape(pane.AgentStateRaw)).Append("\"'\n");

            // 2. Guard on epoch AND name AND state, and mutate in the same client's queue. The false branch
            //    reports whether the server moved (epoch) or only the window's identity did (stale).
            string guard = $"#{{&&:{epochEq},#{{&&:#{{==:#{{window_name}},#{{{nameKey}}}}},#{{==:#{{@agent_state}},#{{{stateKey}}}}}}}}}";
            string falseReport = $"#{{?{epochEq},{nonce}:stale:{id},{nonce}:epoch:{id}}}";
            script.Append("tmux if-shell -F -t ").Append(id).Append(" '").Append(guard)
                  .Append("' 'rename-window -t ").Append(id).Append(" -- \"").Append(TmuxEscape(desired))
                  .Append("\" ; display-message -p -t ").Append(id).Append(' ').Append(nonce).Append(":ok:").Append(id)
                  .Append("' 'display-message -p -t ").Append(id).Append(" \"").Append(falseReport).Append("\"' || :\n");

            // 3. Unset the staged options so no run leaves them behind. Tolerant of a vanished window or a
            //    restarted server, which has already discarded them.
            script.Append("tmux set-option -w -t ").Append(id).Append(" -u ").Append(nameKey).Append(" 2>/dev/null; ")
                  .Append("tmux set-option -w -t ").Append(id).Append(" -u ").Append(stateKey).Append(" 2>/dev/null; :\n");
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
