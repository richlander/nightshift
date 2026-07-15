namespace Nightshift.Commands;

using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Output;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift watch</c> — a kubectl-watch-style follow of the coordinator board. Renders the <c>where</c>
/// board and re-renders LIVE as coordination state changes, driven by Turnstile's SSE watch over
/// <c>/plan/</c> (order lifecycle) with NO polling. Read-only and long-running: it never claims or mutates,
/// and Ctrl-C exits cleanly (exit 0). <c>--output table</c> (default) redraws the board on each change;
/// <c>--output jsonl</c> appends one structured row per change event for piping to a dashboard or log.
/// </summary>
internal static class WatchCommand
{
    private const string PlanRoot = "/plan/";

    public static async Task<int> RunAsync(OutputFormat output)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        // Draw the current board first, then follow every change after this revision so the initial state
        // is not replayed as events.
        if (output == OutputFormat.Table)
        {
            Redraw(await client.RangeAsync(PlanRoot, ct), Console.Out);
        }

        long from = await client.CurrentRevisionAsync(ct);

        try
        {
            await RunLoopAsync(
                client.WatchAsync(PlanRoot, from, ct),
                output,
                Console.Out,
                token => client.RangeAsync(PlanRoot, token),
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl-C: a live follow has no natural end, so cancellation is the clean exit.
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// The event→render core, isolated from the socket so it can be driven by a replayable stream in tests.
    /// In <c>jsonl</c> mode each change emits one structured row; in <c>table</c> mode each change redraws the
    /// board from a fresh <paramref name="snapshot"/>.
    /// </summary>
    internal static async Task RunLoopAsync(
        IAsyncEnumerable<WatchSignal> events,
        OutputFormat output,
        TextWriter writer,
        Func<CancellationToken, Task<IReadOnlyList<KvItem>>> snapshot,
        CancellationToken ct)
    {
        await foreach (WatchSignal signal in events.WithCancellation(ct))
        {
            if (output == OutputFormat.Jsonl)
            {
                writer.WriteLine(RenderEvent(signal));
            }
            else
            {
                Redraw(await snapshot(ct), writer);
            }
        }
    }

    /// <summary>Serializes a change signal into a single JSONL row: <c>{revision, key, op}</c>.</summary>
    internal static string RenderEvent(WatchSignal signal)
        => JsonSerializer.Serialize(
            new WatchEvent
            {
                Revision = signal.Revision,
                Key = signal.Key,
                Op = signal.Deleted ? "delete" : "put",
            },
            WatchJsonContext.Default.WatchEvent);

    /// <summary>Clears the screen and reprints the <c>where</c> board so the terminal shows only current state.</summary>
    internal static void Redraw(IReadOnlyList<KvItem> items, TextWriter writer)
    {
        writer.Write("\u001b[2J\u001b[H"); // erase screen + cursor home — alternate-screen style redraw
        List<WhereRow> rows = WhereCommand.BuildRows(items);
        if (rows.Count == 0)
        {
            WhereCommand.RenderEmpty(OutputFormat.Table, writer);
        }
        else
        {
            WhereCommand.RenderRows(rows, OutputFormat.Table, writer);
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(WatchEvent))]
internal partial class WatchJsonContext : JsonSerializerContext
{
}

internal sealed record WatchEvent
{
    public required long Revision { get; init; }
    public required string Key { get; init; }
    public required string Op { get; init; }
}
