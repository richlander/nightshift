namespace Turnstile.Tests;

using Xunit;

public class ScaffoldTests
{
    [Fact]
    public async Task Cli_WithNoArgs_ReturnsUsageExitCode()
    {
        int rc = await Cli.RunAsync([]);
        Assert.Equal(2, rc);
    }
}
