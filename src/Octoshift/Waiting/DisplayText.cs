namespace Octoshift.Waiting;

using System.Globalization;
using System.Text;

/// <summary>
/// Renders untrusted text safe to print to a terminal.
/// </summary>
/// <remarks>
/// Nearly everything this tool reports is arbitrary text somebody else chose: a tmux session or window
/// name, an <c>@agent_state</c> value quoted back inside a defect, a check name that reached a verdict's
/// reason, a pane capture, or the stderr of an ssh that failed. Two things follow from that, and both
/// have been observed rather than imagined.
///
/// A newline forges rows: the report is line-oriented, so a window named <c>a\nATTENTION 9 of 9…</c>
/// prints a second line that reads exactly like the tool's own summary. A CR is worse, because it
/// overwrites the line already printed rather than adding one.
///
/// An ESC drives the terminal: a capture or a window name carrying <c>ESC[2J</c> clears the reader's
/// screen, and <c>ESC]0;…BEL</c> retitles their terminal. The reader is the operator this whole path
/// exists to inform, so text that can move their cursor is text that can hide the row they needed.
///
/// So control characters are escaped into their visible spelling — the value is still legible, still
/// says what the agent wrote, and can no longer be mistaken for the report's own framing. Ordinary text,
/// including every non-ASCII character that is not a control, is passed through untouched: escaping is
/// for what a terminal executes, not for what it merely displays.
/// </remarks>
internal static class DisplayText
{
    /// <summary>Escapes control characters so a value cannot forge a row or drive the terminal.</summary>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        int first = -1;
        for (int i = 0; i < value.Length; i++)
        {
            if (NeedsEscape(value[i]))
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            return value;
        }

        var escaped = new StringBuilder(value.Length + 8);
        escaped.Append(value, 0, first);
        for (int i = first; i < value.Length; i++)
        {
            char c = value[i];
            switch (c)
            {
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                case '\u001b': escaped.Append("\\e"); break;
                default:
                    if (!NeedsEscape(c))
                    {
                        escaped.Append(c);
                    }
                    else
                    {
                        escaped.Append(c <= 0xff
                            ? string.Create(CultureInfo.InvariantCulture, $"\\x{(int)c:x2}")
                            : string.Create(CultureInfo.InvariantCulture, $"\\u{(int)c:x4}"));
                    }

                    break;
            }
        }

        return escaped.ToString();
    }

    /// <summary>
    /// C0, DEL and C1 — everything a terminal acts on rather than shows — plus the two Unicode separators
    /// that some readers treat as line breaks.
    /// </summary>
    private static bool NeedsEscape(char c) => char.IsControl(c) || c is '\u2028' or '\u2029';
}
