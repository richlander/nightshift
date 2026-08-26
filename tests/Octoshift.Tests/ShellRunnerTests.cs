namespace Octoshift.Tests;

using System.Diagnostics;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// How a collection is started, which is a security boundary rather than a detail. The script carries the
/// run's framing nonce, and <c>ps</c> shows every argument of every process to every other process of the
/// same user — which is exactly who the agents being swept are. So the script travels on stdin, and the
/// program that reads it is named outright rather than left to whatever shell the account happens to use.
/// </summary>
public class ShellRunnerTests
{
    [Fact]
    public void InvocationFor_ThisMachineRunsAnExplicitShellThatReadsTheScript()
    {
        Invocation invocation = ShellRunner.InvocationFor(null);

        Assert.Equal("/bin/sh", invocation.File);
        Assert.Equal(["-s"], invocation.Arguments);
    }

    [Fact]
    public void InvocationFor_ARemoteHostExecsBinShRatherThanTheAccountShell()
    {
        // The remote command is two words the login shell only has to exec, so a csh or tcsh account never
        // parses the POSIX script — the sh on the other side of that exec does, from its stdin.
        Invocation invocation = ShellRunner.InvocationFor("fernie");

        Assert.Equal("ssh", invocation.File);
        Assert.Equal(["-T", "-o", "BatchMode=yes", "-o", "ConnectTimeout=10", "--", "fernie", "/bin/sh", "-s"], invocation.Arguments);
    }

    [Theory]
    [InlineData("fernie")]
    [InlineData("build-1")]
    [InlineData("rich@web-2.example.com")]
    public void InvocationFor_PassesAnSshConfigAliasThroughUntouched(string host)
    {
        // ProxyJump, control sockets and per-host fallbacks live in ~/.ssh/config, so the alias has to
        // reach ssh exactly as the user wrote it — and after `--`, where it cannot be read as an option.
        IReadOnlyList<string> arguments = ShellRunner.InvocationFor(host).Arguments;

        Assert.Equal(host, arguments[arguments.ToList().IndexOf("--") + 1]);
    }

    [Fact]
    public void InvocationFor_RefusesToBuildArgumentsFromAnOptionShapedHost()
        => Assert.Throws<ArgumentException>(() => ShellRunner.InvocationFor("-V"));

    [Fact]
    public async Task RunAsync_SendsTheScriptOnStdinAndLeavesItOutOfArgv()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The collection path runs /bin/sh; there is nothing to start on Windows.");

        // The script asks the operating system what another process of the same user would see of it, so
        // this is the actual exposure rather than a restatement of how the arguments were built.
        const string nonce = "7c1f0a2b5e4d6a98";
        string script = $"printf 'argv=%s\\n' \"$(ps -o args= -p $$ | tr -d '\\n')\"\nprintf 'argc=%s\\n' \"$#\"\nprintf 'nonce={nonce}\\n'\n";

        CommandResult result = await ShellRunner.For(null)(script, TestContext.Current.CancellationToken);

        // It ran, and stdin is the only route it could have arrived by.
        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"nonce={nonce}", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("argc=0", result.Stdout, StringComparison.Ordinal);

        string argv = result.Stdout.Split('\n').Single(line => line.StartsWith("argv=", StringComparison.Ordinal));
        Assert.Contains("sh", argv, StringComparison.Ordinal);
        Assert.DoesNotContain(nonce, argv, StringComparison.Ordinal);
        Assert.DoesNotContain("printf", argv, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DrainsBothStreamsWhileTheScriptIsStillBeingWritten()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The collection path runs /bin/sh; there is nothing to start on Windows.");

        // A sweep of a busy host is bigger than a pipe buffer in both directions: the script does not fit
        // in one write, and the captures do not fit in one read. A child whose stdout is full stops
        // reading its stdin, so writing the script before draining the output hangs both sides forever.
        string script = "i=0\nwhile [ $i -lt 4000 ]; do printf 'out %s\\n' \"$i\"; printf 'err %s\\n' \"$i\" >&2; i=$((i+1)); done\n"
            + string.Join('\n', Enumerable.Repeat("# padding, well past any pipe buffer, never executed early", 6000));

        CommandResult result = await ShellRunner.RunAsync("/bin/sh", ["-s"], script, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("out 3999", result.Stdout, StringComparison.Ordinal);
        Assert.Contains("err 3999", result.Stderr, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_AChildThatStopsReadingIsAnAnswerNotADeadlock()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The collection path runs /bin/sh; there is nothing to start on Windows.");

        // `exit 3` is what the script itself does when tmux cannot be listed, and it happens with most of
        // the script still unwritten. The exit code is the answer; the broken pipe is only how it arrives.
        string script = "exit 3\n" + string.Join('\n', Enumerable.Repeat("# never reached", 20000));

        CommandResult result = await ShellRunner.RunAsync("/bin/sh", ["-s"], script, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_CancellationEndsTheProcessRatherThanJustTheWait()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The collection path runs /bin/sh; there is nothing to start on Windows.");

        // An abandoned ssh holds a connection open, so cancellation has to take the tree down. If it only
        // stopped waiting, this would sit here for the full sleep.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        var elapsed = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ShellRunner.RunAsync("/bin/sh", ["-s"], "sleep 60\n", cts.Token));

        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(20), $"cancellation took {elapsed.Elapsed}");
    }

    [Fact]
    public async Task RunAsync_AProgramThatIsNotThereIsReportedRatherThanThrown()
    {
        CommandResult result = await ShellRunner.RunAsync(
            "octoshift-no-such-program", [], "printf 'never\\n'", TestContext.Current.CancellationToken);

        Assert.Equal(127, result.ExitCode);
        Assert.NotEmpty(result.Stderr);
    }
}
