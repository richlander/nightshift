namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// Projects a <see cref="Plan"/> into Turnstile and keeps <c>/ready/*</c> consistent with the DAG.
/// An order (one landable PR) is dispatchable iff its dependency edges are satisfied, it is not already
/// claimed, and it has no in-flight/terminal outcome. An edge is satisfied one of two ways:
/// <list type="bullet">
/// <item>an <b>independent</b> order (base ref <c>main</c>) waits until every order it depends on has
/// <b>landed</b> (merged) — <c>landed</c>, not the agent's self-declared <c>done</c>, is the signal that
/// opens downstream work, so the human stays in the merge loop while dispatch is autonomous; or</item>
/// <item>a <b>stacked</b> order (a non-default base ref — a parent's branch or a pinned SHA) releases as
/// soon as that base-ref commit is <b>reachable in the local object database</b>, ahead of the parent's
/// merge, so siblings on a shared base build concurrently (see <c>docs/design/stacked-orders.md</c> §3).</item>
/// </list>
/// </summary>
internal static class Reconciler
{
    // Statuses that keep an order OUT of the ready set (it is in-flight or finished, not available).
    // `changes-requested` is deliberately ABSENT: a review-rejected order is non-terminal and must return
    // to the claimable pool (under the normal deps-landed + unclaimed rule) so a worker can continue its
    // branch. It is not `landed`, so it never joins `landedOrders` below and never opens its dependents.
    private static readonly HashSet<string> Ineligible = ["done", "landed", "blocked", "escalated", "refused"];

    public sealed record Result(int SpecsCreated, int Added, int Removed);

    public static Task<Result> RunAsync(TurnstileClient client, Plan plan, CancellationToken ct)
        => RunAsync(client, plan, Git.IsReachable, ct);

    /// <param name="isReachable">
    /// Tests whether a stacked order's base-ref commit-ish exists in the local object database. Injected so
    /// the git dependency is isolated for tests; production passes <see cref="Git.IsReachable"/>.
    /// </param>
    public static async Task<Result> RunAsync(TurnstileClient client, Plan plan, Func<string, bool> isReachable, CancellationToken ct)
    {
        int specsCreated = 0;
        foreach (Order order in plan.Orders)
        {
            if (await client.CreateImmutableAsync($"{order.Base}/spec", order.SpecJson, ct))
            {
                specsCreated++;
            }
        }

        // Snapshot each order's outcome once so dependency checks are cheap.
        var status = new Dictionary<string, string?>();
        foreach (Order order in plan.Orders)
        {
            KvItem? state = await client.GetAsync($"{order.Base}/state", ct);
            status[order.Id] = state is null ? null : StatusOf(state.Text);
        }

        var landedOrders = plan.Orders
            .Where(o => status[o.Id] == "landed")
            .Select(o => o.Id)
            .ToHashSet();

        var presentReady = (await client.RangeAsync("/ready/", ct)).Select(k => k.Key).ToHashSet();

        int added = 0, removed = 0;
        foreach (Order order in plan.Orders)
        {
            string readyKey = plan.ReadyKey(order);
            bool present = presentReady.Contains(readyKey);

            bool depsLanded = order.After.All(landedOrders.Contains);
            bool stackedReady = await StackedReadyAsync(client, order, isReachable, ct);
            bool depsSatisfied = depsLanded || stackedReady;
            bool ineligibleState = status[order.Id] is { } st && Ineligible.Contains(st);
            bool claimed = await client.GetAsync($"{order.Base}/claim", ct) is not null;
            bool eligible = depsSatisfied && !ineligibleState && !claimed;

            if (eligible && !present)
            {
                await client.SetAsync(readyKey, order.Base, ct);
                added++;
            }
            else if (!eligible && present)
            {
                await client.DeleteAsync(readyKey, ct);
                removed++;
            }
        }

        return new Result(specsCreated, added, removed);
    }

    /// <summary>
    /// A stacked order is released early — ahead of its parent's merge — once its base-ref commit is reachable
    /// locally. Only a <b>non-default</b> base ref (something other than <c>main</c>, written by the coordinator
    /// for a stacked child) takes this path; an independent order on <c>main</c> keeps today's deps-landed
    /// semantics and never triggers a git probe here.
    /// </summary>
    private static async Task<bool> StackedReadyAsync(TurnstileClient client, Order order, Func<string, bool> isReachable, CancellationToken ct)
    {
        KvItem? baseRef = await client.GetAsync($"{order.Base}/base-ref", ct);
        if (baseRef?.Text.Trim() is not { Length: > 0 } commitish
            || string.Equals(commitish, OrderView.DefaultBaseRef, StringComparison.Ordinal))
        {
            return false;
        }

        return isReachable(commitish);
    }

    private static string? StatusOf(string stateJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            return doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
