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
    private const string BasePrefix = "/plan/";
    private const string OrderSegment = "/order/";

    /// <summary>The order base, e.g. <c>/plan/9001/order/op1</c> — the argument <c>nightshift land</c> takes.</summary>
    public string Base => $"/plan/{Plan}/order/{Order}";

    /// <summary>The worker branch, e.g. <c>nightshift/9001/op1</c>.</summary>
    public string Branch => $"{BranchPrefix}{Plan}/{Order}";

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

    /// <summary>Parses an order base (<c>/plan/{plan}/order/{order}</c>); null if it is not one.</summary>
    public static OrderRef? FromBase(string? orderBase)
    {
        if (orderBase is null || !orderBase.StartsWith(BasePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        int split = orderBase.IndexOf(OrderSegment, StringComparison.Ordinal);
        if (split <= BasePrefix.Length || split + OrderSegment.Length >= orderBase.Length)
        {
            return null;
        }

        if (!orderBase.EndsWith("/", StringComparison.Ordinal))
        {
            string plan = orderBase[BasePrefix.Length..split];
            string order = orderBase[(split + OrderSegment.Length)..];
            return plan.Length > 0 && order.Length > 0 && !plan.Contains('/') && !order.Contains('/')
                ? new OrderRef(plan, order)
                : null;
        }

        return null;
    }
}
