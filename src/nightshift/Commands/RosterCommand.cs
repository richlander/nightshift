namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift roster</c> — who is on duty. Lists every worker's roster entry (<c>/agent/{id}</c>) with
/// its status (<c>active</c> or <c>standby</c>), one per line. Read-only: it never touches the roster it
/// reports. Prints <c>(no agents)</c> when the shift is empty.
/// </summary>
internal static class RosterCommand
{
    private const string AgentRoot = "/agent/";

    public static async Task<int> RunAsync(string[] args)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        IReadOnlyList<KvItem> agents = await client.RangeAsync(AgentRoot, ct);

        if (agents.Count == 0)
        {
            Console.WriteLine("(no agents)");
            return ExitCode.Ok;
        }

        foreach (KvItem agent in agents)
        {
            string id = agent.Key.StartsWith(AgentRoot, StringComparison.Ordinal) ? agent.Key[AgentRoot.Length..] : agent.Key;
            Console.WriteLine($"{id}\t{agent.Text}");
        }

        return ExitCode.Ok;
    }
}
