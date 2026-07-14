namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift plan --order &lt;order.json&gt; [--sha]</c> — the live ns-plan controller. Seeds the order,
/// then watches Turnstile and continuously reconciles <c>/ready/*</c> so the DAG advances on its own:
/// when a slice lands, its dependents open; when a claim is freed, its slice returns to the pool. This is
/// the piece that keeps the operator out of the dispatch loop — merges drive work, no re-run required.
/// </summary>
internal static class PlanCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? path = OrderFile.FirstPositional(args) ?? Options.Value(args, "--order");
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: nightshift plan --order <order.json> [--sha <commit>]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        (WorkOrder order, string sha) = await OrderFile.LoadAsync(path, args, ct);
        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        Reconciler.Result initial = await Reconciler.RunAsync(client, order, ct);
        long from = await client.CurrentRevisionAsync(ct);
        Console.WriteLine($"plan: order {order.OrderId} @ {OrderFile.ShortSha(sha)} — {initial.SpecsCreated} spec(s), {initial.Added} ready (watching)");

        try
        {
            // Any change under /order/** (a state write) or /ready/** (a claim) can shift the frontier.
            await foreach (WatchSignal signal in client.WatchAsync("/order/", from, ct))
            {
                _ = signal;
                Reconciler.Result r = await Reconciler.RunAsync(client, order, ct);
                if (r.Added > 0 || r.Removed > 0)
                {
                    Console.WriteLine($"plan: +{r.Added} ready, -{r.Removed} (rev {signal.Revision})");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.WriteLine("plan: stopped");
        }

        return 0;
    }
}
