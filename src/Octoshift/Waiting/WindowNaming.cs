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
    /// <remarks>
    /// <c>follows</c> remains here so a legacy window still carrying it is cleaned up on the next pass, but
    /// it is no longer <em>applied</em>: it was a fleet-global ownership standing (whether another window
    /// registered the same PR first), which no per-window tmux guard can revalidate at mutation time — a
    /// concurrent retire or a new rival could flip it while the guarded window itself did not change — so a
    /// window name, which is read at a glance and believed, must not assert it. The contest is still
    /// reported in the row; it just no longer drives a rename.
    /// </remarks>
    private static readonly string[] Owned = ["blocked", "conflict", "merged", "ready", "stale", "ask", "follows"];

    /// <summary>The suffix a verdict earns, or null when the window should carry none.</summary>
    /// <remarks>
    /// Deliberately sparse. A name is read at a glance and believed, so it carries only states that are
    /// unambiguous and worth acting on; everything else leaves the base name clean rather than crowding
    /// the status bar with detail the row already gives.
    ///
    /// The suffix is a pure function of the <em>verdict</em> — never of fleet-wide ownership. Ownership
    /// (owner/follower) is decided across every window claiming a PR, over state that lives outside this
    /// window and can change during the unguardable gap between the sweep and the rename; publishing it in
    /// a name the rename guard cannot revalidate would assert as current fact something that may already be
    /// stale. The verdict's own inputs — the pane's activity and its published <c>@agent_state</c> — are
    /// what the rename guard revalidates at mutation time (a window that resumed, or republished, aborts),
    /// so a suffix built from them alone is defensible where an ownership suffix is not.
    /// </remarks>
    internal static string? SuffixFor(WaitingVerdict verdict)
    {
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
    /// <c>--</c> precedes the desired name so a leading dash cannot become an option. The desired name gets
    /// one extra hazard the staged values do not: <c>rename-window</c> subjects its argument to a second,
    /// format-expansion pass after the lexer decodes the escapes, so a literal <c>#</c> would still open
    /// <c>#{…}</c>/<c>#(…)</c>/<c>#[…]</c>/shorthands (and the <c>#[…]</c> style exceptions defeat any
    /// <c>#</c>-doubling scheme). So the desired name is staged into a user option too and the rename reads
    /// it back as <c>#{@…}</c>: an option value is substituted but not re-expanded, so the name lands
    /// byte-for-byte with no second pass to escape.
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
    /// <strong>Each mutation is guarded on the whole scanned identity, atomically.</strong> A rename is
    /// planned against a sweep that is already stale by the time it runs: the history lock is released and
    /// GitHub is read before rename, and a second concurrent sweep may act in between. Guarding on the
    /// server epoch alone is not enough — the same window may have changed its published PR or its name, a
    /// newer sweep may have already renamed it, or the pane may have <em>resumed</em> since it was scanned
    /// idle, all under an unchanged server. So each window's guard compares, in one tmux client's command
    /// queue, the live server generation (<c>#{pid}:#{start_time}</c>), the live <c>#{window_activity}</c>,
    /// the live <c>#{window_name}</c>, and the live <c>#{@agent_state}</c> against the exact values the
    /// sweep saw. The activity stamp is the durable half of the suffix's other input: every suffix the tool
    /// applies is read from a pane that had <em>stopped</em> (idle, blocked, or stalled — never mid-turn),
    /// and tmux advances <c>window_activity</c> on any pane output, so a window that has produced anything
    /// since the sweep — an idle pane that started working during the GitHub read, a prompt that appeared,
    /// a stall that cleared — no longer matches and the rename aborts. It is the finest atomic activity
    /// signal tmux exposes; the suffix set is deliberately confined to what this plus the name and state
    /// guards can defend, and fleet-global ownership, which none of them can, is no longer published as a
    /// suffix at all.
    ///
    /// <strong>The epoch check and the mutation share one server connection.</strong> The guard is a
    /// <c>tmux if-shell -F</c>: the format is evaluated and, only on a full match, its true branch — a tmux
    /// command string run in that same client's queue, no nested <c>tmux</c> client and no
    /// <c>run-shell</c> — renames the window and prints its confirmation. There is no gap between "checked"
    /// and "renamed" for a restart or a concurrent rename to slip into. Each invocation prints exactly one
    /// marker naming its own window: <c>&lt;nonce&gt;:ok:@id</c> on a confirmed rename;
    /// <c>&lt;nonce&gt;:stale:@id</c> when the server is unchanged but the window's name, published state,
    /// or activity has moved since the sweep (its false branch tests the epoch to tell this from a
    /// restart); and <c>&lt;nonce&gt;:epoch:@id</c> when the server generation itself changed. The caller
    /// accounts for every window independently, so a change to one window leaves the others' outcomes
    /// intact. <c>|| :</c> keeps one failed invocation from aborting the rest.
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

        // Per-run, per-window user option keys the scanned name, scanned state, and the desired name are
        // staged under. The nonce is hex, so the names are valid option identifiers, and keying by it means
        // a second rename process running concurrently on the same host stages under different names — it
        // cannot read this run's expectations or have this run read its.
        string nameKey = $"@o{nonce}n";
        string stateKey = $"@o{nonce}s";
        string descKey = $"@o{nonce}d";

        var script = new StringBuilder();
        foreach ((TmuxPane pane, string desired) in renames)
        {
            string id = pane.WindowId;

            // The scanned window_activity, a non-negative integer validated at collection, so it is safe
            // inside the single-quoted format. Anything else is replaced with a value tmux never prints for
            // window_activity (it is always >= 0), so the guard fails closed rather than embedding an
            // unvalidated string.
            string activity = TmuxScanner.IsActivityStamp(pane.ActivityStamp) ? pane.ActivityStamp : "-1";
            string activityEq = $"#{{==:#{{window_activity}},{activity}}}";

            // 1. Stage the scanned name, raw state, and desired name into this window's options, escaped so
            //    no raw byte reaches shell or tmux syntax. if-shell -F 1 is only a way to have tmux lex the
            //    command string, which is what decodes the escapes; there is no false branch.
            script.Append("tmux if-shell -F 1 'set-option -w -t ").Append(id).Append(' ').Append(nameKey)
                  .Append(" \"").Append(TmuxEscape(pane.WindowName)).Append("\" ; set-option -w -t ").Append(id).Append(' ').Append(stateKey)
                  .Append(" \"").Append(TmuxEscape(pane.AgentStateRaw)).Append("\" ; set-option -w -t ").Append(id).Append(' ').Append(descKey)
                  .Append(" \"").Append(TmuxEscape(desired)).Append("\"'\n");

            // 2. Guard on epoch AND activity AND name AND state, and mutate in the same client's queue. The
            //    rename reads the desired name from its staged option (#{@descKey}) rather than an inline
            //    literal: rename-window subjects its argument to a *second*, format-expansion pass, so any
            //    inline literal #{…}/#(…)/#[…]/shorthand in an agent-chosen name would be evaluated. A
            //    staged option value is substituted but not itself re-expanded, so the name lands
            //    byte-for-byte. The false branch reports whether the server moved (epoch) or only the
            //    identity/activity did (stale).
            string nameEq = $"#{{==:#{{window_name}},#{{{nameKey}}}}}";
            string stateEq = $"#{{==:#{{@agent_state}},#{{{stateKey}}}}}";
            string guard = $"#{{&&:{epochEq},#{{&&:{activityEq},#{{&&:{nameEq},{stateEq}}}}}}}";
            string falseReport = $"#{{?{epochEq},{nonce}:stale:{id},{nonce}:epoch:{id}}}";
            script.Append("tmux if-shell -F -t ").Append(id).Append(" '").Append(guard)
                  .Append("' 'rename-window -t ").Append(id).Append(" -- \"#{").Append(descKey).Append("}\"")
                  .Append(" ; display-message -p -t ").Append(id).Append(' ').Append(nonce).Append(":ok:").Append(id)
                  .Append("' 'display-message -p -t ").Append(id).Append(" \"").Append(falseReport).Append("\"' || :\n");

            // 3. Unset the staged options so no run leaves them behind. Tolerant of a vanished window or a
            //    restarted server, which has already discarded them.
            script.Append("tmux set-option -w -t ").Append(id).Append(" -u ").Append(nameKey).Append(" 2>/dev/null; ")
                  .Append("tmux set-option -w -t ").Append(id).Append(" -u ").Append(stateKey).Append(" 2>/dev/null; ")
                  .Append("tmux set-option -w -t ").Append(id).Append(" -u ").Append(descKey).Append(" 2>/dev/null; :\n");
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
    /// <remarks>
    /// This protects only the command lexer. It is deliberately <em>not</em> relied on to make the desired
    /// name safe on the <c>rename-window</c> line: that argument gets a second, format-expansion pass, which
    /// this encoding does not neutralise (a decoded literal <c>#</c> is still a format introducer, and the
    /// exceptions around <c>#[…]</c> styles make any doubling scheme brittle). The desired name is instead
    /// staged into a user option and referenced as <c>#{@…}</c>, whose value is substituted but not
    /// re-expanded — see <see cref="BuildRenameScript"/> — so it lands byte-for-byte.
    /// </remarks>
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
