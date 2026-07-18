namespace Octoshift.Tests;

using Octoshift.Coordination;
using Xunit;

/// <summary>Parsing and branch matching for plan/order observation scopes.</summary>
public class ObservationScopeTests
{
    [Theory]
    [InlineData("/plan/3")]
    [InlineData("plan/3")]
    [InlineData("3")]
    public void Parse_PlanForms_Normalize(string scopeText)
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse(scopeText));

        Assert.False(scope.IsOrder);
        Assert.Equal("/plan/3", scope.Base);
        Assert.Equal("nightshift/3/", scope.BranchSearch);
        Assert.True(scope.MatchesBranch("nightshift/3/op-a"));
        Assert.False(scope.MatchesBranch("nightshift/30/op-a"));
    }

    [Theory]
    [InlineData("/plan/3/order/op-a")]
    [InlineData("plan/3/order/op-a")]
    [InlineData("3/op-a")]
    [InlineData("nightshift/3/op-a")]
    public void Parse_OrderForms_Normalize(string scopeText)
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse(scopeText));

        Assert.True(scope.IsOrder);
        Assert.Equal("/plan/3/order/op-a", scope.Base);
        Assert.Equal("nightshift/3/op-a", scope.BranchSearch);
        Assert.True(scope.MatchesBranch("nightshift/3/op-a"));
        Assert.False(scope.MatchesBranch("nightshift/3/op-ab"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("plan")]
    [InlineData("/plan/3/order")]
    [InlineData("/plan/3/order/op-a/extra")]
    [InlineData("nightshift/3")]
    public void Parse_RejectsMalformed(string scopeText)
        => Assert.Null(ObservationScope.Parse(scopeText));
}
