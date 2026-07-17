namespace Nightsky.Commands;

using System.Text.Json;
using System.Text.Json.Serialization;
using Markout;
using Markout.Formatting;
using Nightsky.Output;
using Nightsky.Turnstile;
using SocketException = System.Net.Sockets.SocketException;

internal static class BoardCommand
{
    private const string PlanRoot = "/plan/";
    private const string ReadyRoot = "/ready/";
    private const string AgentRoot = "/agent/";
    private const string ControlRoot = "/control/";
    private const string HaltKey = "/control/halt";
    private const string DrainingKey = "/control/draining";
    private const string StateSuffix = "/state";
    private const string BranchSuffix = "/branch";
    private const string DirectiveSuffix = "/directive";
    private const string ClaimSuffix = "/claim";
    private const string PrSuffix = "/pr";
    private const string LandedStatus = "landed";
    private const string EscalatedStatus = "escalated";
    private const string MissingValue = "—";

    public static async Task<int> RunAsync(
        string socketPath,
        string? rawScope,
        bool watch,
        bool showAll,
        OutputFormat output)
    {
        if (!ScopeSelector.TryParse(rawScope, out ScopeSelector scope, out string? scopeError))
        {
            Console.Error.WriteLine(scopeError);
            return ExitCode.Usage;
        }

        if (watch && output == OutputFormat.Json)
        {
            Console.Error.WriteLine("--watch supports table or jsonl output");
            return ExitCode.Usage;
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(socketPath);
        try
        {
            if (watch)
            {
                await RunWatchAsync(client, scope, showAll, output, Console.Out, ct);
            }
            else
            {
                BoardSnapshot snapshot = await CreateSnapshotAsync(client, scope, showAll, ct);
                RenderSnapshot(snapshot, output, Console.Out);
            }

            return ExitCode.Ok;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return ExitCode.Ok;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            Console.Error.WriteLine($"ERROR: failed to read Turnstile socket at '{socketPath}': {ex.Message}");
            return ExitCode.Error;
        }
    }

    internal static async Task RunWatchAsync(
        TurnstileClient client,
        ScopeSelector scope,
        bool showAll,
        OutputFormat output,
        TextWriter writer,
        CancellationToken ct)
    {
        while (true)
        {
            // Watch boundary before snapshot: backlog can overlap the ranged view but never leave a gap.
            long fromRevision = await client.CurrentRevisionAsync(ct);
            BoardSnapshot snapshot = await CreateSnapshotAsync(client, scope, showAll, ct);

            if (output == OutputFormat.Table)
            {
                Redraw(snapshot, writer);
            }

            try
            {
                await foreach (WatchSignal signal in client.WatchAsync("/", fromRevision, ct))
                {
                    if (!scope.MatchesBoardKey(signal.Key))
                    {
                        continue;
                    }

                    if (output == OutputFormat.Jsonl)
                    {
                        writer.WriteLine(RenderEvent(signal));
                    }
                    else
                    {
                        Redraw(await CreateSnapshotAsync(client, scope, showAll, ct), writer);
                    }
                }

                return;
            }
            catch (WatchCompactedException)
            {
                // Level-triggered recovery for compaction (HTTP 410): re-range from a fresh revision,
                // then re-establish the watch.
            }
        }
    }

    internal static async Task<BoardSnapshot> CreateSnapshotAsync(
        TurnstileClient client,
        ScopeSelector scope,
        bool showAll,
        CancellationToken ct)
    {
        Task<IReadOnlyList<KvItem>> planTask = client.RangeAsync(scope.PlanPrefix, ct);
        Task<IReadOnlyList<KvItem>> readyTask = client.RangeAsync(scope.ReadyPrefix, ct);
        Task<IReadOnlyList<KvItem>> agentTask = client.RangeAsync(AgentRoot, ct);
        Task<IReadOnlyList<KvItem>> controlTask = client.RangeAsync(ControlRoot, ct);

        await Task.WhenAll(planTask, readyTask, agentTask, controlTask);

        IReadOnlyList<KvItem> planItems = await planTask;
        IReadOnlyList<KvItem> readyItems = await readyTask;
        IReadOnlyList<KvItem> agentItems = await agentTask;
        IReadOnlyList<KvItem> controlItems = await controlTask;

        return new BoardSnapshot
        {
            Orders = BuildRows(planItems, showAll),
            ReadySet = BuildReadyRows(readyItems, scope),
            Roster = BuildRosterRows(agentItems),
            Escalations = BuildEscalations(planItems),
            ControlFlags = BuildControlRows(BuildControlFlags(controlItems)),
        };
    }

    internal static List<BoardRow> BuildRows(IEnumerable<KvItem> planItems, bool showAll)
        => ProjectOrders(planItems)
            .Where(order => showAll || order.Status != LandedStatus)
            .Select(order => new BoardRow
            {
                OrderBase = order.OrderBase,
                ClaimLease = FormatClaim(order.ClaimHolder, order.ClaimLease),
                State = FormatState(order.Status, order.Directive),
                Branch = string.IsNullOrWhiteSpace(order.Branch) ? MissingValue : order.Branch,
                Pr = string.IsNullOrWhiteSpace(order.Pr) ? MissingValue : order.Pr,
            })
            .ToList();

    internal static List<EscalationRow> BuildEscalations(IEnumerable<KvItem> planItems)
        => ProjectOrders(planItems)
            .Where(order => order.Status == EscalatedStatus)
            .Select(order => new EscalationRow { OrderBase = order.OrderBase })
            .ToList();

    internal static ControlFlags BuildControlFlags(IEnumerable<KvItem> controlItems)
    {
        bool halt = false;
        bool draining = false;
        foreach (KvItem item in controlItems)
        {
            if (item.Key == HaltKey)
            {
                halt = true;
            }
            else if (item.Key == DrainingKey)
            {
                draining = true;
            }
        }

        return new ControlFlags(halt, draining);
    }

    internal static List<ControlRow> BuildControlRows(ControlFlags flags)
    {
        return
        [
            new ControlRow { Flag = HaltKey, State = flags.Halt ? "set" : "clear" },
            new ControlRow { Flag = DrainingKey, State = flags.Draining ? "set" : "clear" },
        ];
    }

    internal static string RenderEvent(WatchSignal signal)
        => JsonSerializer.Serialize(
            new WatchEvent
            {
                Revision = signal.Revision,
                Key = signal.Key,
                Op = signal.Deleted ? "delete" : "put",
            },
            BoardJsonContext.Default.WatchEvent);

    internal static void RenderSnapshot(BoardSnapshot snapshot, OutputFormat output, TextWriter writer)
    {
        switch (output)
        {
            case OutputFormat.Table:
                RenderSnapshotTable(snapshot, writer);
                break;
            case OutputFormat.Json:
            case OutputFormat.Jsonl:
                writer.WriteLine(JsonSerializer.Serialize(snapshot, BoardJsonContext.Default.BoardSnapshot));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(output), output, null);
        }
    }

