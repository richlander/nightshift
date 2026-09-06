namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The CI counterpart to the blocker fan-out alert (#224): when more than one currently-idle, not-yet-done
/// window is red on the very same named check in the same repo, that shared-root-cause case (e.g.
/// <c>main</c> itself will not build) has to be visibly worse than N indistinguishable quiet Holding rows.
/// </summary>
public class WaitingCheckBreakAlertTests
{
    private static readonly WaitingCommand.Budget NoBudget = new(0, 0, 0, null, false);

    private static AgentState InProgress(string window = "pr4595")
        => AgentState.Parse("pr=4595 head=722512e25 reviews=0/2 rec=wait", window)!;

    private static WaitingVerdict Holding(string? failedCheck)
        => new(WaitingState.Holding, RowOwner.Nobody,
            failedCheck is null ? "in progress" : $"in progress; CI red ({failedCheck})", Assurance.High, failedCheck);

    private static WaitingRow Row(WaitingVerdict verdict, string target, string windowName, string? repo = "owner/repo")
        => new()
        {
            Pane = new TmuxPane
            {
                PaneId = "%1",
                Target = target,
                Host = null,
                WindowName = windowName,
                SessionAttached = false,
                Activity = PaneActivity.Idle,
            },
            Record = InProgress(windowName),
            Verdict = verdict,
            Repo = repo,
            StoppedFor = TimeSpan.FromMinutes(5),
        };

    [Fact]
    public void BuildCheckBreakAlerts_TwoWindowsRedOnTheSameCheckProduceOneAlert()
    {
        WaitingRow[] rows =
        [
            Row(Holding("build"), "night:1", "pr4595"),
            Row(Holding("build"), "night:2", "pr4600"),
        ];

        IReadOnlyList<WaitingCommand.CheckBreakAlert> alerts = WaitingCommand.BuildCheckBreakAlerts(rows);

        WaitingCommand.CheckBreakAlert alert = Assert.Single(alerts);
        Assert.Equal("build", alert.CheckName);
        Assert.Equal(2, alert.WindowCount);
        Assert.Contains("night:1", alert.Windows);
        Assert.Contains("night:2", alert.Windows);
    }

    [Fact]
    public void BuildCheckBreakAlerts_ASingleWindowIsNotAFanOut()
    {
        // One window red on its own check is already visible in its row's reason; the alert exists for
        // the shared case, not the ordinary single case.
        WaitingRow[] rows = [Row(Holding("build"), "night:1", "pr4595")];

        Assert.Empty(WaitingCommand.BuildCheckBreakAlerts(rows));
    }

    [Fact]
    public void BuildCheckBreakAlerts_DifferentCheckNamesAreNotConflated()
    {
        WaitingRow[] rows =
        [
            Row(Holding("build"), "night:1", "pr4595"),
            Row(Holding("lint"), "night:2", "pr4600"),
        ];

        Assert.Empty(WaitingCommand.BuildCheckBreakAlerts(rows));
    }

    [Fact]
    public void BuildCheckBreakAlerts_DifferentRepoNamespacesAreNotConflated()
    {
        WaitingRow[] rows =
        [
            Row(Holding("build"), "night:1", "pr4595", repo: "owner/repo-a"),
            Row(Holding("build"), "night:2", "pr4600", repo: "owner/repo-b"),
        ];

        Assert.Empty(WaitingCommand.BuildCheckBreakAlerts(rows));
    }

    [Fact]
    public void BuildCheckBreakAlerts_GreenWindowsNeverGroup()
    {
        WaitingRow[] rows =
        [
            Row(Holding(null), "night:1", "pr4595"),
            Row(Holding(null), "night:2", "pr4600"),
        ];

        Assert.Empty(WaitingCommand.BuildCheckBreakAlerts(rows));
    }

    [Fact]
    public void WriteTable_EmitsOneCiredLinePerFanOutCheck()
    {
        var alerts = new[] { new WaitingCommand.CheckBreakAlert("build", "owner/repo", ["night:1", "night:2"]) };
        var rows = new WaitingRow[]
        {
            Row(Holding("build"), "night:1", "pr4595"),
            Row(Holding("build"), "night:2", "pr4600"),
        };

        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, rows, NoBudget, [], checkBreakAlerts: alerts);
        string text = output.ToString();

        Assert.Contains("CIRED 2 window(s) red on owner/repo check \"build\" — night:1, night:2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteJson_CarriesTheCheckBreakAlertAsStructuredData()
    {
        var alerts = new[] { new WaitingCommand.CheckBreakAlert("build", "owner/repo", ["night:1", "night:2"]) };
        var rows = new WaitingRow[]
        {
            Row(Holding("build"), "night:1", "pr4595"),
            Row(Holding("build"), "night:2", "pr4600"),
        };

        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, rows, NoBudget, [], checkBreakAlerts: alerts);

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        JsonElement checkBreak = doc.RootElement.GetProperty("ciBreaks")[0];
        Assert.Equal("build", checkBreak.GetProperty("check").GetString());
        Assert.Equal("owner/repo", checkBreak.GetProperty("repo").GetString());
        Assert.Equal(2, checkBreak.GetProperty("windowCount").GetInt32());
        Assert.Equal(2, checkBreak.GetProperty("windows").GetArrayLength());
    }
}
