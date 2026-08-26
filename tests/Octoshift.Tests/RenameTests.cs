namespace Octoshift.Tests;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// What the rename path actually does when run. These execute the generated script through
/// <c>/bin/sh</c> — the same shell production hands it to — against a fake <c>tmux</c>, because the two
/// properties that matter cannot be asserted structurally: that no byte of an agent-set window name can
/// execute a command, and that a rename is reported only when tmux confirmed it under the epoch the sweep
/// saw.
/// </summary>
public sealed class RenameTests : IDisposable
{
    private readonly string _dir;
    private readonly string _calls;

    public RenameTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"octoshift-rename-{Guid.NewGuid():N}");
        _calls = Path.Combine(_dir, "calls");
        Directory.CreateDirectory(_calls);

        string tmux = Path.Combine(_dir, "tmux");
        File.WriteAllText(tmux, FakeTmux);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                tmux,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    // A tmux stand-in: it answers the epoch probes from env vars, records each rename to its own files so
    // an arbitrary name survives verbatim, and fails a nominated target so a partial batch can be driven.
    private const string FakeTmux = """
        #!/bin/sh
        sub=$1
        shift
        case "$sub" in
          display-message) printf '%s:%s\n' "$FAKE_PID" "$FAKE_CREATED" ;;
          rename-window)
            # The script always emits: rename-window -t TARGET NAME
            target=$2
            name=$3
            if [ -n "$FAIL_TARGET" ] && [ "$target" = "$FAIL_TARGET" ]; then
              exit 1
            fi
            c=$(cat "$CALLS/count" 2>/dev/null || echo 0)
            c=$((c + 1))
            printf '%s' "$c" > "$CALLS/count"
            printf '%s' "$target" > "$CALLS/target.$c"
            printf '%s' "$name" > "$CALLS/name.$c"
            ;;
        esac
        exit 0
        """;

    private const string EpochPid = "4242";
    private const string EpochCreated = "1755900000";
    private const string ScannedEpoch = EpochPid + ":" + EpochCreated;

    private Func<string?, Func<string, CancellationToken, Task<CommandResult>>> ShellFor(string pid = EpochPid, string? failTarget = null)
        => _ => (script, ct) => RunShellAsync(script, pid, failTarget, ct);

    private async Task<CommandResult> RunShellAsync(string script, string pid, string? failTarget, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("/bin/sh", "-s")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = _dir,
        };
        psi.Environment["PATH"] = _dir + ":" + Environment.GetEnvironmentVariable("PATH");
        psi.Environment["FAKE_PID"] = pid;
        psi.Environment["FAKE_CREATED"] = EpochCreated;
        psi.Environment["CALLS"] = _calls;
        if (failTarget is not null)
        {
            psi.Environment["FAIL_TARGET"] = failTarget;
        }

        using Process p = Process.Start(psi)!;
        await p.StandardInput.WriteAsync(script.AsMemory(), ct);
        p.StandardInput.Close();
        string stdout = await p.StandardOutput.ReadToEndAsync(ct);
        string stderr = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        return new CommandResult(p.ExitCode, stdout, stderr);
    }

    private IReadOnlyList<(string Target, string Name)> RecordedRenames()
    {
        string countPath = Path.Combine(_calls, "count");
        if (!File.Exists(countPath))
        {
            return [];
        }

        int count = int.Parse(File.ReadAllText(countPath), CultureInfo.InvariantCulture);
        var renames = new List<(string, string)>();
        for (int i = 1; i <= count; i++)
        {
            renames.Add((
                File.ReadAllText(Path.Combine(_calls, $"target.{i}")),
                File.ReadAllText(Path.Combine(_calls, $"name.{i}"))));
        }

        return renames;
    }

    private bool Injected => File.Exists(Path.Combine(_dir, "INJECTED"));

    private static WaitingRow Row(string paneId, string windowName, string epoch = ScannedEpoch, string? windowId = null, string? host = null)
        => new()
        {
            Pane = new TmuxPane
            {
                PaneId = paneId,
                WindowId = windowId ?? ("@" + paneId.TrimStart('%')),
                Target = "cp:1",
                Host = host,
                WindowName = windowName,
                SessionAttached = false,
                Activity = PaneActivity.Idle,
                Epoch = epoch,
            },
            Verdict = new WaitingVerdict(WaitingState.Ready, RowOwner.Operator, "ready", Assurance.High),
        };

    [Theory]
    [InlineData("x'; touch INJECTED; echo 'y")]           // single-quote break + a command
    [InlineData("x$(touch INJECTED)y")]                    // command substitution
    [InlineData("x`touch INJECTED`y")]                     // backticks
    [InlineData("x; touch INJECTED; :")]                   // bare semicolons
    [InlineData("x\ntouch INJECTED\ny")]                   // an embedded newline
    public async Task Rename_ANameCannotExecuteACommandAndIsAppliedExactly(string malicious)
    {
        // The window name is agent-controlled arbitrary text and flows into the desired name; every one of
        // these breaks a single-quoted interpolation. Encoded, none of them runs: no INJECTED file appears,
        // and the window is renamed to exactly the name the tool chose.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        WaitingRow row = Row("%1", malicious);
        string desired = WindowNaming.Apply(malicious, WindowNaming.SuffixFor(row.Verdict, row.Claim));

        int failures = await WaitingCommand.RenameAsync(
            [row], ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        Assert.False(Injected);
        Assert.Equal(0, failures);
        (string target, string name) = Assert.Single(RecordedRenames());
        Assert.Equal("@1", target);
        Assert.Equal(desired, name);
        Assert.Contains("RENAMED", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_AppliesAUnicodeNameExactly()
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        WaitingRow row = Row("%1", "café-日本語-作業");
        string desired = WindowNaming.Apply(row.Pane.WindowName, WindowNaming.SuffixFor(row.Verdict, row.Claim));

        await WaitingCommand.RenameAsync([row], ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        (_, string name) = Assert.Single(RecordedRenames());
        Assert.Equal(desired, name);
    }

    [Fact]
    public async Task Rename_ConfirmsEverySuccessfulRename()
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(rows, ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        Assert.Equal(2, RecordedRenames().Count);
        string text = diagnostics.ToString();
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("RENAMED", StringComparison.Ordinal)));
        Assert.DoesNotContain("RENAME-FAILED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_AbortsTheWholeBatchWhenTheEpochChanged()
    {
        // The sweep saw one server; the mutation sees another (a different pid), so pane ids may have been
        // recycled. Nothing is renamed, nothing is reported RENAMED, and the run is told it did not happen.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(
            rows, ShellFor(pid: "9999"), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(2, failures);
        Assert.Empty(RecordedRenames());
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-SKIPPED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAMED ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_ReportsAPartialBatchWhereOneWindowFailed()
    {
        // @2 has vanished, so its rename fails while @1 succeeds. Only the confirmed one is RENAMED; the
        // other is named as failed and counted, so the exit code can reflect it.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(
            rows, ShellFor(failTarget: "@2"), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        (string target, _) = Assert.Single(RecordedRenames());
        Assert.Equal("@1", target);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAME-FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_CorrectsQuietAndWorkingWindowsNotOnlyTheShownRows()
    {
        // The complete-set finding: rename works on the whole resolved fleet. A quiet holding window and a
        // low-confidence (working) window each carry a stale suffix and are both dropped from the report;
        // both are corrected here.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var quiet = Row("%1", "pr4448-blocked") with { Verdict = new WaitingVerdict(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High) };
        var working = Row("%2", "pr4600-ready") with { Verdict = new WaitingVerdict(WaitingState.Unknown, RowOwner.Agent, "mid-turn", Assurance.Low("busy")) };

        await WaitingCommand.RenameAsync([quiet, working], ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        var recorded = RecordedRenames();
        Assert.Contains(recorded, r => r.Name == "pr4448");
        Assert.Contains(recorded, r => r.Name == "pr4600");
    }

    [Fact]
    public async Task Rename_TargetsTheWindowIdNotThePaneId()
    {
        // Blocker 4: pane ids are recycled by break/join, so the rename target is the window id the sweep
        // saw. Even when the pane id and window id disagree, the command tmux runs names the window id.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        WaitingRow row = Row("%77", "pr4448-blocked", windowId: "@5");

        int failures = await WaitingCommand.RenameAsync([row], ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        (string target, _) = Assert.Single(RecordedRenames());
        Assert.Equal("@5", target);
        Assert.NotEqual("%77", target);
        Assert.NotEqual("@77", target);
    }

    [Fact]
    public async Task Rename_SkipsAnAmbiguousDuplicateWindowId()
    {
        // One-row-per-window collection should never produce two rows with the same window id; if it does,
        // renaming by that id could rename the wrong window, so both are skipped and counted, and nothing
        // is renamed.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: "@7"),
            Row("%2", "pr4600-stale", windowId: "@7"),
        ];

        int failures = await WaitingCommand.RenameAsync(rows, ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(2, failures);
        Assert.Empty(RecordedRenames());
        string text = diagnostics.ToString();
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("RENAME-SKIPPED", StringComparison.Ordinal)));
        Assert.DoesNotContain("RENAMED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_SkipsARowWithNoWindowId()
    {
        // A row whose window id was not captured cannot be a safe rename target, so it is skipped rather
        // than guessed; a sibling with a good id is still renamed.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: string.Empty),
            Row("%2", "pr4600-stale", windowId: "@2"),
        ];

        int failures = await WaitingCommand.RenameAsync(rows, ShellFor(), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        (string target, _) = Assert.Single(RecordedRenames());
        Assert.Equal("@2", target);
        Assert.Contains("RENAME-SKIPPED", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_OneHungHostDoesNotHoldUpTheOthers()
    {
        // Blocker 6: a per-host deadline, so a host that hangs during its rename batch is timed out and its
        // renames counted failed, while a later reachable host still runs to completion.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: "@1", host: "slow"),
            Row("%2", "pr4600-stale", windowId: "@2", host: "fast"),
        ];

        int failures = await WaitingCommand.RenameAsync(
            rows, ShellHangingOn("slow"), diagnostics, TestContext.Current.CancellationToken, perHostTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, failures);
        (string target, _) = Assert.Single(RecordedRenames());
        Assert.Equal("@2", target);
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-TIMEOUT", text, StringComparison.Ordinal);
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_AllHostsHangingAllTimeOutAndNothingIsRenamed()
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: "@1", host: "a"),
            Row("%2", "pr4600-stale", windowId: "@2", host: "b"),
        ];

        int failures = await WaitingCommand.RenameAsync(
            rows, HangingShell, diagnostics, TestContext.Current.CancellationToken, perHostTimeout: TimeSpan.FromMilliseconds(150));

        Assert.Equal(2, failures);
        Assert.Empty(RecordedRenames());
        Assert.Equal(2, diagnostics.ToString().Split('\n').Count(l => l.StartsWith("RENAME-TIMEOUT", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Rename_GenuineCallerCancellationEscapesCarryingTheCallersToken()
    {
        // Blocker 6: a real caller cancellation must dominate the per-host deadline and propagate the
        // caller's own token, mirroring CollectAsync, so the run stops rather than being mistaken for a
        // timeout.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        OperationCanceledException oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WaitingCommand.RenameAsync([Row("%1", "pr4448-blocked")], HangingShell, diagnostics, cts.Token));

        Assert.Equal(cts.Token, oce.CancellationToken);
        Assert.Empty(RecordedRenames());
    }

    // A shell whose call never returns until its token fires — a host stuck mid-rename.
    private static Func<string?, Func<string, CancellationToken, Task<CommandResult>>> HangingShell
        => _ => async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CommandResult(0, string.Empty, string.Empty);
        };

    // Hangs only the named host; every other host runs the real fake-tmux shell.
    private Func<string?, Func<string, CancellationToken, Task<CommandResult>>> ShellHangingOn(string? hangHost)
        => host => async (script, ct) =>
        {
            if (string.Equals(host, hangHost, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return await RunShellAsync(script, EpochPid, null, ct);
        };
}
