namespace Octoshift.Tests;

using Octoshift;
using Octoshift.Waiting;
using Xunit;

/// <summary>CLI parse coverage for the membrane verbs and legacy-usage guard.</summary>
public class CliParseTests
{
    [Fact]
    public async Task RunAsync_UnknownVerbReturnsUsage()
    {
        int exit = await Cli.RunAsync(["unknown"]);

        Assert.Equal(ExitCode.Usage, exit);
    }

    [Theory]
    [InlineData("--host=-V")]          // ssh prints its version, exits 0, and the sweep reads as quiet
    [InlineData("--host=-oProxyCommand=id")]
    [InlineData("--host=")]
    [InlineData("--host= ")]
    [InlineData("--host=two words")]
    public void CreateRootCommand_WaitingRejectsAHostSshWouldNotReadAsAHost(string argument)
    {
        var result = Cli.CreateRootCommand().Parse(["waiting", argument, "--repo", "owner/repo"]);

        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("--host", StringComparison.Ordinal));
    }

    [Fact]
    public void CreateRootCommand_BareHostCannotSwallowTheNextFlag()
    {
        // `--host --json` parsed `--json` as the hostname, so the flag vanished and ssh was handed an
        // option. Either reading is wrong; the value is not a hostname.
        var result = Cli.CreateRootCommand().Parse(["waiting", "--host", "--json", "--repo", "owner/repo"]);

        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void CreateRootCommand_BareHostAtTheEndIsStillAUsageError()
    {
        var result = Cli.CreateRootCommand().Parse(["waiting", "--repo", "owner/repo", "--host"]);

        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("fernie")]
    [InlineData("build-1")]
    [InlineData("rich@web-2.example.com")]
    public void CreateRootCommand_WaitingAcceptsOrdinarySshAliases(string host)
    {
        var result = Cli.CreateRootCommand().Parse(["waiting", "--host", host, "--repo", "owner/repo"]);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task RunAsync_AnOptionShapedHostIsAUsageErrorRatherThanASweep()
    {
        // Exit 2 and nothing collected: the value never reaches ssh, so there is no quiet exit-0 JSON.
        Assert.Equal(ExitCode.Usage, await Cli.RunAsync(["waiting", "--host=-V", "--json", "--repo", "owner/repo"]));
    }

    [Fact]
    public void Distinct_DropsRepeatsAndKeepsFirstSeenOrder()
    {
        Assert.Equal(["fernie", "banff", "revelstoke"], HostTarget.Distinct(["fernie", "banff", "fernie", "revelstoke", "banff"]));
    }

    [Fact]
    public void For_RefusesToBuildSshArgumentsFromAnOptionShapedHost()
        => Assert.Throws<ArgumentException>(() => ShellRunner.For("-V"));

    [Fact]
    public void CreateRootCommand_ParsesTheFleetVerb()
    {
        var result = Cli.CreateRootCommand().Parse(["fleet"]);

        Assert.Empty(result.Errors);
        Assert.Equal("fleet", result.CommandResult.Command.Name);
    }

    [Theory]
    [InlineData("list")]
    [InlineData("retire")]
    public void CreateRootCommand_ParsesTheFleetSubcommands(string sub)
    {
        var result = Cli.CreateRootCommand().Parse(["fleet", sub, .. sub == "retire" ? new[] { "--host", "fernie" } : []]);

        Assert.Empty(result.Errors);
        Assert.Equal(sub, result.CommandResult.Command.Name);
    }

    [Fact]
    public void CreateRootCommand_FleetRetireRejectsAnOptionShapedHost()
    {
        var result = Cli.CreateRootCommand().Parse(["fleet", "retire", "--host=-V"]);

        Assert.NotEmpty(result.Errors);
        Assert.Contains(result.Errors, e => e.Message.Contains("--host", StringComparison.Ordinal));
    }
}
