namespace Nightshift.Commands;

using System.Text.Json;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift rework &lt;order&gt; [--reason ...] [--reason-file ...]</c> — send a review-rejected order back
/// for another pass. A COORDINATOR verb (sibling of <c>land</c>): after an order was released <c>done</c>
/// (submitted, awaiting merge) but FAILED review, this returns it to the claimable pool as the non-terminal
/// <c>changes-requested</c> status, carrying the review findings, WITHOUT discarding the worker's pushed
/// work — <c>{base}/branch</c> and <c>{base}/claim</c> are left untouched so the re-claiming worker
/// CONTINUES the existing branch. Pure Turnstile writes: no git, no checkout. <c>&lt;order&gt;</c> is the base
/// path <c>next</c> printed, e.g. <c>/plan/1234/order/op4</c>.
/// </summary>
internal static class ReworkCommand
{
    private const string DefaultReason = "changes requested";

    public static async Task<int> RunAsync(string? orderBase, string? reason, string? reasonFile)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        return await RunAsync(client, orderBase, reason, reasonFile, Session.Identity, ct);
    }

    /// <summary>The pure-Turnstile core, separated so tests can drive it with a live daemon client and identity.</summary>
    internal static async Task<int> RunAsync(TurnstileClient client, string? orderBase, string? reason, string? reasonFile, string by, CancellationToken ct)
    {
        if (orderBase is null || !orderBase.StartsWith("/plan/", StringComparison.Ordinal))
        {
            Console.Error.WriteLine("usage: nightshift rework <order-base> [--reason <text>] [--reason-file <path>]   (e.g. /plan/1234/order/op4)");
            return ExitCode.Usage;
        }

        if (await client.GetAsync($"{orderBase}/spec", ct) is null)
        {
            Console.Error.WriteLine($"nightshift rework: no such order: {orderBase}");
            return ExitCode.Usage;
        }

        // Only a submitted-but-unlanded order may be sent back: `done` (awaiting merge) or an already
        // reworked order (idempotent refresh). Never `landed` (merged), never a fresh/claimed order.
        string? status = await StatusOfAsync(client, orderBase, ct);
        if (status != "done" && status != OrderView.ChangesRequested)
        {
            Console.Error.WriteLine(
                $"nightshift rework: order must be 'done' (submitted, awaiting merge) to send back for rework; it is '{status ?? "unstarted"}'");
            return ExitCode.Usage;
        }

        if (await ResolveFindingsAsync(reason, reasonFile, ct) is not { } findings)
        {
            return ExitCode.Usage;
        }

        string shortReason = reason is { Length: > 0 } ? reason : DefaultReason;

        // Write the findings brief BEFORE the state flip: any observer (`next`/`show`) that later sees
        // `changes-requested` is then guaranteed the `{base}/rework` key already exists, so a rework packet
        // never carries `mode: rework` without `findings:`. The claim/branch keys are left as-is so the
        // order re-serves on its existing branch; the live `plan` controller reconciles it back to ready.
        await client.SetAsync($"{orderBase}/rework", findings, ct);
        await OrderState.WriteAsync(client, orderBase, OrderView.ChangesRequested, shortReason, by, ct);

        Console.WriteLine($"REWORK {orderBase}");
        return ExitCode.Ok;
    }

    /// <summary>The full findings brief: the <c>--reason-file</c> contents when given, else the short <c>--reason</c>.</summary>
    private static async Task<string?> ResolveFindingsAsync(string? reason, string? reasonFile, CancellationToken ct)
    {
        if (reasonFile is { Length: > 0 })
        {
            if (!File.Exists(reasonFile))
            {
                Console.Error.WriteLine($"nightshift rework: --reason-file not found: {reasonFile}");
                return null;
            }

            string contents;
            try
            {
                contents = await File.ReadAllTextAsync(reasonFile, ct);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"nightshift rework: --reason-file unreadable: {reasonFile}");
                return null;
            }

            // A rework must carry real findings; an empty/whitespace file would yield an empty `{base}/rework`
            // and a packet with `mode: rework` but no `findings:` line. Reject it as a usage error.
            if (string.IsNullOrWhiteSpace(contents))
            {
                Console.Error.WriteLine($"nightshift rework: --reason-file is empty: {reasonFile}");
                return null;
            }

            return contents;
        }

        return reason is { Length: > 0 } ? reason : DefaultReason;
    }

    private static async Task<string?> StatusOfAsync(TurnstileClient client, string orderBase, CancellationToken ct)
    {
        KvItem? state = await client.GetAsync($"{orderBase}/state", ct);
        if (state is null)
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(state.Text);
            return doc.RootElement.TryGetProperty("status", out JsonElement s) ? s.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
