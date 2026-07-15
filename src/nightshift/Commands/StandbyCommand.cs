namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift standby</c> — stay on the roster but stop taking new work. Marks this agent <c>standby</c>
/// and renews the shift lease. Self-healing: if presence had lapsed, standby re-establishes it. Does not
/// touch an order already in hand — use <c>release</c> or <c>leave</c> to give one back.
/// </summary>
internal static class StandbyCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        await Shift.ClockAsync(client, "standby", cts.Token);

        Console.WriteLine("STANDBY");
        return ExitCode.Ok;
    }
}
