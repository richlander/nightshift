namespace Octoshift.Tests;

using Octoshift.Coordination;
using Xunit;

/// <summary>The locally re-implemented order↔branch parse: valid nightshift branches map to a base, malformed ones reject.</summary>
public class OrderRefTests
{
    [Fact]
    public void FromBranch_MapsToBase()
    {
        OrderRef? order = OrderRef.FromBranch("nightshift/9001/op1");

        Assert.NotNull(order);
        Assert.Equal("/plan/9001/order/op1", order.Value.Base);
        Assert.Equal("nightshift/9001/op1", order.Value.Branch);
    }

    [Fact]
    public void FromBase_MapsToBranch()
    {
        OrderRef? order = OrderRef.FromBase("/plan/9001/order/op1");

        Assert.NotNull(order);
        Assert.Equal("nightshift/9001/op1", order.Value.Branch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    [InlineData("nightshift/9001")]        // missing order segment
    [InlineData("nightshift/9001/op1/x")]  // too many segments
    [InlineData("nightshift//op1")]        // empty plan
    [InlineData("feature/nightshift/9001/op1")] // wrong prefix
    public void FromBranch_RejectsMalformed(string? branch)
        => Assert.Null(OrderRef.FromBranch(branch));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/plan/9001")] // missing order segment
    [InlineData("/plan/9001/order/")] // missing order value
    [InlineData("/plan/9001/order/op1/extra")] // too many segments
    [InlineData("plan/9001/order/op1")] // missing leading slash
    public void FromBase_RejectsMalformed(string? orderBase)
        => Assert.Null(OrderRef.FromBase(orderBase));
}
