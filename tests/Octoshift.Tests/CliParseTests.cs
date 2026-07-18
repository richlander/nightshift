namespace Octoshift.Tests;

using Octoshift;
using Xunit;

/// <summary>CLI parse coverage for the observation verbs and legacy-usage guard.</summary>
public class CliParseTests
{
    [Theory]
    [InlineData("wait", "/plan/3")]
    [InlineData("watch", "3/op-a")]
    public void CreateRootCommand_ParsesObservationVerbs(string verb, string scope)
    {
        var result = Cli.CreateRootCommand().Parse([verb, scope, "--repo", "owner/repo"]);

        Assert.Empty(result.Errors);
        Assert.Equal(verb, result.CommandResult.Command.Name);
    }

    [Fact]
    public void CreateRootCommand_WaitRequiresScopeArgument()
    {
        var result = Cli.CreateRootCommand().Parse(["wait"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public async Task RunAsync_UnknownVerbReturnsUsage()
    {
        int exit = await Cli.RunAsync(["unknown"]);

        Assert.Equal(ExitCode.Usage, exit);
    }
}
