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
        Console.WriteLine($"LANDED {orderBase}");
        return 0;
    }
}
