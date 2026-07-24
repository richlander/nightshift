namespace Nightshift.Tests;

using Nightshift;
using Nightshift.Commands;
using Xunit;

/// <summary>
/// <c>nightshift skill [role]</c>: it serves the packaged skills from the binary with no daemon, strips YAML
/// frontmatter so the body prints clean, and rejects an unknown role with the usage exit code.
/// </summary>
public sealed class SkillCommandTests
{
    [Fact]
    public async Task Skill_NoArgument_PrintsGeneralOrientation()
    {
        CaptureResult result = await InvokeAsync("skill");

        Assert.Equal(ExitCode.Ok, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.StartsWith("# Nightshift", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("nightshift skill <role>", result.Stdout, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("planner", "# Nightshift coordinator")]
    [InlineData("worker", "# Nightshift worker")]
    [InlineData("coordinator", "# Nightshift coordinator")]
    [InlineData("builder", "# Nightshift builder")]
    [InlineData("reviewer", "# Nightshift reviewer")]
    public async Task Skill_WithRole_PrintsThatRoleSkill(string role, string heading)
    {
        CaptureResult result = await InvokeAsync("skill", role);

        Assert.Equal(ExitCode.Ok, result.ExitCode);
        Assert.Empty(result.Stderr);
        Assert.StartsWith(heading, result.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Skill_EveryRole_PrintsWithFrontmatterStripped()
    {
        foreach (string role in SkillCommand.Roles)
        {
            CaptureResult result = await InvokeAsync("skill", role);

            Assert.Equal(ExitCode.Ok, result.ExitCode);
            Assert.False(result.Stdout.StartsWith("---", StringComparison.Ordinal), $"'{role}' skill leaked its frontmatter delimiter.");
            Assert.DoesNotContain("\nname:", result.Stdout, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Skill_UnknownRole_ReturnsUsageAndListsRoles()
    {
        CaptureResult result = await InvokeAsync("skill", "bogus");

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Contains("unknown role 'bogus'", result.Stderr, StringComparison.Ordinal);
        Assert.Contains("planner, coordinator, worker, builder, reviewer", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("skill", "worker")]
    public void Skill_Parses_WithoutError(params string[] args)
    {
        var result = Cli.CreateRootCommand().Parse(args);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void StripFrontmatter_RemovesLeadingBlock_KeepsBodyRules()
    {
        const string content = "---\nname: demo\n---\n\n# Title\n\nbody\n\n---\n\nmore\n";

        string stripped = SkillCommand.StripFrontmatter(content);

        Assert.Equal("# Title\n\nbody\n\n---\n\nmore\n", stripped);
    }

    [Fact]
    public void StripFrontmatter_WithoutFrontmatter_ReturnsUnchanged()
    {
        const string content = "# Title\n\nbody\n";

        Assert.Equal(content, SkillCommand.StripFrontmatter(content));
    }

    [Fact]
    public void StripFrontmatter_WithoutClosingDelimiter_ReturnsUnchanged()
    {
        const string content = "---\nname: demo\nno closing delimiter\n";

        Assert.Equal(content, SkillCommand.StripFrontmatter(content));
    }

    private static async Task<CaptureResult> InvokeAsync(params string[] args)
    {
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            int exitCode = await Cli.RunAsync(args);
            return new CaptureResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private sealed record CaptureResult(int ExitCode, string Stdout, string Stderr);
}
