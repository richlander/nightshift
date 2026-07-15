namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift drain [--resume]</c> — the graceful gesture: stop handing out NEW work while letting
/// running workers finish. Sets the durable <c>/control/draining</c> flag so every worker's <c>next</c>
/// returns <c>DRAINING</c>; <c>--resume</c> clears it and dispatch continues. This is the 95% case.
/// </summary>
internal static class DrainCommand
{
    private const string DrainingKey = "/control/draining";

    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(Array.IndexOf(args, "--resume") >= 0);

    public static async Task<int> RunAsync(bool resume)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        return await Control.ToggleAsync(client, resume, DrainingKey, "DRAINING", "RESUMED", cts.Token);
    }
}
