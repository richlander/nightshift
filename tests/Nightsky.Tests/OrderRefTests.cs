namespace Nightsky.Tests;

using Nightsky.Commands;
using Xunit;

public class OrderRefTests
{
    [Fact]
    public void FromBase_ParsesPlanAndOrder()
    {
        OrderRef? parsed = OrderRef.FromBase("/plan/42/order/op7");

        Assert.Equal(new OrderRef("42", "op7"), parsed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/plan/42/order")]
    [InlineData("/plan/42/order/op7/state")]
    [InlineData("/plan//order/op7")]
    [InlineData("/ready/42/op7")]
    public void FromBase_RejectsInvalidShapes(string? value)
    {
        Assert.Null(OrderRef.FromBase(value));
    }
}
