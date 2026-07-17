namespace Nightshift.Commands;

using System.Text.Json;
using System.Text.Json.Serialization;
using Nightshift.Output;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift watch</c> — a kubectl-watch-style follow of the coordinator board. Renders the <c>where</c>
/// board and re-renders LIVE as coordination state changes, driven by Turnstile's SSE watch over
/// <c>/plan/</c> (order lifecycle) with NO polling. Read-only and long-running: it never claims or mutates,
/// and Ctrl-C exits cleanly (exit 0). <c>--output table</c> (default) redraws the board on each change,
/// hiding <c>landed</c> orders unless <c>--all</c>/<c>-a</c> is supplied;
/// <c>--output jsonl</c> appends one structured row per change event for piping to a dashboard or log.
/// </summary>
internal static class WatchCommand
{
    private const string PlanRoot = "/plan/";

    /// <summary>Status value for orders that have been fully landed; filtered from the live table by default.</summary>
    private const string LandedStatus = "landed";

    /// <param name="output">Selects table (live redraw) or jsonl (raw change-event stream) output.</param>
    /// <param name="showAll">When <c>true</c>, landed orders are included in the table redraw (<c>--all</c>/<c>-a</c>).</param>
    public static async Task<int> RunAsync(OutputFormat output, bool showAll = false)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);

        try
        {
            // Establish the watch boundary BEFORE snapshotting, not after: reading the revision first
            // guarantees the snapshot reflects state at or after `from`, and the watch backlog (from `from`
            // exclusive) covers every change beyond it. The two windows can only overlap — which costs an
            // idempotent redraw — never leave a gap. Reading the revision after the range would let any
            // write in between fall into neither the snapshot nor the backlog, silently missing it.
            long from = await client.CurrentRevisionAsync(ct);

            if (output == OutputFormat.Table)
            {
                Redraw(await client.RangeAsync(PlanRoot, ct), Console.Out, showAll);
            }

            await RunLoopWithRecoveryAsync(
                from,
                (revision, token) => client.WatchAsync(PlanRoot, revision, token),
                output,
                Console.Out,
                token => client.RangeAsync(PlanRoot, token),
                client.CurrentRevisionAsync,
                showAll,
                ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Ctrl-C anywhere — the startup handshake or the live follow — is the clean exit (a follow has
            // no natural end), so cancellation resolves to exit 0.
        }

        return ExitCode.Ok;
    }

    /// <summary>
    /// The event→render core, isolated from the socket so it can be driven by a replayable stream in tests.
    /// In <c>jsonl</c> mode each change emits one structured row (always unfiltered); in <c>table</c> mode
    /// each change redraws the board from a fresh <paramref name="snapshot"/>, hiding landed orders unless
    /// <paramref name="showAll"/> is <c>true</c>.
    /// </summary>
    internal static async Task RunLoopAsync(
        IAsyncEnumerable<WatchSignal> events,
        OutputFormat output,
        TextWriter writer,
        Func<CancellationToken, Task<IReadOnlyList<KvItem>>> snapshot,
        bool showAll,
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
                Redraw(await snapshot(ct), writer, showAll);
            }
        }
    }

    internal static async Task RunLoopWithRecoveryAsync(
        long fromRevision,
        Func<long, CancellationToken, IAsyncEnumerable<WatchSignal>> watch,
        OutputFormat output,
        TextWriter writer,
        Func<CancellationToken, Task<IReadOnlyList<KvItem>>> snapshot,
        Func<CancellationToken, Task<long>> currentRevision,
        bool showAll,
        CancellationToken ct)
    {
        long from = fromRevision;
        while (true)
        {
            try
            {
                await RunLoopAsync(
                    watch(from, ct),
                    output,
                    writer,
                    snapshot,
                    showAll,
                    ct);
                return;
            }
            catch (WatchCompactedException)
            {
                // Canonical level-triggered recovery: re-range and re-watch from a fresh revision floor.
                from = await currentRevision(ct);
                IReadOnlyList<KvItem> fresh = await snapshot(ct);
                if (output == OutputFormat.Table)
                {
                    Redraw(fresh, writer, showAll);
                }
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

    /// <summary>
    /// Clears the screen and reprints the <c>where</c> board so the terminal shows only current state.
    /// When <paramref name="showAll"/> is <c>false</c> (the default), orders whose status is
    /// <c>landed</c> are omitted from the redraw.
    /// </summary>
    internal static void Redraw(IReadOnlyList<KvItem> items, TextWriter writer, bool showAll = false)
    {
        writer.Write("\u001b[2J\u001b[H"); // erase screen + cursor home — alternate-screen style redraw
        List<WhereRow> rows = WhereCommand.BuildRows(items);
        if (!showAll)
        {
            rows = rows.Where(r => r.Status != LandedStatus).ToList();
        }

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
