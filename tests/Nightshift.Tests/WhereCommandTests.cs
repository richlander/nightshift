namespace Nightshift.Tests;

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
    public void BuildTable_ShapesSortedOrderRows()
    {
        OutputTable table = WhereCommand.BuildTable(
        [
            Item("/plan/2/order/op-b/branch", "nightshift/2/op-b"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"done\"}"),
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
            Item("/plan/3/order/op-c/state", "{\"status\":\"blocked\"}"),
        ]);

        Assert.Equal(["Order Base", "Status", "Branch"], table.Columns.Select(column => column.Header));
        Assert.Equal(["order_base", "status", "branch"], table.Columns.Select(column => column.Field));
        Assert.Collection(
            table.Rows,
            row => Assert.Equal(["/plan/1/order/op-a", "done", "nightshift/1/op-a"], row),
            row => Assert.Equal(["/plan/2/order/op-b", "open", "nightshift/2/op-b"], row),
            row => Assert.Equal(["/plan/3/order/op-c", "blocked", ""], row));
    }

    [Fact]
    public void TsvOutput_MatchesLegacyWhereRows()
    {
        OutputTable table = WhereCommand.BuildTable(
        [
            Item("/plan/2/order/op-b/branch", "nightshift/2/op-b"),
            Item("/plan/1/order/op-a/state", "{\"status\":\"done\"}"),
            Item("/plan/1/order/op-a/branch", "nightshift/1/op-a"),
        ]);

        string expected = string.Join(
            Environment.NewLine,
            "/plan/1/order/op-a\tdone\tnightshift/1/op-a",
            "/plan/2/order/op-b\topen\tnightshift/2/op-b",
            string.Empty);
        Assert.Equal(expected, OutputFormatter.RenderTable(OutputFormat.Tsv, table));
    }

    private static KvItem Item(string key, string text)
        => new(key, CreateRevision: 1, ModRevision: 1, Lease: null, Immutable: false, Value: System.Text.Encoding.UTF8.GetBytes(text));
}
