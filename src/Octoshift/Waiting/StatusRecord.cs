namespace Octoshift.Waiting;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>How much a reader may trust a record's fields.</summary>
internal enum RecordSource
{
    /// <summary>The agent emitted a <c>NIGHTSHIFT-STATUS</c> line. Fields are asserted, not guessed.</summary>
    Declared,

    /// <summary>No sentinel; the PR number (and maybe a sha) were scraped out of prose.</summary>
    Inferred,
}

/// <summary>
/// The status record an agent leaves as its final output when it stops (see <c>AGENTS.md</c>, "Stopping:
/// the status record"). Parsing is deliberately two-tier: an exact read of the sentinel line when one is
/// present, and a prose fallback when it is not.
/// </summary>
/// <remarks>
/// The fallback matters more than it looks. Agents phrase their stop lines differently and new phrasings
/// keep appearing, so enumerating templates is a treadmill. Recovering just "which PR" from arbitrary
/// prose is enough to check that PR's real state and report it — an inferred record degrades to something
/// useful rather than to nothing, and it never auto-releases an agent because its fields were guessed.
/// </remarks>
internal sealed partial record StatusRecord
{
    /// <summary>The sentinel that opens a declared record.</summary>
    public const string Sentinel = "NIGHTSHIFT-STATUS";

    /// <summary>PR number the record describes.</summary>
    public required int PrNumber { get; init; }

    /// <summary>The head sha the record describes; null when it could not be determined.</summary>
    public string? Head { get; init; }

    /// <summary>Review round just completed, when the stop was a round boundary.</summary>
    public int? Round { get; init; }

    /// <summary>The agent's own conclusion (<c>converging</c>, <c>gated</c>, <c>escalating</c>, ...).</summary>
    public string? Verdict { get; init; }

    /// <summary>The condition that would unblock the agent.</summary>
    public WaitingPredicate Waiting { get; init; } = WaitingPredicate.Unknown;

    /// <summary>What the agent would do once unblocked. A reader releases this; it never authors one.</summary>
    public string? Next { get; init; }

    /// <summary>The record's own timestamp, when it carried one.</summary>
    public DateTimeOffset? At { get; init; }

    /// <summary>Whether the fields were declared by the agent or inferred from prose.</summary>
    public required RecordSource Source { get; init; }

    /// <summary>
    /// Parses the last record out of a captured pane. Returns null when no PR could be identified at all.
    /// </summary>
    /// <param name="paneText">The captured pane.</param>
    /// <param name="paneWidth">
    /// The pane's column count, when known. A terminal hard-wraps at that column without regard for token
    /// boundaries, so this is what distinguishes a split field from a new line of prose.
    /// </param>
    public static StatusRecord? Parse(string? paneText, int paneWidth = 0)
    {
        if (string.IsNullOrWhiteSpace(paneText))
        {
            return null;
        }

        IReadOnlyList<NormalizedLine> lines = Normalize(paneText);
        return ParseDeclared(lines, paneWidth) ?? Infer(lines);
    }

    /// <summary>A pane line with its framing removed, keeping the width it occupied on screen.</summary>
    internal readonly record struct NormalizedLine(string Text, int RawWidth);

    /// <summary>
    /// Strips the TUI's box-drawing frame and trailing blank lines so the pane reads as plain text.
    /// Agent output arrives inside a bordered box, so most lines carry a rule character at one or both
    /// edges; leaving them in would corrupt the first and last token of every line.
    /// </summary>
    internal static IReadOnlyList<NormalizedLine> Normalize(string paneText)
    {
        string[] raw = paneText.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lines = new List<NormalizedLine>(raw.Length);
        foreach (string line in raw)
        {
            // The on-screen width is measured before the frame comes off, because that is the width the
            // terminal wrapped against.
            lines.Add(new NormalizedLine(StripFrame(line), line.TrimEnd().Length));
        }

        while (lines.Count > 0 && lines[^1].Text.Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string StripFrame(string line)
    {
        ReadOnlySpan<char> span = line.AsSpan().Trim();

        // Peel one rule character from each edge, then re-trim: the frame is a single column, so a second
        // pass would start eating content.
        if (span.Length > 0 && IsRule(span[0]))
        {
            span = span[1..].TrimStart();
        }

        if (span.Length > 0 && IsRule(span[^1]))
        {
            span = span[..^1].TrimEnd();
        }

        return span.ToString();
    }

    /// <summary>Box-drawing (U+2500–U+257F) and block-element (U+2580–U+259F) glyphs a TUI frames with.</summary>
    private static bool IsRule(char c) => c is >= '─' and <= '▟';

    private static StatusRecord? ParseDeclared(IReadOnlyList<NormalizedLine> lines, int paneWidth)
    {
        int start = -1;
        for (int i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Text.Contains(Sentinel, StringComparison.Ordinal))
            {
                start = i;
                break;
            }
        }

        if (start < 0)
        {
            return null;
        }

        string first = lines[start].Text;
        var text = new StringBuilder(first[(first.IndexOf(Sentinel, StringComparison.Ordinal) + Sentinel.Length)..]);
        Continue(lines, start, paneWidth, text);

        Dictionary<string, string> fields = SplitFields(text.ToString());
        if (!fields.TryGetValue("pr", out string? prValue) || !TryParsePrNumber(prValue, out int pr))
        {
            return null;
        }

        return new StatusRecord
        {
            PrNumber = pr,
            Head = fields.TryGetValue("head", out string? head) && IsSha(head) ? head.ToLowerInvariant() : null,
            Round = fields.TryGetValue("round", out string? round)
                && int.TryParse(round, NumberStyles.None, CultureInfo.InvariantCulture, out int roundValue)
                    ? roundValue
                    : null,
            Verdict = fields.TryGetValue("verdict", out string? verdict) ? verdict : null,
            Waiting = WaitingPredicate.Parse(fields.GetValueOrDefault("waiting")),
            Next = fields.TryGetValue("next", out string? next) ? next : null,
            At = fields.TryGetValue("at", out string? at)
                && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset atValue)
                    ? atValue
                    : null,
            Source = RecordSource.Declared,
        };
    }

