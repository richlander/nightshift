namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift land &lt;order&gt;</c> — mark an order (one landable PR) merged. This is the DAG-advancing
/// signal, distinct from the agent's self-declared <c>done</c>: an order only opens its dependents once it
/// has <b>landed</b> on main. Triggered at merge time (by the operator or a merge-watcher), it wakes the
/// live <c>plan</c> controller, which promotes every now-unblocked order. <c>&lt;order&gt;</c> is the base
/// path `next` printed, e.g. <c>/plan/1234/order/op4</c>.
/// </summary>
internal static class LandCommand
{
    private const string BlessedMainHeadKey = "/coord/main-head";

    public static async Task<int> RunAsync(string? orderBase, string? reason)
    {
        if (orderBase is null || !orderBase.StartsWith("/plan/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: nightshift land <order>   (e.g. /plan/1234/order/op4)");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        if (await client.GetAsync($"{orderBase}/spec", ct) is null)
        {
            Console.Error.WriteLine($"nightshift land: no such order: {orderBase}");
            return 3;
        }

        await OrderState.WriteAsync(client, orderBase, "landed", reason, "operator", ct);
        await TryAdvanceAndBlessMainAsync(client, ct);
        Console.WriteLine($"LANDED {orderBase}");
        return 0;
    }

    private static async Task TryAdvanceAndBlessMainAsync(TurnstileClient client, CancellationToken ct)
    {
        try
        {
            string? currentBranch = Git.CurrentBranch();
            if (!string.Equals(currentBranch, "main", StringComparison.Ordinal))
            {
                await TryBlessCurrentMainAsync(client, ct);
                return;
            }

            string? target = Git.RevParse("origin/main");
            if (target is null)
            {
                Console.Error.WriteLine("nightshift land: could not resolve origin/main; skipping local main fast-forward");
                return;
            }

            string? head = Git.RevParse("main");
            if (head is null)
            {
                Console.Error.WriteLine("nightshift land: could not resolve main; skipping local main fast-forward");
                return;
            }

            if (head == target)
            {
                await client.SetAsync(BlessedMainHeadKey, head, ct);
                return;
            }

            if (!Git.IsAncestor(head, target))
            {
                Console.Error.WriteLine("nightshift land: local main is not an ancestor of origin/main; refusing non-fast-forward move");
                return;
            }

            if (!Git.MergeFastForwardOnly(target))
            {
                Console.Error.WriteLine($"nightshift land: git merge --ff-only {target} failed; local main not advanced");
                return;
            }

            string? advanced = Git.RevParse("main");
            if (advanced is null)
            {
                Console.Error.WriteLine("nightshift land: local main advanced but could not resolve new main SHA; blessed head not updated");
                return;
            }

            await client.SetAsync(BlessedMainHeadKey, advanced, ct);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"nightshift land: non-fatal local main sync error: {ex.Message}");
        }
    }

    private static async Task TryBlessCurrentMainAsync(TurnstileClient client, CancellationToken ct)
    {
        string? localMain = Git.RevParse("main");
        if (localMain is null)
        {
            Console.Error.WriteLine("nightshift land: could not resolve main; skipping blessed main-head update");
            return;
        }

        await client.SetAsync(BlessedMainHeadKey, localMain, ct);
    }
}
