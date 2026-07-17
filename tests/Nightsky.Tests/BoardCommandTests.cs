namespace Nightsky.Tests;

using System.Text;
using Nightsky.Commands;
using Nightsky.Turnstile;
using Xunit;

public class BoardCommandTests
{
    [Fact]
    public void BuildRows_JoinsPlanes_AndUsesMissingPlaceholders()
    {
        KvItem[] planItems =
        [
            Item("/plan/1/order/op-a/state", "{\"status\":\"done\"}"),
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/claim", "worker-a", lease: "lease-1"),
            Item("/plan/1/order/op-a/pr", "opaque-pr"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"blocked\"}"),
        ];

        List<BoardRow> rows = BoardCommand.BuildRows(planItems, showAll: true);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("/plan/1/order/op-a", row.OrderBase);
                Assert.Equal("worker-a (lease-1 ✓)", row.ClaimLease);
                Assert.Equal("done", row.State);
                Assert.Equal("nightshift/1/op-a", row.Branch);
                Assert.Equal("opaque-pr", row.Pr);
            },
            row =>
            {
                Assert.Equal("/plan/1/order/op-b", row.OrderBase);
                Assert.Equal("—", row.ClaimLease);
                Assert.Equal("blocked", row.State);
                Assert.Equal("—", row.Branch);
                Assert.Equal("—", row.Pr);
            });
    }

    [Fact]
    public void BuildRows_Default_HidesLanded_AndShowAllIncludesIt()
    {
        KvItem[] planItems =
        [
            Item("/plan/1/order/op-a/state", "{\"status\":\"landed\"}"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"done\"}"),
        ];

        List<BoardRow> hidden = BoardCommand.BuildRows(planItems, showAll: false);
        List<BoardRow> shown = BoardCommand.BuildRows(planItems, showAll: true);

        Assert.Single(hidden);
        Assert.Equal("/plan/1/order/op-b", hidden[0].OrderBase);
        Assert.Equal(2, shown.Count);
    }

    [Fact]
    public void BuildEscalations_ReturnsEscalatedOrders()
    {
        KvItem[] planItems =
        [
            Item("/plan/1/order/op-a/state", "{\"status\":\"escalated\"}"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"done\"}"),
        ];

        List<EscalationRow> escalations = BoardCommand.BuildEscalations(planItems);

        Assert.Single(escalations);
        Assert.Equal("/plan/1/order/op-a", escalations[0].OrderBase);
    }

    [Fact]
    public void BuildControlRows_RendersFlagStates()
    {
        KvItem[] controls =
        [
            Item("/control/halt", "1"),
        ];

        ControlFlags flags = BoardCommand.BuildControlFlags(controls);
        List<ControlRow> rows = BoardCommand.BuildControlRows(flags);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("/control/halt", row.Flag);
                Assert.Equal("set", row.State);
            },
            row =>
            {
                Assert.Equal("/control/draining", row.Flag);
                Assert.Equal("clear", row.State);
            });
    }

    [Theory]
    [InlineData(false, "put")]
    [InlineData(true, "delete")]
    public void RenderEvent_EmitsJsonl(bool deleted, string op)
    {
        string row = BoardCommand.RenderEvent(new WatchSignal("/plan/1/order/op-a/state", deleted, 17));

        Assert.Equal($"{{\"revision\":17,\"key\":\"/plan/1/order/op-a/state\",\"op\":\"{op}\"}}", row);
    }

    [Fact]
    public void Redraw_ClearsScreenAndPrintsSections()
    {
        var snapshot = new BoardSnapshot
        {
            Orders =
            [
                new BoardRow
                {
                    OrderBase = "/plan/1/order/op-a",
                    ClaimLease = "worker-a (lease-1 ✓)",
                    State = "claimed",
                    Branch = "nightshift/1/op-a",
                    Pr = "—",
                },
            ],
            ReadySet =
            [
                new ReadyRow
                {
                    ReadyKey = "/ready/1/op-a",
                    OrderBase = "/plan/1/order/op-a",
                },
            ],
            Roster =
            [
                new RosterRow
                {
                    Agent = "worker-a",
                    Status = "active",
                },
            ],
            Escalations = [],
            ControlFlags =
            [
                new ControlRow { Flag = "/control/halt", State = "clear" },
                new ControlRow { Flag = "/control/draining", State = "clear" },
            ],
        };

        using var writer = new StringWriter();
        BoardCommand.Redraw(snapshot, writer);

        string output = writer.ToString();
        Assert.StartsWith("\u001b[2J\u001b[H", output, StringComparison.Ordinal);
        Assert.Contains("orders", output);
        Assert.Contains("roster", output);
        Assert.Contains("ready set", output);
        Assert.Contains("control flags", output);
        Assert.Contains("/plan/1/order/op-a", output);
    }

    private static KvItem Item(string key, string text, string? lease = null)
        => new(key, 1, 1, lease, Encoding.UTF8.GetBytes(text));
}
