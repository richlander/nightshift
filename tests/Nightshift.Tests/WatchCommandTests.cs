namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Output;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The event→render core of <c>nightshift watch</c>, driven by a replayable stream instead of a live
/// daemon: <c>jsonl</c> emits one structured row per change, and <c>table</c> redraws the <c>where</c> board
/// from a fresh snapshot on each change.
/// </summary>
public class WatchCommandTests
{
    [Theory]
    [InlineData(false, "put")]
    [InlineData(true, "delete")]
    public void RenderEvent_MapsSignalToJsonlRow(bool deleted, string op)
    {
        string row = WatchCommand.RenderEvent(new WatchSignal("/plan/1/order/op-a/state", deleted, 42));

        Assert.Equal(
            $"{{\"revision\":42,\"key\":\"/plan/1/order/op-a/state\",\"op\":\"{op}\"}}",
            row);
    }

    [Fact]
    public async Task RunLoop_Jsonl_EmitsOneRowPerEvent()
    {
        WatchSignal[] events =
        [
            new("/plan/1/order/op-a/branch", Deleted: false, 5),
            new("/plan/1/order/op-a/state", Deleted: false, 6),
            new("/plan/1/order/op-a/claim", Deleted: true, 7),
        ];

        using var writer = new StringWriter();
        await WatchCommand.RunLoopAsync(
            Replay(events),
            OutputFormat.Jsonl,
            writer,
            _ => throw new InvalidOperationException("jsonl mode must not snapshot the board"),
            showAll: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(
            "{\"revision\":5,\"key\":\"/plan/1/order/op-a/branch\",\"op\":\"put\"}\n"
            + "{\"revision\":6,\"key\":\"/plan/1/order/op-a/state\",\"op\":\"put\"}\n"
            + "{\"revision\":7,\"key\":\"/plan/1/order/op-a/claim\",\"op\":\"delete\"}\n",
            writer.ToString());
    }

    [Fact]
    public async Task RunLoop_Table_RedrawsBoardOnEachEvent()
    {
        WatchSignal[] events =
        [
            new("/plan/1/order/op-a/branch", Deleted: false, 5),
            new("/plan/1/order/op-a/state", Deleted: false, 6),
        ];

        IReadOnlyList<KvItem> snapshot =
        [
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"done\"}"),
        ];

        using var writer = new StringWriter();
        await WatchCommand.RunLoopAsync(
            Replay(events),
            OutputFormat.Table,
            writer,
            _ => Task.FromResult(snapshot),
            showAll: false,
            TestContext.Current.CancellationToken);

        string output = writer.ToString();
        // One redraw (clear + home) per change event.
        Assert.Equal(2, CountOccurrences(output, "\u001b[2J\u001b[H"));
        Assert.Contains("/plan/1/order/op-a", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public async Task RunLoopWithRecovery_Table_OnCompaction_ReRangesAndResumes()
    {
        var watchedFrom = new List<long>();
        int snapshots = 0;
        using var writer = new StringWriter();

        await WatchCommand.RunLoopWithRecoveryAsync(
            fromRevision: 10,
            watch: (from, _) =>
            {
                watchedFrom.Add(from);
                if (watchedFrom.Count == 1)
                {
                    throw new WatchCompactedException("/plan/", from, compactRevision: 99);
                }

                return Replay(
                [
                    new WatchSignal("/plan/1/order/op-b/state", Deleted: false, 101),
                ]);
            },
            output: OutputFormat.Table,
            writer,
            snapshot: _ =>
            {
                snapshots++;
                return Task.FromResult<IReadOnlyList<KvItem>>(
                    snapshots == 1
                        ? [Item("/plan/1/order/op-a/state", "{\"status\":\"done\"}")]
                        : [Item("/plan/1/order/op-b/state", "{\"status\":\"blocked\"}")]);
            },
            currentRevision: _ => Task.FromResult(100L),
            showAll: false,
            TestContext.Current.CancellationToken);

        Assert.Equal([10L, 100L], watchedFrom);
        Assert.Equal(2, snapshots);

        string output = writer.ToString();
        Assert.Contains("/plan/1/order/op-a", output);
        Assert.Contains("/plan/1/order/op-b", output);
        Assert.Contains("blocked", output);
    }

    [Fact]
    public void Redraw_EmptyBoard_ClearsAndPrintsNoOrders()
    {
        using var writer = new StringWriter();
        WatchCommand.Redraw([], writer);

        Assert.StartsWith("\u001b[2J\u001b[H", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("(no orders)", writer.ToString());
    }

    [Fact]
    public void Redraw_Default_HidesLandedOrders()
    {
        IReadOnlyList<KvItem> items =
        [
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"landed\"}"),
            Item("/plan/1/order/op-b/branch", "nightshift/1/op-b"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"done\"}"),
        ];

        using var writer = new StringWriter();
        WatchCommand.Redraw(items, writer, showAll: false);

        string output = writer.ToString();
        Assert.DoesNotContain("/plan/1/order/op-a", output);
        Assert.DoesNotContain("landed", output);
        Assert.Contains("/plan/1/order/op-b", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void Redraw_ShowAll_IncludesLandedOrders()
    {
        IReadOnlyList<KvItem> items =
        [
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"landed\"}"),
            Item("/plan/1/order/op-b/branch", "nightshift/1/op-b"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"done\"}"),
        ];

        using var writer = new StringWriter();
        WatchCommand.Redraw(items, writer, showAll: true);

        string output = writer.ToString();
        Assert.Contains("/plan/1/order/op-a", output);
        Assert.Contains("landed", output);
        Assert.Contains("/plan/1/order/op-b", output);
        Assert.Contains("done", output);
    }

    [Fact]
    public void Redraw_Default_OnlyLandedOrders_RendersEmptyBoard()
    {
        IReadOnlyList<KvItem> items =
        [
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"landed\"}"),
            Item("/plan/1/order/op-b/branch", "nightshift/1/op-b"),
            Item("/plan/1/order/op-b/state", "{\"status\":\"landed\"}"),
        ];

        using var writer = new StringWriter();
        WatchCommand.Redraw(items, writer, showAll: false);

        Assert.Contains("(no orders)", writer.ToString());
    }

    [Theory]
    [InlineData("claimed")]
    [InlineData("done")]
    [InlineData("blocked")]
    [InlineData("escalated")]
    [InlineData("refused")]
    public void Redraw_Default_NonLandedStatusesAlwaysVisible(string status)
    {
        IReadOnlyList<KvItem> items =
        [
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/1/order/op-a/state", $"{{\"status\":\"{status}\"}}"),
        ];

        using var writer = new StringWriter();
        WatchCommand.Redraw(items, writer, showAll: false);

        string output = writer.ToString();
        Assert.Contains("/plan/1/order/op-a", output);
        Assert.Contains(status, output);
    }

    private static async IAsyncEnumerable<WatchSignal> Replay(WatchSignal[] events)
    {
        foreach (WatchSignal signal in events)
        {
            yield return signal;
            await Task.Yield();
        }
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        for (int i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private static KvItem Item(string key, string text)
        => new(key, 1, 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(text));
}
