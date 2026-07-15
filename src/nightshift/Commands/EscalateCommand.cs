namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift escalate --reason "..."</c> — stop and ask for judgment ("pull the andon cord"). Records
/// <c>state=escalated</c> with the reason but, unlike <c>release</c>, KEEPS the claim and lease: escalate
/// PAUSES on the order awaiting an answer, it does not hand it back. Because the reconciler treats
/// <c>escalated</c> as ineligible, the order is never auto-redispatched — even if this agent then exits, it
/// waits for a human. The answer returns through the order's <c>directive</c> key, surfaced by
/// <c>check</c> as QUERY.
/// </summary>
internal static class EscalateCommand
{
    public static async Task<int> RunAsync(string[] args)
        => await RunAsync(Options.Value(args, "--reason"));

    public static async Task<int> RunAsync(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            Console.Error.WriteLine("nightshift escalate: --reason is required (say what needs judgment)");
            return ExitCode.Usage;
        }

        SessionState? session = Session.Load();
        if (session is null)
        {
            Console.Error.WriteLine("nightshift escalate: no active claim (nothing to escalate)");
            return ExitCode.NoClaim;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Mark the order as needing judgment. The claim and lease stay put — the agent may keep calling
        // `check` to receive the answer (a directive → QUERY), or exit and let a human intervene. Either
        // way `escalated` gates re-dispatch, so the order is not handed to another agent behind our back.
        await OrderState.WriteAsync(client, session.OrderBase, "escalated", reason, Session.Identity, ct);

        Console.WriteLine("ESCALATED");
        return ExitCode.Ok;
    }
}
