namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift add &lt;orders.json&gt; [--sha]</c> — a one-shot seed/reconcile. Projects the plan into
/// Turnstile (immutable specs) and brings <c>/ready/*</c> into line with the DAG once, then exits. Use
/// <c>plan</c> for the live controller; <c>add</c> is the bootstrap/one-off form and is safe to re-run.
/// </summary>
internal static class AddCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? path = PlanFile.FirstPositional(args);
        if (path is null || !File.Exists(path))
        {
            Console.Error.WriteLine("usage: nightshift add <orders.json> [--sha <commit>]");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        (Plan plan, string sha) = await PlanFile.LoadAsync(path, args, ct);
        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        Reconciler.Result r = await Reconciler.RunAsync(client, plan, ct);
        Console.WriteLine($"seeded plan {plan.PlanId} @ {PlanFile.ShortSha(sha)}: {r.SpecsCreated} spec(s) created, {r.Added} ready added, {r.Removed} removed");
        return 0;
    }
}
