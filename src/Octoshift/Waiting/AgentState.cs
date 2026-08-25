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

    /// <summary>Still working; nothing is being asked of anyone.</summary>
    Continue,

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

    /// <summary>
    /// A condition a reader can evaluate against <see cref="Head"/>, for waits with nothing openable
    /// behind them. Separate from <see cref="Blocked"/> because the two differ in who can act: a blocker
    /// is for a person to prioritise, a predicate is for a tool to test.
    /// </summary>
    public WaitPredicate Waiting { get; init; }

    /// <summary>True when this window is tracking an issue because no PR exists yet.</summary>
    public bool IsIssue { get; init; }

    public Recommendation Recommendation { get; init; }

    public required StateSource Source { get; init; }

    /// <summary>
    /// Ways the record contradicts its own contract. Reported rather than corrected: a record that says
    /// something impossible is a signal about the agent, and silently repairing it hides that.
    /// </summary>
    public IReadOnlyList<string> Defects { get; init; } = [];

    /// <summary>
    /// The repository bar: two clean reviews from two different models. A record claiming <c>1/1</c> has
    /// not met it, so the count is checked against this rather than against whatever the record says was
    /// required of it.
    /// </summary>
    public const int RequiredCleanReviews = 2;

    /// <summary>
    /// True only when the declared count actually meets the repository bar. Deliberately not "clean
    /// equals required": a record is not permitted to lower the bar it is measured against.
    /// </summary>
    public bool ReviewsMeetBar
        => ReviewsClean >= RequiredCleanReviews
            && ReviewsRequired >= RequiredCleanReviews
            && ReviewsClean <= ReviewsRequired;

    /// <summary>
    /// Reads a window's state. <paramref name="agentState"/> is the <c>@agent_state</c> option and
    /// <paramref name="windowName"/> the tmux window name. Returns null when neither identifies a PR.
    /// </summary>
    public static AgentState? Parse(string? agentState, string? windowName)
    {
        (int Number, bool IsIssue)? fromName = PrFromWindowName(windowName);
        Dictionary<string, string> fields = SplitFields(agentState);
        var defects = new List<string>();

        int? statePr = null;
        if (!fields.ContainsKey("pr") && fields.TryGetValue("issue", out string? issueValue))
        {
            // A worker branch is local until the coordinator pushes it, so early round boundaries have an
            // issue and no PR. Requiring `pr` there is what produces invented values.
            if (int.TryParse(issueValue, NumberStyles.None, CultureInfo.InvariantCulture, out int issueNumber) && issueNumber > 0)
            {
                return new AgentState
                {
                    PrNumber = issueNumber,
                    IsIssue = true,
                    Head = fields.GetValueOrDefault("head") is { } h && IsSha(h) ? h.ToLowerInvariant() : null,
                    Recommendation = ParseRecommendation(fields.GetValueOrDefault("rec"), defects),
                    Source = StateSource.Declared,
                    Defects = defects,
                };
            }

            // Malformed: fall through to the window name rather than inventing an identity.
            defects.Add($"issue={issueValue} is not an issue number");
        }

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
            return fromName is not { } named
                ? null
                : new AgentState { PrNumber = named.Number, IsIssue = named.IsIssue, Source = StateSource.WindowName };
        }

        if (fromName is { IsIssue: false } window && window.Number != statePr)
        {
            defects.Add($"window is named pr{window.Number} but the record says pr={statePr}");
        }

        (int? clean, int? required) = ParseReviews(fields.GetValueOrDefault("reviews"), defects);
        IReadOnlyList<int> blocked = ParseBlocked(fields.GetValueOrDefault("blocked"), defects);
        if (blocked.Contains(statePr.Value))
        {
            // Observed live. Self-reference reads as a real blocker to anything counting entries, and
            // there is nothing behind it to clear.
            defects.Add($"blocked lists its own PR #{statePr}");
        }

        Recommendation rec = ParseRecommendation(fields.GetValueOrDefault("rec"), defects);
        WaitPredicate waiting = WaitPredicate.Parse(fields.GetValueOrDefault("waiting"), defects);

        // Wait asserts that something it named is still outstanding. Either channel satisfies that — a
        // citable blocker for a person, or an evaluable predicate for a reader — but one of them must be
        // present, or there is nothing to re-check and the claim cannot be evaluated at all.
        if (rec == Recommendation.Wait && blocked.Count == 0 && waiting.Kind == WaitKind.None)
        {
            defects.Add("rec=wait with nothing in blocked or waiting");
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
            Waiting = waiting,
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
            case "continue": return Recommendation.Continue;
            default:
                defects.Add($"rec={value} is not one of continue|wait|merge|approve|stop");
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

    private static (int Number, bool IsIssue)? PrFromWindowName(string? windowName)
    {
        if (windowName is null)
        {
            return null;
        }

        Match match = WindowPr().Match(windowName);
        return match.Success && int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            ? (number, match.Groups[1].Value.Equals("i", StringComparison.OrdinalIgnoreCase))
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

    /// <summary>
    /// Matches the <c>pr4595</c> and <c>i4611</c> window-naming conventions, tolerating a trailing state
    /// suffix such as <c>-blocked</c>.
    /// </summary>
    [GeneratedRegex(@"^(pr|i)(\d{2,6})(?:-|$)", RegexOptions.IgnoreCase)]
    private static partial Regex WindowPr();
}
