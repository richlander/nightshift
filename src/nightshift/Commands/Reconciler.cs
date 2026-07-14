namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// Projects a <see cref="WorkOrder"/> into Turnstile and keeps <c>/ready/*</c> consistent with the DAG.
/// A slice is dispatchable iff every op it depends on has fully <b>landed</b> (merged), it is not already
/// claimed, and it has no in-flight/terminal outcome. `landed` — not the agent's self-declared `done` —
/// is the signal that opens downstream work: the human stays in the merge loop, dispatch is autonomous.
/// </summary>
internal static class Reconciler
{
    // Statuses that keep a slice OUT of the ready set (it is in-flight or finished, not available).
    private static readonly HashSet<string> Ineligible = ["done", "landed", "blocked", "escalated", "refused"];

    public sealed record Result(int SpecsCreated, int Added, int Removed);

    public static async Task<Result> RunAsync(TurnstileClient client, WorkOrder order, CancellationToken ct)
    {
        int specsCreated = 0;
        foreach (OrderOp op in order.Ops)
        {
            foreach (OrderSlice slice in op.Slices)
            {
                if (await client.CreateImmutableAsync($"{slice.Base}/spec", slice.SpecJson, ct))
                {
                    specsCreated++;
                }
            }
        }

        // Snapshot each slice's outcome once, so op-level "fully landed" can be resolved cheaply.
        var status = new Dictionary<string, string?>();
        foreach (OrderOp op in order.Ops)
        {
            foreach (OrderSlice slice in op.Slices)
            {
                KvItem? state = await client.GetAsync($"{slice.Base}/state", ct);
                status[slice.Base] = state is null ? null : StatusOf(state.Text);
            }
        }

        var landedOps = order.Ops
            .Where(op => op.Slices.Count > 0 && op.Slices.All(s => status[s.Base] == "landed"))
            .Select(op => op.Id)
            .ToHashSet();

        var presentReady = (await client.RangeAsync("/ready/", ct)).Select(k => k.Key).ToHashSet();

        int added = 0, removed = 0;
        foreach (OrderOp op in order.Ops)
        {
            bool depsLanded = op.After.All(landedOps.Contains);
            foreach (OrderSlice slice in op.Slices)
            {
                string readyKey = order.ReadyKey(op, slice);
                bool present = presentReady.Contains(readyKey);

                bool ineligibleState = status[slice.Base] is { } st && Ineligible.Contains(st);
                bool claimed = await client.GetAsync($"{slice.Base}/claim", ct) is not null;
                bool eligible = depsLanded && !ineligibleState && !claimed;

                if (eligible && !present)
                {
                    await client.SetAsync(readyKey, slice.Base, ct);
                    added++;
                }
                else if (!eligible && present)
                {
                    await client.DeleteAsync(readyKey, ct);
                    removed++;
                }
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
