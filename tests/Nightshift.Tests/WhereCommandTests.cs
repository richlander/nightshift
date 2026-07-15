namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Output;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// The pure key/status derivation behind <c>nightshift where</c>: order bases are recovered from their
/// <c>/state</c> and <c>/branch</c> sub-keys, and the status is pulled from the order's state JSON.
/// </summary>
public class WhereCommandTests
{
    [Theory]
    [InlineData("/plan/1/order/op1/state", "/state", "/plan/1/order/op1")]
    [InlineData("/plan/1/order/op1/branch", "/branch", "/plan/1/order/op1")]
    public void BaseOf_StripsSuffix(string key, string suffix, string expected)
        => Assert.Equal(expected, WhereCommand.BaseOf(key, suffix));

    [Theory]
    [InlineData("/plan/1/order/op1/spec", "/state")]      // different sub-key
    [InlineData("/plan/1/order/op1", "/state")]           // no suffix
    [InlineData("/plan/1/order/op1/state", "/branch")]    // suffix mismatch
    public void BaseOf_ReturnsNull_WhenSuffixAbsent(string key, string suffix)
        => Assert.Null(WhereCommand.BaseOf(key, suffix));

    [Fact]
    public void ParseStatus_ReadsStatusField()
        => Assert.Equal("done", WhereCommand.ParseStatus("{\"status\":\"done\",\"by\":\"a\",\"at\":\"t\"}"));

    [Theory]
    [InlineData("")]                    // empty
    [InlineData("not json")]            // unparseable
    [InlineData("{\"by\":\"a\"}")]      // no status field
    [InlineData("[1,2,3]")]             // not an object
    [InlineData("\"done\"")]            // bare string, not an object
    public void ParseStatus_ReturnsSentinel_WhenAbsentOrMalformed(string json)
        => Assert.Equal("?", WhereCommand.ParseStatus(json));

    [Fact]
    public void BuildRows_ShapesBoardKeysIntoSortedRows()
    {
        KvItem[] items =
        [
            Item("/plan/2/order/op-b/branch", "nightshift/2/op-b\n"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"done\",\"by\":\"agent\"}"),
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/3/order/op-c/state", "{\"status\":\"blocked\"}"),
        ];

        List<WhereRow> rows = WhereCommand.BuildRows(items);

        Assert.Collection(
            rows,
            row =>
            {
                Assert.Equal("/plan/1/order/op-a", row.OrderBase);
                Assert.Equal("done", row.Status);
                Assert.Equal("nightshift/1/op-a", row.Branch);
            },
            row =>
            {
                Assert.Equal("/plan/2/order/op-b", row.OrderBase);
                Assert.Equal("open", row.Status);
                Assert.Equal("nightshift/2/op-b", row.Branch);
            },
            row =>
            {
                Assert.Equal("/plan/3/order/op-c", row.OrderBase);
                Assert.Equal("blocked", row.Status);
                Assert.Equal(string.Empty, row.Branch);
            });
    }

    [Fact]
    public void RenderRows_TsvMatchesLegacyBytes()
    {
        List<WhereRow> rows =
        [
            new("/plan/1/order/op-a", "done", "nightshift/1/op-a"),
            new("/plan/2/order/op-b", "open", "nightshift/2/op-b"),
            new("/plan/3/order/op-c", "blocked", string.Empty),
        ];

        using var writer = new StringWriter();
        WhereCommand.RenderRows(rows, OutputFormat.Tsv, writer);

        string expected =
            "/plan/1/order/op-a\tdone\tnightshift/1/op-a\n"
            + "/plan/2/order/op-b\topen\tnightshift/2/op-b\n"
            + "/plan/3/order/op-c\tblocked\t\n";
        Assert.Equal(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(writer.ToString()));
    }

    [Fact]
    public void RenderRows_JsonFormatsUseSnakeCaseRows()
    {
        List<WhereRow> rows =
        [
            new("/plan/1/order/op-a", "done", "nightshift/1/op-a"),
        ];

        using var json = new StringWriter();
        WhereCommand.RenderRows(rows, OutputFormat.Json, json);

        using var jsonl = new StringWriter();
        WhereCommand.RenderRows(rows, OutputFormat.Jsonl, jsonl);

        Assert.Equal(
            "[{\"order_base\":\"/plan/1/order/op-a\",\"status\":\"done\",\"branch\":\"nightshift/1/op-a\"}]\n",
            json.ToString());
        Assert.Equal(
            "{\"order_base\":\"/plan/1/order/op-a\",\"status\":\"done\",\"branch\":\"nightshift/1/op-a\"}\n",
            jsonl.ToString());
    }

    [Fact]
    public void RenderEmpty_JsonEmitsEmptyArray()
    {
        using var writer = new StringWriter();
        WhereCommand.RenderEmpty(OutputFormat.Json, writer);

        Assert.Equal($"[]{Environment.NewLine}", writer.ToString());
    }

    [Fact]
    public void RenderEmpty_JsonlEmitsNothing()
    {
        using var writer = new StringWriter();
        WhereCommand.RenderEmpty(OutputFormat.Jsonl, writer);

        Assert.Equal(string.Empty, writer.ToString());
    }

    private static KvItem Item(string key, string text)
        => new(key, 1, 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(text));
}
