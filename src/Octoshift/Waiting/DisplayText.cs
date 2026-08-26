namespace Octoshift.Waiting;

using System.Globalization;
using System.Text;

/// <summary>
/// Renders untrusted text safe to print to a terminal.
/// </summary>
/// <remarks>
/// Nearly everything this tool reports is arbitrary text somebody else chose: a tmux session or window
/// name, an <c>@agent_state</c> value quoted back inside a defect, a check name that reached a verdict's
/// reason, a pane capture, or the stderr of an ssh that failed. Three things follow from that, and they
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
/// And a Unicode format character reorders the row without touching a single byte of it. Terminals
/// implement bidi: a RIGHT-TO-LEFT OVERRIDE (U+202E) inside a window name prints the rest of the line
/// reversed, so a defect can be made to read as its own opposite; an isolate (U+2066–U+2069) or an
/// embedding (U+202A–U+202D) does the same to a bounded run and can leave a state and its reason
/// swapped; a BOM (U+FEFF) and its zero-width siblings are invisible, so two rows that differ can print
/// identically. None of these are control characters, so every one of them used to pass through
/// untouched. They are escaped for the same reason ESC is: the operator has to be able to read what is
/// actually there.
///
/// So control characters and format characters are escaped into their visible spelling — the value is
/// still legible, still says what the agent wrote, and can no longer be mistaken for the report's own
/// framing or rearranged into something else. Ordinary text is passed through untouched, including every
/// non-ASCII letter and every supplementary character that merely displays: escaping is for what a
/// terminal executes or lays out, not for what it shows.
/// </remarks>
internal static class DisplayText
{
    /// <summary>
    /// Escapes control and format characters so a value cannot forge a row, drive the terminal, or
    /// reorder what is printed around it.
    /// </summary>
    public static string Safe(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Walked by code point, not by char. A supplementary character arrives as a surrogate pair, and
        // judging its halves separately gets both answers wrong: an emoji would be escaped as two lone
        // surrogates, while U+E0001 and the tag characters — format code points a terminal does act on —
        // would each look like an ordinary unassigned char. So a pair is decoded, judged whole, and either
        // copied whole or spelled out as one code point. Nothing is allocated until something has to be.
        StringBuilder? escaped = null;
        int copied = 0;
        int i = 0;
        while (i < value.Length)
        {
            int width = 1;
            int codePoint = value[i];
            if (char.IsHighSurrogate(value[i]) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                codePoint = char.ConvertToUtf32(value[i], value[i + 1]);
                width = 2;
            }

            if (!NeedsEscape(codePoint))
            {
                i += width;
                continue;
            }

            escaped ??= new StringBuilder(value.Length + 8);
            escaped.Append(value, copied, i - copied);
            escaped.Append(Spelling(codePoint));
            i += width;
            copied = i;
        }

        if (escaped is null)
        {
            return value;
        }

        escaped.Append(value, copied, value.Length - copied);
        return escaped.ToString();
    }

    /// <summary>The visible spelling of one code point, in the shortest escape that names it exactly.</summary>
    private static string Spelling(int codePoint) => codePoint switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        '\u001b' => "\\e",
        <= 0xff => string.Create(CultureInfo.InvariantCulture, $"\\x{codePoint:x2}"),
        <= 0xffff => string.Create(CultureInfo.InvariantCulture, $"\\u{codePoint:x4}"),
        _ => string.Create(CultureInfo.InvariantCulture, $"\\U{codePoint:x8}"),
    };

    /// <summary>
    /// Everything a terminal acts on rather than shows: C0, DEL and C1; the two Unicode separators some
    /// readers treat as line breaks; an unpaired surrogate, which is not a character at all; and the whole
    /// <see cref="UnicodeCategory.Format"/> category.
    /// </summary>
    /// <remarks>
    /// The category, rather than a list of the dangerous ones. It contains every bidi control — U+061C,
    /// U+200E/U+200F, the U+202A–U+202E embeddings and overrides, the U+2066–U+2069 isolates — and the
    /// zero-width and invisible characters including U+FEFF, so a hand-written list would have to be
    /// maintained against future Unicode revisions merely to stay correct. It is also the right rule on
    /// its own terms: a format character is by definition one that changes how the text around it is laid
    /// out while printing nothing itself, which is exactly the property that makes a report say something
    /// other than what it contains. Ordinary printable text is outside it — letters, marks, symbols,
    /// punctuation and emoji all pass through.
    /// </remarks>
    private static bool NeedsEscape(int codePoint)
    {
        if (codePoint <= 0xffff)
        {
            char c = (char)codePoint;
            if (char.IsControl(c) || char.IsSurrogate(c) || c is '\u2028' or '\u2029')
            {
                return true;
            }
        }

        return Rune.GetUnicodeCategory(new Rune(codePoint)) == UnicodeCategory.Format;
    }
}
