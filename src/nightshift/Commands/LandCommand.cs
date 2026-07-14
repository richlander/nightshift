namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift land &lt;slice&gt;</c> — mark a slice merged. This is the DAG-advancing signal, distinct from
/// the agent's self-declared <c>done</c>: a slice only opens its dependents once it has <b>landed</b> on
/// main. Triggered at merge time (by the operator or a merge-watcher), it wakes the live <c>plan</c>
/// controller, which promotes every now-unblocked slice. <c>&lt;slice&gt;</c> is the base path `next` printed,
/// e.g. <c>/order/1234/op/0004/slice/a</c>.
/// </summary>
internal static class LandCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? sliceBase = OrderFile.FirstPositional(args);
        if (sliceBase is null || !sliceBase.StartsWith("/order/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: nightshift land <slice>   (e.g. /order/1234/op/0004/slice/a)");
            return 2;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        if (await client.GetAsync($"{sliceBase}/spec", ct) is null)
        {
            Console.Error.WriteLine($"nightshift land: no such slice: {sliceBase}");
            return 3;
        }

        await SliceState.WriteAsync(client, sliceBase, "landed", Options.Value(args, "--reason"), "operator", ct);
        Console.WriteLine($"LANDED {sliceBase}");
        return 0;
    }
}
