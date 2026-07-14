namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// Projects a <see cref="Plan"/> into Turnstile and keeps <c>/ready/*</c> consistent with the DAG.
/// An order (one landable PR) is dispatchable iff every order it depends on has <b>landed</b> (merged),
/// it is not already claimed, and it has no in-flight/terminal outcome. `landed` — not the agent's
/// self-declared `done` — is the signal that opens downstream work: the human stays in the merge loop,
/// dispatch is autonomous.
/// </summary>
internal static class Reconciler
{
    // Statuses that keep an order OUT of the ready set (it is in-flight or finished, not available).
    private static readonly HashSet<string> Ineligible = ["done", "landed", "blocked", "escalated", "refused"];

    public sealed record Result(int SpecsCreated, int Added, int Removed);

    public static async Task<Result> RunAsync(TurnstileClient client, Plan plan, CancellationToken ct)
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
            bool ineligibleState = status[order.Id] is { } st && Ineligible.Contains(st);
            bool claimed = await client.GetAsync($"{order.Base}/claim", ct) is not null;
            bool eligible = depsLanded && !ineligibleState && !claimed;

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
