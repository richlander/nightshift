namespace Nightshift.Tests;

using Nightshift.Commands;
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
}
