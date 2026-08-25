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
    internal static string? BuildRenameScript(IEnumerable<(TmuxPane Pane, string Desired)> renames)
    {
        var script = new StringBuilder();
        foreach ((TmuxPane pane, string desired) in renames)
        {
            // Pane ids are tmux-generated (%12) and desired names are built from a fixed vocabulary plus
            // an existing window name, so neither can carry shell metacharacters; quoted regardless.
            script.Append("tmux rename-window -t '").Append(pane.PaneId).Append("' '").Append(desired).Append("'\n");
        }

        return script.Length > 0 ? script.ToString() : null;
    }

    [GeneratedRegex(@"^(.*)-([A-Za-z]+)$")]
    private static partial Regex Suffixed();
}
