namespace Octoshift.Coordination;

/// <summary>
/// The order↔branch bijection, re-implemented here on purpose: octoshift links nothing from Nightshift, so
/// it owns a tiny local copy of the mapping rather than referencing <c>Nightshift.Commands.OrderRef</c>. A
/// worker produces exactly one branch, <c>nightshift/{plan}/{order}</c>, off origin/main, which maps 1:1 to
/// the order base <c>/plan/{plan}/order/{order}</c>. That branch is the join key octoshift resolves a
/// merged PR through — machine-set, unique, and stable across PR-body edits.
/// </summary>
internal readonly record struct OrderRef(string Plan, string Order)
{
    private const string BranchPrefix = "nightshift/";

    /// <summary>The order base, e.g. <c>/plan/9001/order/op1</c> — the argument <c>nightshift land</c> takes.</summary>
    public string Base => $"/plan/{Plan}/order/{Order}";

    /// <summary>Parses a worker branch name (<c>nightshift/{plan}/{order}</c>); null if it is not one.</summary>
    public static OrderRef? FromBranch(string? branch)
    {
        if (branch is null || !branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] parts = branch[BranchPrefix.Length..].Split('/');
        if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0)
        {
            return null;
        }

        return new OrderRef(parts[0], parts[1]);
    }
}
