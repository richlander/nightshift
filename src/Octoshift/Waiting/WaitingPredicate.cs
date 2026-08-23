namespace Octoshift.Waiting;

/// <summary>The kind of condition an agent declared it is waiting on.</summary>
internal enum PredicateKind
{
    /// <summary>No <c>waiting=</c> was declared and none could be inferred.</summary>
    Unknown,

    /// <summary>A named check run must report on the record's head.</summary>
    Check,

    /// <summary>The PR must become mergeable.</summary>
    Merge,

    /// <summary>A review round is outstanding; nothing on GitHub will change it.</summary>
    Review,

    /// <summary>A human decision is required. No tool may release this.</summary>
    Operator,

    /// <summary>Nothing external blocks the agent; it stopped at a round boundary.</summary>
    None,
}

/// <summary>
/// The parsed <c>waiting=</c> field. The point of the field is that a reader can *evaluate* it, so the
/// parse keeps the discriminating detail — <c>check:ci-required</c> names one check run to look up on one
/// sha, where a bare <c>ci</c> would force the reader to guess which of several pending checks was meant.
/// </summary>
internal readonly record struct WaitingPredicate(PredicateKind Kind, string? CheckName)
{
    public static WaitingPredicate Unknown { get; } = new(PredicateKind.Unknown, null);

    /// <summary>Parses a <c>waiting=</c> value. Unrecognised values degrade to <see cref="Unknown"/>.</summary>
    public static WaitingPredicate Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Unknown;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("check:", StringComparison.OrdinalIgnoreCase))
        {
            string name = trimmed["check:".Length..];
            return name.Length == 0 ? Unknown : new WaitingPredicate(PredicateKind.Check, name);
        }

        return trimmed.ToLowerInvariant() switch
        {
            "merge" => new WaitingPredicate(PredicateKind.Merge, null),
            "review" => new WaitingPredicate(PredicateKind.Review, null),
            "operator" => new WaitingPredicate(PredicateKind.Operator, null),
            "none" => new WaitingPredicate(PredicateKind.None, null),

            // `ci` is the vague form AGENTS.md steers away from: it names no check, so it can only be
            // evaluated as "is anything still pending", which is what PredicateKind.None already means.
            "ci" => new WaitingPredicate(PredicateKind.None, null),
            _ => Unknown,
        };
    }

    /// <summary>Renders the predicate back to its wire form for display.</summary>
    public override string ToString() => Kind switch
    {
        PredicateKind.Check => $"check:{CheckName}",
        PredicateKind.Merge => "merge",
        PredicateKind.Review => "review",
        PredicateKind.Operator => "operator",
        PredicateKind.None => "none",
        _ => "unknown",
    };
}