    /// <summary>
    /// Reassembles a record the terminal split across rows. Two different wraps have to be told apart:
    /// a TUI word-wraps inside its box, so the next row opens with a whole <c>key=value</c>; a plain pane
    /// hard-wraps at its last column, so the next row opens with the tail of a token and must be rejoined
    /// with no space. Anything else ends the record, which is what stops prose being absorbed into it.
    /// </summary>
    private static void Continue(IReadOnlyList<NormalizedLine> lines, int start, int paneWidth, StringBuilder text)
    {
        int previousWidth = lines[start].RawWidth;

        for (int i = start + 1; i < lines.Count; i++)
        {
            string[] tokens = lines[i].Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                return;
            }

            bool opensWithFragment = !tokens[0].Contains('=', StringComparison.Ordinal);
            bool tailAreFields = true;
            for (int t = 1; t < tokens.Length; t++)
            {
                if (!FieldToken().IsMatch(tokens[t]))
                {
                    tailAreFields = false;
                    break;
                }
            }

            bool splitToken = false;
            if (opensWithFragment)
            {
                // "tes" + "t next=round-2-review": a bare word trailed by nothing but fields is a shape
                // prose does not take, so it is read as a split token even when the width is unknown. A
                // fragment standing alone is ambiguous, and there the full row is the only evidence.
                splitToken = tailAreFields
                    && (tokens.Length > 1 || (paneWidth > 0 && previousWidth >= paneWidth));

                if (!splitToken)
                {
                    return;
                }
            }
            else if (!tailAreFields || !FieldToken().IsMatch(tokens[0]))
            {
                return;
            }

            for (int t = 0; t < tokens.Length; t++)
            {
                text.Append(t == 0 && splitToken ? string.Empty : " ").Append(tokens[t]);
            }

            previousWidth = lines[i].RawWidth;
        }
    }

    private static Dictionary<string, string> SplitFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || eq == token.Length - 1)
            {
                continue;
            }

            // First writer wins, so a duplicated key cannot be overridden by trailing prose.
            fields.TryAdd(token[..eq], token[(eq + 1)..]);
        }

        return fields;
    }

    private static StatusRecord? Infer(IReadOnlyList<NormalizedLine> lines)
    {
        // Last mention wins: a pane references many PRs over its life and the newest is the live one.
        int pr = -1;
        for (int i = lines.Count - 1; i >= 0 && pr < 0; i--)
        {
            MatchCollection matches = PrMention().Matches(lines[i].Text);
            if (matches.Count > 0 && TryParsePrNumber(matches[^1].Groups[1].Value, out int parsed))
            {
                pr = parsed;
            }
        }

        if (pr < 0)
        {
            return null;
        }

        string? head = null;
        for (int i = lines.Count - 1; i >= 0 && head is null; i--)
        {
            MatchCollection matches = ShaMention().Matches(lines[i].Text);
            for (int m = matches.Count - 1; m >= 0; m--)
            {
                string candidate = matches[m].Groups[1].Value.ToLowerInvariant();

                // Require a hex letter so a bare date or build number cannot pass as a sha.
                if (candidate.AsSpan().IndexOfAnyInRange('a', 'f') >= 0)
                {
                    head = candidate;
                    break;
                }
            }
        }

        return new StatusRecord
        {
            PrNumber = pr,
            Head = head,
            Waiting = WaitingPredicate.Unknown,
            Source = RecordSource.Inferred,
        };
    }

    private static bool TryParsePrNumber(string value, out int number)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    private static bool IsSha(string value)
    {
        if (value.Length is < 7 or > 40)
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_]*=\S+$")]
    private static partial Regex FieldToken();

    [GeneratedRegex(@"\bPR\s*#?(\d{2,6})\b", RegexOptions.IgnoreCase)]
    private static partial Regex PrMention();

    [GeneratedRegex(@"\b([0-9a-fA-F]{7,40})\b")]
    private static partial Regex ShaMention();
}
