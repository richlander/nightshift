namespace Octoshift.Tests;

using System.Globalization;
using System.Text.RegularExpressions;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// How <see cref="WaitingCommand.RenameAsync"/> accounts for what the rename batch reports: which rows it
/// even hands to the shell, which it counts renamed, and how it treats an epoch abort, a partial batch, a
/// per-host timeout and a caller cancellation. The shell is faked at the seam, echoing exactly the
/// confirmation markers the generated script would emit — the script's real behaviour against a live tmux
/// server (byte-safe names, the atomic epoch guard, a restart recycling ids) is proven separately in
/// <see cref="TmuxRenameIntegrationTests"/>, which runs it for real.
/// </summary>
public sealed class RenameTests
{
    private const string ScannedEpoch = "4242:1755900000";

    // A scanned second and an observation second one second later, so a Row is activity-fresh by default
    // (its last activity strictly predates the sweep's observation second) and passes the rename's
    // whole-second-quiescence gate; the same-second/too-recent case is exercised explicitly below.
    private const string FreshActivity = "1755900000";
    private const string FreshObservation = "1755900001";

    private static WaitingRow Row(
        string paneId,
        string windowName,
        string epoch = ScannedEpoch,
        string? windowId = null,
        string? host = null,
        string activity = FreshActivity,
        string observation = FreshObservation)
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
                ActivityStamp = activity,
                ObservationSecond = observation,
                Epoch = epoch,
            },
            Verdict = new WaitingVerdict(WaitingState.Ready, RowOwner.Operator, "ready", Assurance.High),
        };

    // The run-shell nonce and the window ids the generated script would rename, read back out of the
    // script so a fake shell can echo exactly the markers a live tmux would on success.
    private static (string Nonce, string[] WindowIds) Parse(string script)
        => (Regex.Match(script, "([0-9a-f]+):epoch").Groups[1].Value,
            [.. Regex.Matches(script, @"rename-window -t (@[0-9]+)").Select(m => m.Groups[1].Value)]);

    private static CommandResult Confirm(string script, Func<string, bool>? include = null)
    {
        (string nonce, string[] ids) = Parse(script);
        return new CommandResult(0, string.Join('\n', ids.Where(id => include?.Invoke(id) ?? true).Select(id => $"{nonce}:ok:{id}")), string.Empty);
    }

    // Every rename in the batch is confirmed, as a live server that kept its generation would.
    private static Func<string?, Func<string, CancellationToken, Task<CommandResult>>> SucceedingShell
        => _ => (script, _) => Task.FromResult(Confirm(script));

    // The server generation moved: each window's if-shell takes its else branch and prints a per-window
    // epoch marker, so nothing is confirmed.
    private static Func<string?, Func<string, CancellationToken, Task<CommandResult>>> EpochMismatchShell
        => _ => (script, _) =>
        {
            (string nonce, string[] ids) = Parse(script);
            return Task.FromResult(new CommandResult(0, string.Join('\n', ids.Select(id => $"{nonce}:epoch:{id}")), string.Empty));
        };

    // A shell whose call never returns until its token fires — a host stuck mid-rename.
    private static Func<string?, Func<string, CancellationToken, Task<CommandResult>>> HangingShell
        => _ => async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new CommandResult(0, string.Empty, string.Empty);
        };

    // Hangs only the named host; every other host confirms its renames.
    private static Func<string?, Func<string, CancellationToken, Task<CommandResult>>> ShellHangingOn(string? hangHost)
        => host => async (script, ct) =>
        {
            if (string.Equals(host, hangHost, StringComparison.Ordinal))
            {
                await Task.Delay(Timeout.Infinite, ct);
            }

            return Confirm(script);
        };

    [Fact]
    public async Task Rename_ConfirmsEverySuccessfulRename()
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(rows, SucceedingShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        string text = diagnostics.ToString();
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("RENAMED", StringComparison.Ordinal)));
        Assert.DoesNotContain("RENAME-FAILED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_DefersAPaneWhoseActivityIsInTheObservationSecondSoTheSameSecondBlindSpotIsClosed()
    {
        // The pane's last activity is the SAME second the sweep observed it, so a resume inside that second
        // could stamp window_activity to the very value the guard compares against — undetectable. Rather
        // than name it on evidence the guard cannot defend, the rename defers it: no rename, no shell call,
        // and it does not cost the exit code (a benign wait for a later sweep, not a failure).
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", activity: "1755900000", observation: "1755900000"),
        ];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (_, _) => throw new Xunit.Sdk.XunitException("a deferred pane must not reach the shell"),
            diagnostics,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-DEFERRED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAMED", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("1755900001", "1755900000")] // activity AFTER observation — a clock step; fail closed
    [InlineData("", "1755900001")]            // no activity stamp
    [InlineData("1755900000", "")]            // no observation second (an older-format or clock-failed sweep)
    [InlineData("01", "1755900001")]          // non-canonical activity stamp
    public async Task Rename_DefersOnAnyNonStrictlyOlderOrMalformedSnapshot(string activity, string observation)
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", activity: activity, observation: observation)];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (_, _) => throw new Xunit.Sdk.XunitException("a deferred pane must not reach the shell"),
            diagnostics,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        Assert.Contains("RENAME-DEFERRED", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_RenamesAPaneWhoseActivityStrictlyPredatesTheObservationSecond()
    {
        // The complement of the deferral: a pane quiescent into a strictly older second passes the gate and
        // is renamed. One fresh, one same-second — only the fresh one is confirmed; the other is deferred,
        // not failed.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", activity: "1755900000", observation: "1755900001"),
            Row("%2", "pr4600-stale", activity: "1755900001", observation: "1755900001"),
        ];

        int failures = await WaitingCommand.RenameAsync(rows, SucceedingShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Contains("RENAME-DEFERRED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_AbortsTheWholeBatchWhenTheEpochChanged()
    {
        // The sweep saw one server; the mutation sees another. The batch's epoch guard takes its else
        // branch, nothing is renamed or reported RENAMED, and the run is told it did not happen.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(rows, EpochMismatchShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(2, failures);
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-SKIPPED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAMED ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_ReportsAPartialBatchWhereOneWindowFailed()
    {
        // @2 vanished mid-batch, so its rename printed no confirmation while @1's did. Only the confirmed
        // one is RENAMED; the other is named as failed and counted, so the exit code can reflect it.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked"), Row("%2", "pr4600-stale")];

        int failures = await WaitingCommand.RenameAsync(
            rows, _ => (script, _) => Task.FromResult(Confirm(script, id => id != "@2")), diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAME-FAILED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_CorrectsQuietAndWorkingWindowsNotOnlyTheShownRows()
    {
        // The complete-set finding: rename works on the whole resolved fleet. A quiet holding window and a
        // low-confidence (working) window each carry a stale suffix and are both dropped from the report;
        // both are still corrected here.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        var quiet = Row("%1", "pr4448-blocked") with { Verdict = new WaitingVerdict(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High) };
        var working = Row("%2", "pr4600-ready") with { Verdict = new WaitingVerdict(WaitingState.Unknown, RowOwner.Agent, "mid-turn", Assurance.Low("busy")) };

        int failures = await WaitingCommand.RenameAsync([quiet, working], SucceedingShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(0, failures);
        string text = diagnostics.ToString();
        Assert.Contains("-> pr4448", text, StringComparison.Ordinal);
        Assert.Contains("-> pr4600", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_SkipsAnAmbiguousDuplicateWindowId()
    {
        // One-row-per-window collection should never produce two rows with the same window id; if it does,
        // renaming by that id could rename the wrong window, so both are skipped and counted, and the
        // shell is never even asked.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: "@7"),
            Row("%2", "pr4600-stale", windowId: "@7"),
        ];

        int failures = await WaitingCommand.RenameAsync(rows, SucceedingShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(2, failures);
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

        int failures = await WaitingCommand.RenameAsync(rows, SucceedingShell, diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Contains("RENAME-SKIPPED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_OneHungHostDoesNotHoldUpTheOthers()
    {
        // A per-host deadline, so a host that hangs during its rename batch is timed out and its renames
        // counted failed, while a later reachable host still runs to completion.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows =
        [
            Row("%1", "pr4448-blocked", windowId: "@1", host: "slow"),
            Row("%2", "pr4600-stale", windowId: "@2", host: "fast"),
        ];

        int failures = await WaitingCommand.RenameAsync(
            rows, ShellHangingOn("slow"), diagnostics, TestContext.Current.CancellationToken, perHostTimeout: TimeSpan.FromMilliseconds(200));

        Assert.Equal(1, failures);
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
        string text = diagnostics.ToString();
        Assert.Equal(2, text.Split('\n').Count(l => l.StartsWith("RENAME-TIMEOUT", StringComparison.Ordinal)));
        Assert.DoesNotContain("RENAMED", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_GenuineCallerCancellationEscapesCarryingTheCallersToken()
    {
        // A real caller cancellation must dominate the per-host deadline and propagate the caller's own
        // token, mirroring CollectAsync, so the run stops rather than being mistaken for a timeout.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        OperationCanceledException oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => WaitingCommand.RenameAsync([Row("%1", "pr4448-blocked")], HangingShell, diagnostics, cts.Token));

        Assert.Equal(cts.Token, oce.CancellationToken);
    }

    [Fact]
    public async Task Rename_ReportsEachWindowIndependentlyOnAMidBatchRestart()
    {
        // Blocker 2: a restart between windows confirms the earlier one and mismatches the later. The
        // earlier success stays RENAMED even though a later window's guard failed; the later is
        // RENAME-SKIPPED, not silently discarded because a mismatch appeared elsewhere.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", windowId: "@1"), Row("%2", "pr4600-stale", windowId: "@2")];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (script, _) => Task.FromResult(new CommandResult(0, $"{Parse(script).Nonce}:ok:@1\n{Parse(script).Nonce}:epoch:@2", string.Empty)),
            diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAME-SKIPPED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_ReportsAStaleMarkerAsSkippedNotRenamed()
    {
        // Round 7: the server is unchanged but the window's name or state moved since the sweep, so its
        // guard prints a stale marker. That is a skip with its own reason, never a rename, and it costs the
        // exit code exactly as a server-changed skip does.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", windowId: "@1")];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (script, _) => Task.FromResult(new CommandResult(0, $"{Parse(script).Nonce}:stale:@1", string.Empty)),
            diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-SKIPPED", text, StringComparison.Ordinal);
        Assert.Contains("changed since the sweep", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAMED ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rename_AccountsStaleAndConfirmedWindowsIndependently()
    {
        // A mixed batch: one window renamed, another found stale (its identity moved). The confirmed one
        // stays RENAMED; the stale one is skipped, not discarded because a sibling changed.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", windowId: "@1"), Row("%2", "pr4600-stale", windowId: "@2")];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (script, _) => Task.FromResult(new CommandResult(0, $"{Parse(script).Nonce}:ok:@1\n{Parse(script).Nonce}:stale:@2", string.Empty)),
            diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAMED", StringComparison.Ordinal));
        Assert.Single(text.Split('\n'), l => l.StartsWith("RENAME-SKIPPED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rename_IgnoresAMarkerForAnUnrequestedWindow()
    {
        // A marker naming a window this host never requested confers nothing; the requested window still
        // has no marker of its own, so it is failed rather than credited by the stray one.
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", windowId: "@1")];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (script, _) => Task.FromResult(new CommandResult(0, $"{Parse(script).Nonce}:ok:@999", string.Empty)),
            diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        Assert.Contains("RENAME-FAILED", diagnostics.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(":ok:@1\n{0}:ok:@1")]     // duplicate confirmation
    [InlineData(":ok:@1\n{0}:epoch:@1")]  // both confirmed and mismatched
    [InlineData(":ok:@1\n{0}:stale:@1")]  // both confirmed and stale
    [InlineData(":stale:@1\n{0}:epoch:@1")] // both stale and server-changed
    public async Task Rename_FailsClosedOnDuplicateOrConflictingMarkers(string tail)
    {
        var diagnostics = new StringWriter(CultureInfo.InvariantCulture);
        IReadOnlyList<WaitingRow> rows = [Row("%1", "pr4448-blocked", windowId: "@1")];

        int failures = await WaitingCommand.RenameAsync(
            rows,
            _ => (script, _) =>
            {
                string nonce = Parse(script).Nonce;
                return Task.FromResult(new CommandResult(0, nonce + string.Format(System.Globalization.CultureInfo.InvariantCulture, tail, nonce), string.Empty));
            },
            diagnostics, TestContext.Current.CancellationToken);

        Assert.Equal(1, failures);
        string text = diagnostics.ToString();
        Assert.Contains("RENAME-FAILED", text, StringComparison.Ordinal);
        Assert.DoesNotContain("RENAMED ", text, StringComparison.Ordinal);
    }
}
