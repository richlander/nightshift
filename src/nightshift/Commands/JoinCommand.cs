namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift join</c> — clock in. Registers this agent as <c>active</c> on the roster under a shift
/// lease, so a coordinator can see who is on duty before any work is claimed. Idempotent: a re-join just
/// refreshes the lease and status.
/// </summary>
internal static class JoinCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        await Shift.ClockAsync(client, "active", cts.Token);

        Console.WriteLine("JOINED");
        return ExitCode.Ok;
    }
}
