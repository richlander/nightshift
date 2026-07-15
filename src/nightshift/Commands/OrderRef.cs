namespace Nightshift.Commands;

/// <summary>
/// The bijection between an order and its git branch. An order lives at <c>/plan/{plan}/order/{order}</c>;
/// the worker produces exactly one branch, <c>nightshift/{plan}/{order}</c>, off origin/main. Because the
/// branch name encodes the order, it is a durable, on-disk breadcrumb: it survives a reboot or a wiped
/// runtime dir when the session file does not, so <c>recover</c> can re-attach from the branch alone. This
/// type is the single place that mapping is expressed, so <c>next</c>, <c>show</c>, and <c>recover</c>
/// agree by construction.
/// </summary>
internal readonly record struct OrderRef(string Plan, string Order)
{
    private const string BranchPrefix = "nightshift/";

    /// <summary>The order's key prefix, e.g. <c>/plan/9001/order/op1</c>.</summary>
    public string Base => $"/plan/{Plan}/order/{Order}";

    /// <summary>The worker's branch for this order, e.g. <c>nightshift/9001/op1</c>.</summary>
    public string Branch => $"{BranchPrefix}{Plan}/{Order}";

    /// <summary>The ready-set row for this order, e.g. <c>/ready/9001/op1</c>.</summary>
    public string ReadyKey => $"/ready/{Plan}/{Order}";

    /// <summary>This order's claim key, e.g. <c>/plan/9001/order/op1/claim</c>.</summary>
    public string ClaimKey => $"{Base}/claim";

    /// <summary>Parses an order base path (<c>/plan/{plan}/order/{order}</c>); null if it is not one.</summary>
    public static OrderRef? FromBase(string? orderBase)
    {
        if (orderBase is null)
        {
            return null;
        }

        string[] p = orderBase.Split('/');
        if (p.Length != 5 || p[0].Length != 0 || p[1] != "plan" || p[3] != "order" || p[2].Length == 0 || p[4].Length == 0)
        {
            return null;
        }

        return new OrderRef(p[2], p[4]);
    }

    /// <summary>Parses a worker branch name (<c>nightshift/{plan}/{order}</c>); null if it is not one.</summary>
    public static OrderRef? FromBranch(string? branch)
    {
        if (branch is null || !branch.StartsWith(BranchPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        string[] p = branch[BranchPrefix.Length..].Split('/');
        if (p.Length != 2 || p[0].Length == 0 || p[1].Length == 0)
        {
            return null;
        }

        return new OrderRef(p[0], p[1]);
    }
}
