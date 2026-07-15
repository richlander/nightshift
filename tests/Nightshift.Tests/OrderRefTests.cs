namespace Nightshift.Tests;

using Nightshift.Commands;
using Xunit;

/// <summary>
/// The order↔branch bijection that makes the branch a durable recovery key: every derived key agrees, and
/// the base/branch parsers round-trip and reject anything malformed.
/// </summary>
public class OrderRefTests
{
    [Fact]
    public void DerivedKeys_AreConsistent()
    {
        var order = new OrderRef("9001", "op1");

        Assert.Equal("/plan/9001/order/op1", order.Base);
        Assert.Equal("nightshift/9001/op1", order.Branch);
        Assert.Equal("/ready/9001/op1", order.ReadyKey);
        Assert.Equal("/plan/9001/order/op1/claim", order.ClaimKey);
    }

    [Fact]
    public void FromBase_RoundTripsThroughBranch()
    {
        OrderRef? fromBase = OrderRef.FromBase("/plan/9001/order/op1");
        Assert.NotNull(fromBase);

        OrderRef? fromBranch = OrderRef.FromBranch(fromBase.Value.Branch);
        Assert.Equal(fromBase, fromBranch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/plan/9001/order")] // missing order segment
    [InlineData("/plan/9001/order/op1/claim")] // a sub-key, not the base
    [InlineData("/plan//order/op1")] // empty plan
    [InlineData("plan/9001/order/op1")] // missing leading slash
    [InlineData("/ready/9001/op1")] // wrong shape
    public void FromBase_RejectsMalformed(string? input)
        => Assert.Null(OrderRef.FromBase(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("nightshift/9001")] // missing order segment
    [InlineData("nightshift/9001/op1/extra")] // too many segments
    [InlineData("nightshift//op1")] // empty plan
    [InlineData("feature/nightshift/9001/op1")] // wrong prefix
    public void FromBranch_RejectsMalformed(string? input)
        => Assert.Null(OrderRef.FromBranch(input));
}
