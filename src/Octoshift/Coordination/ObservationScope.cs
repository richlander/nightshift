namespace Octoshift.Coordination;

/// <summary>
/// A Turnstile-native scope for read-only observation verbs: either one plan (<c>/plan/{plan}</c>) or one
/// order (<c>/plan/{plan}/order/{order}</c>). The scope maps to the worker-branch namespace
/// <c>nightshift/{plan}/</c> or <c>nightshift/{plan}/{order}</c> for gh queries.
/// </summary>
internal readonly record struct ObservationScope(string Plan, string? Order)
{
    /// <summary>True when the scope targets one order; false when it targets a whole plan.</summary>
    public bool IsOrder => Order is { Length: > 0 };

    /// <summary>The normalized Turnstile scope base.</summary>
    public string Base => IsOrder ? $"/plan/{Plan}/order/{Order}" : $"/plan/{Plan}";

    /// <summary>The branch namespace used for gh search (<c>head:&lt;branch search&gt;</c>).</summary>
    public string BranchSearch => IsOrder ? $"nightshift/{Plan}/{Order}" : $"nightshift/{Plan}/";

    /// <summary>True when <paramref name="branch"/> belongs to this scope's namespace.</summary>
    public bool MatchesBranch(string branch)
        => IsOrder
            ? string.Equals(branch, BranchSearch, StringComparison.Ordinal)
            : branch.StartsWith(BranchSearch, StringComparison.Ordinal);

    /// <summary>
    /// Parses a plan or order scope. Accepted forms:
    /// <c>/plan/{plan}</c>, <c>plan/{plan}</c>, <c>{plan}</c>,
    /// <c>/plan/{plan}/order/{order}</c>, <c>plan/{plan}/order/{order}</c>, <c>{plan}/{order}</c>,
    /// and a branch short form <c>nightshift/{plan}/{order}</c>.
    /// </summary>
    public static ObservationScope? Parse(string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope))
        {
            return null;
        }

        string trimmed = scope.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (OrderRef.FromBase($"/{trimmed}") is { } baseOrder)
        {
            return new ObservationScope(baseOrder.Plan, baseOrder.Order);
        }

        if (OrderRef.FromBranch(trimmed) is { } branchOrder)
        {
            return new ObservationScope(branchOrder.Plan, branchOrder.Order);
        }

        string[] parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            1 => IsSegment(parts[0]) && !parts[0].Equals("plan", StringComparison.Ordinal) ? new ObservationScope(parts[0], null) : null,
            2 => ParseTwoSegment(parts[0], parts[1]),
            4 => ParseFourSegment(parts[0], parts[1], parts[2], parts[3]),
            _ => null,
        };
    }

    private static ObservationScope? ParseTwoSegment(string first, string second)
    {
        if (!IsSegment(second))
        {
            return null;
        }

        if (first.Equals("nightshift", StringComparison.Ordinal))
        {
            return null;
        }

        if (first.Equals("plan", StringComparison.Ordinal))
        {
            return new ObservationScope(second, null);
        }

        return IsSegment(first) ? new ObservationScope(first, second) : null;
    }

    private static ObservationScope? ParseFourSegment(string first, string second, string third, string fourth)
    {
        if (!first.Equals("plan", StringComparison.Ordinal)
            || !third.Equals("order", StringComparison.Ordinal)
            || !IsSegment(second)
            || !IsSegment(fourth))
        {
            return null;
        }

        return new ObservationScope(second, fourth);
    }

    private static bool IsSegment(string value)
        => value.Length > 0 && !value.Contains('/');
}
