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
    /// <paramref name="windowName"/> the tmux window name. Returns null when neither identifies a PR or
    /// an issue.
    /// </summary>
    /// <remarks>
    /// Identity is settled first and the rest of the record is read the same way whatever settled it.
    /// Identity used to short-circuit the read: a window named <c>i4613</c> publishing <c>pr=none
    /// head=pending round=0 reviews=0/2 rec=stop</c> — an observed value, and the reason the window name
    /// is a fallback at all — returned a name-only record and dropped every other field, including the
    /// <c>rec=stop</c> that was the agent asking to be released. A malformed identity is a defect in one
    /// field; it says nothing about the fields beside it, and discarding them turns an escalation into a
    /// window that looks like it is quietly getting on with things.
    /// </remarks>
    public static AgentState? Parse(string? agentState, string? windowName)
    {
        (int Number, bool IsIssue)? fromName = PrFromWindowName(windowName);
        Dictionary<string, string> fields = SplitFields(agentState);
        var defects = new List<string>();

        int? number = null;
        bool isIssue = false;
        StateSource source = StateSource.Declared;

        // Each identity field is read on its own. They used to be read as one — `issue` was consulted
        // only when no `pr` key was present at all — so a window publishing `pr=none issue=4611` had a
        // perfectly good issue number suppressed by the broken field beside it. That is the same mistake
        // as discarding the record: a malformed field is a defect in that field and says nothing about
        // the field next to it.
        //
        // A worker branch is local until the coordinator pushes it, so early round boundaries have an
        // issue and no PR. Requiring `pr` there is what produces invented values.
        int? declaredPr = ReadIdentity(fields, "pr", defects);
        int? declaredIssue = ReadIdentity(fields, "issue", defects);

        if (declaredPr is { } prNumber && declaredIssue is { } issueNumber)
        {
            // A window tracks one thing, so two identities is a contradiction in the record itself. It is
            // reported and then settled by a fixed rule — `pr` wins, because the contract's own progression
            // is issue-then-PR, so the PR is the later claim about the same work. Deliberately not settled
            // by whichever one the window name agrees with: that would repair the contradiction into a
            // record that then looks corroborated. The defect stands either way, which keeps assurance low
            // and the row out of anything that acts unattended.
            defects.Add($"record declares both pr={prNumber} and issue={issueNumber}");
            number = prNumber;
        }
        else if (declaredPr is { } onlyPr)
        {
            number = onlyPr;
        }
        else if (declaredIssue is { } onlyIssue)
        {
            number = onlyIssue;
            isIssue = true;
        }

        if (number is null)
        {
            // The window name is the fallback identity and a good one: it is set once, survives the report
            // scrolling away, and cannot be confused by prose. Scraping the pane for a PR reference is
            // deliberately not attempted — it produced "PR #37" from the phrase "in PR 37 lines".
            if (fromName is not { } named)
            {
                return null;
            }

            number = named.Number;
            isIssue = named.IsIssue;

            // Said honestly: the identity came from the name, so nothing here is corroborated by the
            // record, and any defect that got us here is kept rather than forgotten.
            source = StateSource.WindowName;
        }
        else if (fromName is { } window)
        {
            // The record identified itself, so the window name is corroboration rather than a source. Both
            // halves of the identity are compared: a state carrying `pr=` in a window named `i4613` is as
            // much a mismatch as one carrying another window's number, and it was invisible while only
            // PR-against-PR numbers were checked. Neither is repaired — four windows on one host once
            // carried a neighbour's state verbatim, and the disagreement is the only way to see it from
            // outside.
            if (window.IsIssue != isIssue)
            {
                defects.Add($"window is named {WindowLabel(window)} but the record declares {(isIssue ? "an issue" : "a PR")}");
            }

            if (window.Number != number)
            {
                defects.Add($"window is named {WindowLabel(window)} but the record says {(isIssue ? "issue" : "pr")}={number}");
            }
        }

        (int? clean, int? required) = ParseReviews(fields.GetValueOrDefault("reviews"), defects);
        IReadOnlyList<int> blocked = ParseBlocked(fields.GetValueOrDefault("blocked"), defects);
        if (blocked.Contains(number.Value))
        {
            // Observed live. Self-reference reads as a real blocker to anything counting entries, and
            // there is nothing behind it to clear. Named for what this window is tracking: an issue window
            // has no PR, and calling its issue a PR is a second wrong fact in a message about wrongness.
            defects.Add($"blocked lists its own {(isIssue ? "issue" : "PR")} #{number}");
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
            PrNumber = number.Value,
            IsIssue = isIssue,
            Head = head?.ToLowerInvariant(),
            Round = int.TryParse(fields.GetValueOrDefault("round"), NumberStyles.None, CultureInfo.InvariantCulture, out int round) ? round : null,
            ReviewsClean = clean,
            ReviewsRequired = required,
            Blocked = blocked,
            Waiting = waiting,
            Recommendation = rec,
            Source = source,
            Defects = defects,
        };
    }

    private static bool TryNumber(string value, out int number)
        => int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0;

    /// <summary>
    /// Reads one identity field, reporting a malformed value as a defect in that field alone and
    /// returning null so the caller can fall back without having lost the account of what was wrong.
    /// </summary>
    private static int? ReadIdentity(Dictionary<string, string> fields, string key, List<string> defects)
    {
        if (!fields.TryGetValue(key, out string? value))
        {
            return null;
        }

        if (TryNumber(value, out int number))
        {
            return number;
        }

        defects.Add($"{key}={value} is not {(key == "issue" ? "an issue" : "a PR")} number");
        return null;
    }

    private static string WindowLabel((int Number, bool IsIssue) window)
        => $"{(window.IsIssue ? "i" : "pr")}{window.Number}";

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
