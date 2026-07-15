namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift stop [--resume]</c> — the emergency gesture: every worker halts at its next <c>check</c>
/// and nothing new commits. Sets the durable <c>/control/halt</c> flag; <c>--resume</c> lifts it. Use
/// <c>drain</c> for the graceful wind-down; <c>stop</c> is the pull-the-cord case.
/// </summary>
internal static class StopCommand
{
    private const string HaltKey = "/control/halt";

    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(Array.IndexOf(args, "--resume") >= 0);

    public static async Task<int> RunAsync(bool resume)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        return await Control.ToggleAsync(client, resume, HaltKey, "HALT", "LIFTED", cts.Token);
    }
}