    internal static void Redraw(BoardSnapshot snapshot, TextWriter writer)
    {
        writer.Write("\u001b[2J\u001b[H");
        RenderSnapshotTable(snapshot, writer);
    }

    private static void RenderSnapshotTable(BoardSnapshot snapshot, TextWriter writer)
    {
        writer.WriteLine("orders");
        if (snapshot.Orders.Count == 0)
        {
            writer.WriteLine("(no orders)");
        }
        else
        {
            MarkoutSerializer.Serialize(new BoardView { Rows = snapshot.Orders }, writer, new TableFormatter(showHeader: true), BoardViewContext.Default);
        }

        writer.WriteLine();
        writer.WriteLine("roster");
        if (snapshot.Roster.Count == 0)
        {
            writer.WriteLine("(none)");
        }
        else
        {
            MarkoutSerializer.Serialize(new RosterView { Rows = snapshot.Roster }, writer, new TableFormatter(showHeader: true), RosterViewContext.Default);
        }

        writer.WriteLine();
        writer.WriteLine("ready set");
        if (snapshot.ReadySet.Count == 0)
        {
            writer.WriteLine("(none)");
        }
        else
        {
            MarkoutSerializer.Serialize(new ReadySetView { Rows = snapshot.ReadySet }, writer, new TableFormatter(showHeader: true), ReadySetViewContext.Default);
        }

        writer.WriteLine();
        writer.WriteLine("escalations");
        if (snapshot.Escalations.Count == 0)
        {
            writer.WriteLine("(none)");
        }
        else
        {
            MarkoutSerializer.Serialize(new EscalationsView { Rows = snapshot.Escalations }, writer, new TableFormatter(showHeader: true), EscalationsViewContext.Default);
        }

        writer.WriteLine();
        writer.WriteLine("control flags");
        MarkoutSerializer.Serialize(new ControlView { Rows = snapshot.ControlFlags }, writer, new TableFormatter(showHeader: true), ControlViewContext.Default);
    }

