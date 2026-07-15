namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Output;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The pure row derivation and multi-format rendering behind <c>nightshift roster</c>: <c>/agent/{id}</c>
/// entries become [agent-id, status] rows, the empty shift keeps its <c>(no agents)</c> sentinel, and the
/// tsv/json shapes stay stable for machine consumers.
/// </summary>
public class RosterCommandTests
{
    [Fact]
    public void BuildRows_StripsAgentPrefixAndKeepsStatus()
    {
        KvItem[] agents =
        [
            Item("/agent/alice", "active"),
            Item("/agent/bob", "standby"),
        ];

        List<RosterRow> rows = RosterCommand.BuildRows(agents);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("alice", row.AgentId);
                Assert.Equal("active", row.Status);
            },
            row =>
            {
                Assert.Equal("bob", row.AgentId);
                Assert.Equal("standby", row.Status);
            });
    }

    [Fact]
    public void RenderRows_TsvMatchesLegacyBytes()
    {
        List<RosterRow> rows =
        [
            new() { AgentId = "alice", Status = "active" },
            new() { AgentId = "bob", Status = "standby" },
        ];

        using var writer = new StringWriter();
        RosterCommand.RenderRows(rows, OutputFormat.Tsv, writer);

        string expected = "alice\tactive\nbob\tstandby\n";
        Assert.Equal(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(writer.ToString()));
    }

    [Fact]
    public void RenderRows_JsonFormatsUseSnakeCaseRows()
    {
        List<RosterRow> rows =
        [
            new() { AgentId = "alice", Status = "active" },
        ];

        using var json = new StringWriter();
        RosterCommand.RenderRows(rows, OutputFormat.Json, json);

        using var jsonl = new StringWriter();
        RosterCommand.RenderRows(rows, OutputFormat.Jsonl, jsonl);

        Assert.Equal("[{\"agent_id\":\"alice\",\"status\":\"active\"}]\n", json.ToString());
        Assert.Equal("{\"agent_id\":\"alice\",\"status\":\"active\"}\n", jsonl.ToString());
    }

    [Fact]
    public void RenderEmpty_PlaintextEmitsNoAgentsSentinel()
    {
        using var writer = new StringWriter();
        RosterCommand.RenderEmpty(OutputFormat.Plaintext, writer);

        Assert.Equal($"(no agents){Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void RenderEmpty_JsonEmitsEmptyArray()
    {
        using var writer = new StringWriter();
        RosterCommand.RenderEmpty(OutputFormat.Json, writer);

        Assert.Equal($"[]{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void RenderEmpty_JsonlEmitsNothing()
    {
        using var writer = new StringWriter();
        RosterCommand.RenderEmpty(OutputFormat.Jsonl, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    private static KvItem Item(string key, string text)
        => new(key, 1, 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(text));
}
