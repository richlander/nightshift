namespace Nightsky.Commands;

/// <summary>Plan/order scope for narrowing snapshots and watches; wave scope is intentionally deferred.</summary>
internal readonly record struct ScopeSelector(string? Plan, string? Order)
{
    public bool HasPlan => !string.IsNullOrEmpty(Plan);

    public bool HasOrder => !string.IsNullOrEmpty(Order);

    public string PlanPrefix
        => (Plan, Order) switch
        {
            (null, null) => "/plan/",
            ({ } plan, null) => $"/plan/{plan}/",
            ({ } plan, { } order) => $"/plan/{plan}/order/{order}/",
            _ => "/plan/",
        };

    public string ReadyPrefix
        => (Plan, Order) switch
        {
            (null, null) => "/ready/",
            ({ } plan, null) => $"/ready/{plan}/",
            ({ } plan, { }) => $"/ready/{plan}/",
            _ => "/ready/",
        };

    public bool MatchesPlanKey(string key)
        => (Plan, Order) switch
        {
            (null, null) => key.StartsWith("/plan/", StringComparison.Ordinal),
            ({ } plan, null) => key.StartsWith($"/plan/{plan}/", StringComparison.Ordinal),
            ({ } plan, { } order) => key.StartsWith($"/plan/{plan}/order/{order}/", StringComparison.Ordinal),
            _ => false,
        };

    public bool MatchesReadyKey(string key)
        => (Plan, Order) switch
        {
            (null, null) => key.StartsWith("/ready/", StringComparison.Ordinal),
            ({ } plan, null) => key.StartsWith($"/ready/{plan}/", StringComparison.Ordinal),
            ({ } plan, { } order) => key == $"/ready/{plan}/{order}",
            _ => false,
        };

    public bool MatchesBoardKey(string key)
        => MatchesPlanKey(key)
           || MatchesReadyKey(key)
           || key.StartsWith("/agent/", StringComparison.Ordinal)
           || key.StartsWith("/control/", StringComparison.Ordinal);

    public bool IncludesReadyOrder(OrderRef order)
        => (Plan, Order) switch
        {
            (null, null) => true,
            ({ } plan, null) => order.Plan == plan,
            ({ } plan, { } selectedOrder) => order.Plan == plan && order.Order == selectedOrder,
            _ => false,
        };

    public static bool TryParse(string? rawScope, out ScopeSelector scope, out string? error)
    {
        if (string.IsNullOrWhiteSpace(rawScope))
        {
            scope = new ScopeSelector(null, null);
            error = null;
            return true;
        }

        string[] segments = rawScope.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        // Design open item: wave selectors are intentionally not implemented in this cut.
        if (segments.Length is < 1 or > 2)
        {
            scope = default;
            error = "scope must be <plan> or <plan>/<order>";
            return false;
        }

        if (segments.Any(static s => s.Length == 0))
        {
            scope = default;
            error = "scope must not contain empty segments";
            return false;
        }

        scope = segments.Length == 1
            ? new ScopeSelector(segments[0], null)
            : new ScopeSelector(segments[0], segments[1]);
        error = null;
        return true;
    }
}
