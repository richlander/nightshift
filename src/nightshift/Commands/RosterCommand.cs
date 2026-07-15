namespace Nightshift.Commands;

using System.Text.Json.Serialization;
using Markout;
using Nightshift.Output;
using Nightshift.Turnstile;

/// <summary>
/// <c>nightshift roster</c> — who is on duty. Lists every worker's roster entry (<c>/agent/{id}</c>) with
/// its status (<c>active</c> or <c>standby</c>), one row per agent. Read-only: it never touches the roster
/// it reports. The default plaintext view is a human table; <c>--output tsv</c> reproduces the prior
/// tab-separated bytes and <c>json</c>/<c>jsonl</c> expose structured rows. Prints <c>(no agents)</c> when
/// the shift is empty.
/// </summary>
internal static class RosterCommand
{
    private const string AgentRoot = "/agent/";

    public static async Task<int> RunAsync(OutputFormat output)
    {
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        CancellationToken ct = cts.Token;

        using TurnstileClient client = TurnstileClient.Connect(Paths.Socket);
        IReadOnlyList<KvItem> agents = await client.RangeAsync(AgentRoot, ct);

        List<RosterRow> rows = BuildRows(agents);
        if (rows.Count == 0)
        {
            RenderEmpty(output, Console.Out);
            return ExitCode.Ok;
        }

        RenderRows(rows, output, Console.Out);
        return ExitCode.Ok;
    }

    /// <summary>Shapes each <c>/agent/{id}</c> entry into an [agent-id, status] row, preserving range order.</summary>
    internal static List<RosterRow> BuildRows(IEnumerable<KvItem> agents)
        => agents
            .Select(agent => new RosterRow
            {
                AgentId = agent.Key.StartsWith(AgentRoot, StringComparison.Ordinal) ? agent.Key[AgentRoot.Length..] : agent.Key,
                Status = agent.Text,
            })
            .ToList();

    internal static void RenderRows(IReadOnlyList<RosterRow> rows, OutputFormat output, TextWriter writer)
        => OutputFormatter.Render(
            new RosterView { Rows = rows.ToList() },
            rows.ToList(),
            writer,
            output,
            RosterViewContext.Default,
            RosterJsonContext.Default.ListRosterRow);

    internal static void RenderEmpty(OutputFormat output, TextWriter writer)
    {
        switch (output)
        {
            case OutputFormat.Json:
                RenderRows([], OutputFormat.Json, writer);
                break;
            case OutputFormat.Jsonl:
                break;
            case OutputFormat.Plaintext:
            case OutputFormat.Table:
            case OutputFormat.Markdown:
            case OutputFormat.Tsv:
                writer.WriteLine("(no agents)");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(output), output, null);
        }
    }
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
    [MarkoutPropertyName("Agent")]
    public required string AgentId { get; init; }
    public required string Status { get; init; }
}

[MarkoutContext(typeof(RosterView))]
internal partial class RosterViewContext : MarkoutSerializerContext
{
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<RosterRow>))]
internal partial class RosterJsonContext : JsonSerializerContext
{
}
