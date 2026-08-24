namespace Octoshift.Waiting;

using System.Globalization;
using System.Text.RegularExpressions;

/// <summary>What the agent wants to happen to its window next.</summary>
internal enum Recommendation
{
    /// <summary>No <c>rec=</c> was declared.</summary>
    None,

    /// <summary>Parked behind the entries in <c>blocked=</c>; resumes itself, needs no decision.</summary>
    Wait,

    /// <summary>Asking the operator to merge.</summary>
    Merge,

    /// <summary>Asking the operator to authorise more rounds.</summary>
    Approve,

    /// <summary>Asking to be released from the work. Pending until answered.</summary>
    Stop,

    /// <summary>A <c>rec=</c> value outside the four. Recorded so it can be reported, never acted on.</summary>
    Unrecognised,
}

/// <summary>Where a window's identity came from, worst case last.</summary>
internal enum StateSource
{
    /// <summary>Parsed from a well-formed <c>@agent_state</c>.</summary>
    Declared,

    /// <summary>Only the window name identified the PR.</summary>
    WindowName,
}

/// <summary>
/// One window's self-reported state, read from the <c>@agent_state</c> tmux window option.
/// </summary>
/// <remarks>
/// A window option rather than the pane's text, because the agent UI runs on the alternate screen and
/// tmux keeps no scrollback for it — <c>capture-pane -S -400</c> returns the same single screen as a
/// plain capture, so a report that has scrolled off is unrecoverable. An option persists until the agent
/// changes it and cannot be garbled by line wrapping.
/// </remarks>
internal sealed partial record AgentState
{
    public required int PrNumber { get; init; }

    public string? Head { get; init; }

    public int? Round { get; init; }

    /// <summary>Clean reviews so far, from <c>reviews=&lt;clean&gt;/&lt;required&gt;</c>.</summary>
    public int? ReviewsClean { get; init; }

    public int? ReviewsRequired { get; init; }

    /// <summary>Issue or PR numbers the agent is waiting on that are not its to fix.</summary>
    public IReadOnlyList<int> Blocked { get; init; } = [];

    public Recommendation Recommendation { get; init; }

    public required StateSource Source { get; init; }

    /// <summary>
    /// Ways the record contradicts its own contract. Reported rather than corrected: a record that says
    /// something impossible is a signal about the agent, and silently repairing it hides that.
    /// </summary>
    public IReadOnlyList<string> Defects { get; init; } = [];

    /// <summary>True when the agent has declared the review bar met.</summary>
    public bool ReviewsComplete => ReviewsRequired is > 0 && ReviewsClean == ReviewsRequired;

    /// <summary>
    /// Reads a window's state. <paramref name="agentState"/> is the <c>@agent_state</c> option and
    /// <paramref name="windowName"/> the tmux window name. Returns null when neither identifies a PR.
    /// </summary>
    public static AgentState? Parse(string? agentState, string? windowName)
    {
        int? nameePr = PrFromWindowName(windowName);
        Dictionary<string, string> fields = SplitFields(agentState);
        var defects = new List<string>();

        int? statePr = null;
        if (fields.TryGetValue("pr", out string? prValue))
        {
            if (int.TryParse(prValue, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
            {
                statePr = parsed;
            }
            else
            {
                defects.Add($"pr={prValue} is not a PR number");
            }
        }

        if (statePr is null)
        {
            // The window name is the fallback identity and a good one: it is set once, survives the report
            // scrolling away, and cannot be confused by prose. Scraping the pane for a PR reference is
            // deliberately not attempted — it produced "PR #37" from the phrase "in PR 37 lines".
            return nameePr is null
                ? null
                : new AgentState { PrNumber = nameePr.Value, Source = StateSource.WindowName };
        }

        if (nameePr is not null && nameePr != statePr)
        {
            defects.Add($"window is named pr{nameePr} but the record says pr={statePr}");
        }

        (int? clean, int? required) = ParseReviews(fields.GetValueOrDefault("reviews"), defects);
        IReadOnlyList<int> blocked = ParseBlocked(fields.GetValueOrDefault("blocked"), defects);
        Recommendation rec = ParseRecommendation(fields.GetValueOrDefault("rec"), defects);

        // Wait asserts "what I listed in blocked= is still outstanding". With nothing listed there is
        // nothing to wait on and nothing a reader can re-check, so the claim cannot be evaluated.
        if (rec == Recommendation.Wait && blocked.Count == 0)
        {
            defects.Add("rec=wait with no citable blocker");
        }

        // Ready is dual-clean and mergeable. Recommending a merge before the reviews are in contradicts
        // the bar the recommendation is measured against.
        if (rec == Recommendation.Merge && required is > 0 && clean != required)
        {
            defects.Add($"rec=merge with reviews={clean}/{required}");
        }

        string? head = fields.GetValueOrDefault("head");
        if (head is not null && !IsSha(head))
        {
            defects.Add($"head={head} is not a sha");
            head = null;
        }

        return new AgentState
        {
            PrNumber = statePr.Value,
            Head = head?.ToLowerInvariant(),
            Round = int.TryParse(fields.GetValueOrDefault("round"), NumberStyles.None, CultureInfo.InvariantCulture, out int round) ? round : null,
            ReviewsClean = clean,
            ReviewsRequired = required,
            Blocked = blocked,
            Recommendation = rec,
            Source = StateSource.Declared,
            Defects = defects,
        };
    }

    private static (int? Clean, int? Required) ParseReviews(string? value, List<string> defects)
    {
        if (value is null)
        {
            return (null, null);
        }

        string[] parts = value.Split('/');
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int clean)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int required))
        {
            return (clean, required);
        }

        defects.Add($"reviews={value} is not <clean>/<required>");
        return (null, null);
    }

    private static IReadOnlyList<int> ParseBlocked(string? value, List<string> defects)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        var numbers = new List<int>();
        foreach (string entry in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(entry.TrimStart('#'), NumberStyles.None, CultureInfo.InvariantCulture, out int number) && number > 0)
            {
                numbers.Add(number);
            }
            else
            {
                // The whole point of the field is that a reader can open the thing and a second agent
                // hitting the same wall can find it. "ci" names nothing.
                defects.Add($"blocked={entry} is not a citable issue or PR number");
            }
        }

        return numbers;
    }

    private static Recommendation ParseRecommendation(string? value, List<string> defects)
    {
        if (value is null)
        {
            return Recommendation.None;
        }

        switch (value.ToLowerInvariant())
        {
            case "wait": return Recommendation.Wait;
            case "merge": return Recommendation.Merge;
            case "approve": return Recommendation.Approve;
            case "stop": return Recommendation.Stop;
            default:
                defects.Add($"rec={value} is not one of wait|merge|approve|stop");
                return Recommendation.Unrecognised;
        }
    }

    private static Dictionary<string, string> SplitFields(string? text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(text))
        {
            return fields;
        }

        foreach (string token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int eq = token.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            // An empty value (blocked=) is the agent saying "nothing here"; keep it out of the map so it
            // reads the same as having been omitted.
            string value = token[(eq + 1)..];
            if (value.Length > 0)
            {
                fields.TryAdd(token[..eq], value);
            }
        }

        return fields;
    }

    private static int? PrFromWindowName(string? windowName)
    {
        if (windowName is null)
        {
            return null;
        }

        Match match = WindowPr().Match(windowName);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int pr)
            ? pr
            : null;
    }

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

    /// <summary>Matches the <c>pr4595</c> window-naming convention, tolerating a trailing state suffix.</summary>
    [GeneratedRegex(@"^pr(\d{2,6})(?:-|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowPr();
}