    private static List<ReadyRow> BuildReadyRows(IEnumerable<KvItem> readyItems, ScopeSelector scope)
        => readyItems
            .Where(item => scope.MatchesReadyKey(item.Key))
            .Select(item =>
            {
                OrderRef? order = OrderRef.FromReadyKey(item.Key);
                if (order is not OrderRef parsed || !scope.IncludesReadyOrder(parsed))
                {
                    return null;
                }

                return new ReadyRow
                {
                    ReadyKey = item.Key,
                    OrderBase = item.Text.Trim(),
                };
            })
            .Where(static row => row is not null)
            .Cast<ReadyRow>()
            .ToList();

    private static List<RosterRow> BuildRosterRows(IEnumerable<KvItem> rosterItems)
        => rosterItems
            .Where(item => item.Key.StartsWith(AgentRoot, StringComparison.Ordinal))
            .Select(item => new RosterRow
            {
                Agent = item.Key[AgentRoot.Length..],
                Status = item.Text.Trim(),
            })
            .ToList();

    private static List<OrderProjection> ProjectOrders(IEnumerable<KvItem> planItems)
    {
        var statuses = new Dictionary<string, string>(StringComparer.Ordinal);
        var branches = new Dictionary<string, string>(StringComparer.Ordinal);
        var directives = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimHolders = new Dictionary<string, string>(StringComparer.Ordinal);
        var claimLeases = new Dictionary<string, string?>(StringComparer.Ordinal);
        var prs = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (KvItem item in planItems)
        {
            if (BaseOf(item.Key, StateSuffix) is { } stateBase)
            {
                statuses[stateBase] = ParseStatus(item.Text);
            }
            else if (BaseOf(item.Key, BranchSuffix) is { } branchBase)
            {
                branches[branchBase] = item.Text.Trim();
            }
            else if (BaseOf(item.Key, DirectiveSuffix) is { } directiveBase)
            {
                directives[directiveBase] = item.Text.Trim();
            }
            else if (BaseOf(item.Key, ClaimSuffix) is { } claimBase)
            {
                claimHolders[claimBase] = item.Text.Trim();
                claimLeases[claimBase] = item.Lease;
            }
            else if (BaseOf(item.Key, PrSuffix) is { } prBase)
            {
                prs[prBase] = item.Text.Trim();
            }
        }

        var bases = new SortedSet<string>(statuses.Keys, StringComparer.Ordinal);
        bases.UnionWith(branches.Keys);
        bases.UnionWith(directives.Keys);
        bases.UnionWith(claimHolders.Keys);
        bases.UnionWith(prs.Keys);

        return bases
            .Select(orderBase => new OrderProjection(
                orderBase,
                statuses.TryGetValue(orderBase, out string? status) ? status : "claimed",
                branches.TryGetValue(orderBase, out string? branch) ? branch : string.Empty,
                claimHolders.TryGetValue(orderBase, out string? holder) ? holder : null,
                claimLeases.TryGetValue(orderBase, out string? lease) ? lease : null,
                prs.TryGetValue(orderBase, out string? pr) ? pr : null,
                directives.TryGetValue(orderBase, out string? directive) ? directive : null))
            .ToList();
    }

    private static string? BaseOf(string key, string suffix)
    {
        if (!key.EndsWith(suffix, StringComparison.Ordinal))
        {
            return null;
        }

        string orderBase = key[..^suffix.Length];
        return OrderRef.FromBase(orderBase) is null ? null : orderBase;
    }

