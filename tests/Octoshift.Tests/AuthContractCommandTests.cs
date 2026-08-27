namespace Octoshift.Tests;

using Octoshift;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The command-level auth contract. With GitHub App credentials configured but unusable — the exact repro
/// <c>OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH=/missing octoshift waiting|pr …</c> — the commands must reach
/// their normal unavailable contract (a human PARTIAL token / one JSON error document /
/// <see cref="ExitCode.Unavailable"/>) rather than crashing with a stack trace and no output before their
/// protected execution begins. Joining the ConsoleCapture collection serializes the console redirect and
/// the process-wide environment mutation with the other command tests.
/// </summary>
[Collection("ConsoleCapture")]
public class AuthContractCommandTests
{
    private const string CredentialsPathVariable = "OCTOSHIFT_GITHUB_APP_CREDENTIALS_PATH";

    /// <summary>
    /// Runs <paramref name="run"/> with a configured-but-missing App credentials path — the case that made
    /// <c>GhRunnerFactory.Create</c> throw before the command could catch anything — capturing the console
    /// and restoring both the environment and the streams afterwards.
    /// </summary>
    private static async Task<(int Exit, string Out, string Err)> RunWithBrokenAppCredentialsAsync(
        Func<CancellationToken, Task<int>> run, CancellationToken ct)
    {
        string missingCredentialsPath = Path.Combine(Path.GetTempPath(), $"octoshift-missing-creds-{Guid.NewGuid():N}.json");
        string? savedEnv = Environment.GetEnvironmentVariable(CredentialsPathVariable);
        TextWriter savedOut = Console.Out;
        TextWriter savedErr = Console.Error;
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable(CredentialsPathVariable, missingCredentialsPath);
            Console.SetOut(outWriter);
            Console.SetError(errWriter);
            int exit = await run(ct);
            return (exit, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(savedOut);
            Console.SetError(savedErr);
            Environment.SetEnvironmentVariable(CredentialsPathVariable, savedEnv);
        }
    }

    [Fact]
    public async Task Waiting_BrokenAppCredentials_LeadsWithPartialToken_NotACrash()
    {
        // A fresh history defaults the fleet to the local machine, whose injected scan fails, so the one
        // target is unreachable and the human path leads with PARTIAL — reached only because the broken
        // credentials no longer crash GhRunnerFactory.Create ahead of the command's try.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authwait-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithBrokenAppCredentialsAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: false, token, historyPath: path,
                    scanAsync: (_, _) => throw new TmuxUnavailableException("local: tmux is not running")),
                ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL", stdout, StringComparison.Ordinal);
            Assert.Contains("tmux is not running", stderr, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Waiting_BrokenAppCredentials_JsonStaysOneErrorDocument_NotACrash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authwaitjson-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, _) = await RunWithBrokenAppCredentialsAsync(
                token => WaitingCommand.RunAsync(
                    "owner/name", [], all: false, json: true, token, historyPath: path,
                    scanAsync: (_, _) => throw new TmuxUnavailableException("local: tmux is not running")),
                ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.DoesNotContain("PARTIAL", stdout, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Pr_BrokenAppCredentials_LeadsWithPartialToken_NotACrash()
    {
        // A malformed history fails the strict load inside the command's protected execution — deterministic
        // without ssh or GitHub. It exercises that the configured-but-broken credentials no longer crash
        // GhRunnerFactory.Create ahead of that protected execution.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authpr-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, string stdout, string stderr) = await RunWithBrokenAppCredentialsAsync(
                token => PrCommand.RunAsync(4448, "owner/name", [], json: false, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
            Assert.StartsWith("PARTIAL PR #4448", stdout, StringComparison.Ordinal);
            Assert.NotEqual(string.Empty, stderr.Trim());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Pr_BrokenAppCredentials_JsonReturnsUnavailable_NotACrash()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-authprjson-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not a history ]");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            (int exit, _, _) = await RunWithBrokenAppCredentialsAsync(
                token => PrCommand.RunAsync(4448, "owner/name", [], json: true, token, historyPath: path), ct);

            Assert.Equal(ExitCode.Unavailable, exit);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }
}
