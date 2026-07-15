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
}
