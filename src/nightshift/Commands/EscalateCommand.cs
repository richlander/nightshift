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
    /// <summary>
    /// The escalation-reason prefix for a <b>prereq-unreachable</b> base ref (stacked orders §4). A worker
    /// that cannot reach its <c>base-ref</c> in the local object database self-raises this on the existing
    /// andon cord; the coordinator resolves it by publishing that base to origin. The prefix lets both
    /// <c>check</c> (re-arm the parked worker once the base is reachable) and <c>coordinate</c> (surface a
    /// publish-the-base transition apart from a judgment escalation) recognise it without a new mechanism.
    /// </summary>
    internal const string PrereqUnreachablePrefix = "prereq-unreachable:";

    /// <summary>Formats the standing reason a worker records when its base ref is not reachable locally.</summary>
    internal static string PrereqUnreachableReason(string baseRef, string branch)
        => $"{PrereqUnreachablePrefix} base-ref '{baseRef}' for branch '{branch}' is not reachable in the local object database. Coordinator: publish it to origin so this worker can fetch it and proceed.";

    /// <summary>True when <paramref name="reason"/> is a prereq-unreachable escalation (see <see cref="PrereqUnreachablePrefix"/>).</summary>
    internal static bool IsPrereqUnreachableReason(string? reason)
        => reason is not null && reason.TrimStart().StartsWith(PrereqUnreachablePrefix, StringComparison.Ordinal);

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
