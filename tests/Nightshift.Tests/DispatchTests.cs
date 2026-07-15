namespace Nightshift.Tests;

using Nightshift;
using Xunit;

/// <summary>Locks the CLI dispatch contract while System.CommandLine owns parsing.</summary>
public sealed class DispatchTests : IClassFixture<TurnstileFixture>
{
    private static readonly SemaphoreSlim ConsoleLock = new(1, 1);
    private readonly TurnstileFixture _fixture;

    public DispatchTests(TurnstileFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task BareInvocation_PrintsUsageAndReturnsUsage()
    {
        InvocationResult result = await InvokeAsync();

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith("usage: nightshift", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownVerb_PrintsUsageAndReturnsUsage()
    {
        InvocationResult result = await InvokeAsync("bogus");

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith("usage: nightshift", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--help")]
    [InlineData("-h")]
    public async Task TopLevelHelpOrVersion_PrintsUsageAndReturnsUsage(string arg)
    {
        InvocationResult result = await InvokeAsync(arg);

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.StartsWith("usage: nightshift", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoArgVerbWithTrailingToken_ReturnsUsage()
    {
        InvocationResult result = await InvokeAsync("join", "extra");

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.NotEmpty(result.Stderr);
    }

    [Theory]
    [InlineData()]
    [InlineData("--status", "bogus")]
    public async Task ReleaseWithoutValidStatus_ReturnsUsage(params string[] args)
    {
        InvocationResult result = await InvokeAsync(["release", .. args]);

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.Equal(
            $"nightshift release: --status must be one of done|blocked|declined|escalated|refused{Environment.NewLine}",
            result.Stderr);
    }

    [Fact]
    public async Task BadTypedOption_ReturnsUsage()
    {
        InvocationResult result = await InvokeAsync("next", "--timeout", "bogus");

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.NotEmpty(result.Stderr);
    }

    [Theory]
    [InlineData("xml")]
    [InlineData("bogus")]
    [InlineData("999")]
    public async Task InvalidWhereOutput_ReturnsUsage(string output)
    {
        InvocationResult result = await InvokeAsync("where", "--output", output);

        Assert.Equal(ExitCode.Usage, result.ExitCode);
        Assert.Empty(result.Stdout);
        Assert.NotEmpty(result.Stderr);
        Assert.DoesNotContain("Unhandled exception", result.Stderr, StringComparison.Ordinal);
        Assert.DoesNotContain("System.InvalidOperationException", result.Stderr, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plaintext")]
    [InlineData("TABLE")]
    [InlineData("Markdown")]
    [InlineData("json")]
    [InlineData("JSONL")]
    [InlineData("tsv")]
    public async Task ValidWhereOutput_ReturnsOk(string output)
    {
        InvocationResult result = await InvokeAsync(_fixture.Socket, "where", "--output", output);

        Assert.Equal(ExitCode.Ok, result.ExitCode);
        Assert.Empty(result.Stderr);
    }

    [Fact]
    public async Task DrainResume_PrintsResumedToken()
    {
        InvocationResult result = await InvokeAsync(_fixture.Socket, "drain", "--resume");

        Assert.Equal(ExitCode.Ok, result.ExitCode);
        Assert.Equal($"RESUMED{Environment.NewLine}", result.Stdout);
        Assert.Empty(result.Stderr);
    }

    private static Task<InvocationResult> InvokeAsync(params string[] args)
        => InvokeAsync(socket: null, args);

    private static async Task<InvocationResult> InvokeAsync(string? socket, params string[] args)
    {
        await ConsoleLock.WaitAsync(TestContext.Current.CancellationToken);
        TextWriter originalOut = Console.Out;
        TextWriter originalError = Console.Error;
        string? originalSocket = Environment.GetEnvironmentVariable("TURNSTILE_SOCKET");
        string? originalRuntime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        string runtimeDir = Path.Combine(AppContext.BaseDirectory, "dispatch-runtime", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runtimeDir);

        await using var stdout = new StringWriter();
        await using var stderr = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("TURNSTILE_SOCKET", socket);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", runtimeDir);
            Console.SetOut(stdout);
            Console.SetError(stderr);

            int exitCode = await Cli.RunAsync(args);
            return new InvocationResult(exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable("TURNSTILE_SOCKET", originalSocket);
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", originalRuntime);
            try
            {
                Directory.Delete(runtimeDir, recursive: true);
            }
            catch (Exception)
            {
                // Best-effort cleanup for files that may be held during a failing test.
            }

            ConsoleLock.Release();
        }
    }

    private sealed record InvocationResult(int ExitCode, string Stdout, string Stderr);
}
