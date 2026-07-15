namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift show</c> — reprint the current claim's WORK packet. State lives in Turnstile and the
/// worktree, never in the model, so an agent that compacted or resumed can recover exactly what it is
/// working on WITHOUT claiming again. Read-only: no lease renewal, no state change.
/// </summary>
internal static class ShowCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift show: no active claim (run `nightshift next` first)");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        KvItem? spec = await client.GetAsync($"{session.OrderBase}/spec", ct);
        OrderView view = spec is null ? OrderView.Empty : OrderView.Parse(spec.Text);
        view.PrintWork(Console.Out, session.OrderBase, session.Fence);
        return ExitCode.Ok;
    }
}