    private static string ParseStatus(string stateJson)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stateJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out JsonElement status)
                && status.ValueKind == JsonValueKind.String)
            {
                return status.GetString() ?? "?";
            }
        }
        catch (JsonException)
        {
        }

        return "?";
    }

    private static string FormatClaim(string? holder, string? lease)
    {
        if (string.IsNullOrWhiteSpace(holder))
        {
            return MissingValue;
        }

        string trimmedHolder = holder.Trim();
        // Design open item: exact lease staleness thresholds are undecided; MVP reports simple presence.
        return string.IsNullOrWhiteSpace(lease)
            ? $"{trimmedHolder} (lease {MissingValue})"
            : $"{trimmedHolder} ({lease} ✓)";
    }

    private static string FormatState(string status, string? directive)
        => string.IsNullOrWhiteSpace(directive)
            ? status
            : $"{status} (QUERY: {directive.Trim()})";

    private sealed record OrderProjection(
        string OrderBase,
        string Status,
        string Branch,
        string? ClaimHolder,
        string? ClaimLease,
        string? Pr,
        string? Directive);
}

internal sealed record ControlFlags(bool Halt, bool Draining);

[MarkoutSerializable(AutoFields = false)]
internal sealed class BoardView
{
    [MarkoutSection(Headless = true)]
    public required List<BoardRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record BoardRow
{
    [MarkoutPropertyName("Order")]
    public required string OrderBase { get; init; }

    [MarkoutPropertyName("Claim/lease")]
    public required string ClaimLease { get; init; }

    public required string State { get; init; }

    public required string Branch { get; init; }

    [MarkoutPropertyName("PR")]
    public required string Pr { get; init; }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class ReadySetView
{
    [MarkoutSection(Headless = true)]
    public required List<ReadyRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record ReadyRow
{
    [MarkoutPropertyName("Ready key")]
    public required string ReadyKey { get; init; }

    [MarkoutPropertyName("Order base")]
    public required string OrderBase { get; init; }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class RosterView
{
    [MarkoutSection(Headless = true)]
    public required List<RosterRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record RosterRow
{
    public required string Agent { get; init; }

    public required string Status { get; init; }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class EscalationsView
{
    [MarkoutSection(Headless = true)]
    public required List<EscalationRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record EscalationRow
{
    [MarkoutPropertyName("Order base")]
    public required string OrderBase { get; init; }
}

[MarkoutSerializable(AutoFields = false)]
internal sealed class ControlView
{
    [MarkoutSection(Headless = true)]
    public required List<ControlRow> Rows { get; init; }
}

[MarkoutSerializable]
internal sealed record ControlRow
{
    [MarkoutPropertyName("Flag")]
    public required string Flag { get; init; }

    [MarkoutPropertyName("State")]
    public required string State { get; init; }
}

internal sealed record BoardSnapshot
{
    public required List<BoardRow> Orders { get; init; }

    public required List<ReadyRow> ReadySet { get; init; }

    public required List<RosterRow> Roster { get; init; }

    public required List<EscalationRow> Escalations { get; init; }

    public required List<ControlRow> ControlFlags { get; init; }
}

internal sealed record WatchEvent
{
    public required long Revision { get; init; }

    public required string Key { get; init; }

    public required string Op { get; init; }
}

[MarkoutContext(typeof(BoardView))]
internal partial class BoardViewContext : MarkoutSerializerContext
{
}

[MarkoutContext(typeof(ReadySetView))]
internal partial class ReadySetViewContext : MarkoutSerializerContext
{
}

[MarkoutContext(typeof(RosterView))]
internal partial class RosterViewContext : MarkoutSerializerContext
{
}

[MarkoutContext(typeof(EscalationsView))]
internal partial class EscalationsViewContext : MarkoutSerializerContext
{
}

[MarkoutContext(typeof(ControlView))]
internal partial class ControlViewContext : MarkoutSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(BoardSnapshot))]
[JsonSerializable(typeof(WatchEvent))]
internal partial class BoardJsonContext : JsonSerializerContext
{
}
