namespace Octoshift.Tests;

using Octoshift.Coordination;
using Xunit;

/// <summary>
/// The board parse against the <c>nightshift where --output json</c> wire contract (snake_case rows): status
/// lookups drive the idempotent land check and the board-aware fast path, and malformed input degrades to an
/// empty board rather than throwing.
/// </summary>
public class BoardStateTests
{
    private const string Sample = """
        [
          {"order_base":"/plan/2/order/op-a","status":"landed","branch":"nightshift/2/op-a"},
          {"order_base":"/plan/2/order/op-b","status":"done","branch":"nightshift/2/op-b"},
          {"order_base":"/plan/2/order/op-c","status":"open","branch":"nightshift/2/op-c"}
        ]
        """;

    [Fact]
    public void Parse_ReadsStatusAndOutstandingDone()
    {
        BoardState board = BoardState.Parse(Sample);

        Assert.True(board.IsLanded("/plan/2/order/op-a"));
        Assert.False(board.IsLanded("/plan/2/order/op-b"));
        Assert.False(board.IsLanded("/plan/2/order/unknown"));
        Assert.True(board.HasOutstandingDone); // op-b is done
        Assert.Equal(["/plan/2/order/op-b"], board.GetDoneOrderBases());
    }

    [Fact]
    public void Parse_NoDone_ReportsNoOutstanding()
    {
        BoardState board = BoardState.Parse("""
            [{"order_base":"/plan/2/order/op-a","status":"landed","branch":"nightshift/2/op-a"}]
            """);

        Assert.False(board.HasOutstandingDone);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json")]
    [InlineData("{}")]
    public void Parse_MalformedInput_IsEmptyBoard(string json)
    {
        BoardState board = BoardState.Parse(json);

        Assert.False(board.HasOutstandingDone);
        Assert.False(board.IsLanded("/plan/2/order/op-a"));
    }
}
