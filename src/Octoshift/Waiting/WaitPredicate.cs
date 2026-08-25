namespace Octoshift.Waiting;

/// <summary>The kind of condition a <c>waiting=</c> predicate names.</summary>
internal enum WaitKind
{
    /// <summary>No predicate was declared.</summary>
    None,

    /// <summary>A named check run must conclude on the record's head.</summary>
    Check,

    /// <summary>Every check on the head must conclude.</summary>
    Checks,

    /// <summary>GitHub must compute mergeability and say the branch can merge.</summary>
    Merge,

    /// <summary>A review round is outstanding. Nothing on GitHub will change it.</summary>
    Review,
}

/// <summary>
/// A wait with nothing openable behind it — a check that has not reported, a mergeability GitHub has
/// not computed. Kept separate from <c>blocked=</c>, which is for things a person can open and
/// prioritise, because a check that has not finished is not a defect and does not deserve an issue.
/// </summary>
/// <remarks>
/// Evaluated against the record's <c>head</c>, so it inherits the same falsifiability rule as every
/// other claim: move the head and the wait is void rather than quietly answered about other code.
/// </remarks>
internal readonly record struct WaitPredicate(WaitKind Kind, string? CheckName)
{
    public static WaitPredicate Parse(string? value, List<string> defects)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return default;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("check:", StringComparison.OrdinalIgnoreCase))
        {
            string name = trimmed["check:".Length..];
            if (name.Length > 0)
            {
                return new WaitPredicate(WaitKind.Check, name);
            }

            defects.Add("waiting=check: names no check");
            return default;
        }

        switch (trimmed.ToLowerInvariant())
        {
            case "checks": return new WaitPredicate(WaitKind.Checks, null);
            case "merge": return new WaitPredicate(WaitKind.Merge, null);
            case "review": return new WaitPredicate(WaitKind.Review, null);
            default:
                defects.Add($"waiting={trimmed} is not check:<name>|checks|merge|review");
                return default;
        }
    }

    public override string ToString() => Kind switch
    {
        WaitKind.Check => $"check:{CheckName}",
        WaitKind.Checks => "checks",
        WaitKind.Merge => "merge",
        WaitKind.Review => "review",
        _ => "none",
    };
}
