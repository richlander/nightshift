namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift.Commands;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The fleet-level fan-out alert (#218): when the same named blocker parks more than one observed
/// dependent, that has to be visibly worse than N indistinguishable quiet Holding rows — the live case
/// that motivated the issue was three agents each parked behind the same still-open issue with nothing
/// tying them together in the report.
/// </summary>
public class WaitingBlockerAlertTests
{
    private static readonly WaitingCommand.Budget NoBudget = new(0, 0, 0, null, false);

    private static AgentState Blocked(int blocker, string window = "pr4595")
        => AgentState.Parse($"pr=4595 head=722512e25 reviews=1/2 blocked={blocker} rec=wait", window)!;

    private static WaitingVerdict Holding(int blocker)
        => new(WaitingState.Holding, RowOwner.Nobody, $"parked behind #{blocker}", Assurance.High);

    private static WaitingRow Row(AgentState record, WaitingVerdict verdict, string target, string windowName, string? repo = "owner/repo")
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
            Record = record,
            Verdict = verdict,
            Repo = repo,
            StoppedFor = TimeSpan.FromMinutes(5),
        };

    [Fact]
    public void BuildBlockerAlerts_TwoWindowsOnTheSameBlockerProduceOneAlert()
    {
        WaitingRow[] rows =
        [
            Row(Blocked(5835, "pr4595"), Holding(5835), "night:1", "pr4595"),
            Row(Blocked(5835, "pr4600"), Holding(5835), "night:2", "pr4600"),
        ];

        IReadOnlyList<WaitingCommand.BlockerAlert> alerts = WaitingCommand.BuildBlockerAlerts(rows);

        WaitingCommand.BlockerAlert alert = Assert.Single(alerts);
        Assert.Equal(5835, alert.Number);
        Assert.Equal(2, alert.DependentCount);
        Assert.Contains("night:1", alert.Windows);
        Assert.Contains("night:2", alert.Windows);
    }

    [Fact]
    public void BuildBlockerAlerts_ASingleDependentIsNotAFanOut()
    {
        // One window parked behind a blocker is already visible as its own row; the alert exists for the
        // case that gets *less* visible as it fans out, not for the ordinary single case.
        WaitingRow[] rows = [Row(Blocked(5835), Holding(5835), "night:1", "pr4595")];

        Assert.Empty(WaitingCommand.BuildBlockerAlerts(rows));
    }

    [Fact]
    public void BuildBlockerAlerts_DifferentRepoNamespacesAreNotConflated()
    {
        // The same number in two different repos names two different things; grouping them together would
        // manufacture a fan-out that never existed.
        WaitingRow[] rows =
        [
            Row(Blocked(5835, "pr4595"), Holding(5835), "night:1", "pr4595", repo: "owner/repo-a"),
            Row(Blocked(5835, "pr4600"), Holding(5835), "night:2", "pr4600", repo: "owner/repo-b"),
        ];

        Assert.Empty(WaitingCommand.BuildBlockerAlerts(rows));
    }

    [Fact]
    public void BuildBlockerAlerts_AnAlreadyUnblockedDependentDropsOutOfTheGroup()
    {
        // Once a dependent's own verdict has already resolved to Unblocked, it has nothing left to alert
        // about; only the rows still actually parked count toward fan-out.
        WaitingVerdict unblocked = new(WaitingState.Unblocked, RowOwner.Operator, "blocker #5835 cleared; the wait behind it is over", Assurance.High);
        WaitingRow[] rows =
        [
            Row(Blocked(5835, "pr4595"), Holding(5835), "night:1", "pr4595"),
            Row(Blocked(5835, "pr4600"), unblocked, "night:2", "pr4600"),
        ];

        Assert.Empty(WaitingCommand.BuildBlockerAlerts(rows));
    }

    [Fact]
    public void WriteTable_EmitsOneBlockedLinePerFanOutBlocker()
    {
        var alerts = new[] { new WaitingCommand.BlockerAlert(5835, "owner/repo", ["night:1", "night:2"]) };
        var rows = new WaitingRow[]
        {
            Row(Blocked(5835, "pr4595"), Holding(5835), "night:1", "pr4595"),
            Row(Blocked(5835, "pr4600"), Holding(5835), "night:2", "pr4600"),
        };

        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, rows, NoBudget, [], blockerAlerts: alerts);
        string text = output.ToString();

        Assert.Contains("BLOCKED 2 window(s) parked behind open owner/repo #5835 — night:1, night:2", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteJson_CarriesTheBlockerAlertAsStructuredData()
    {
        var alerts = new[] { new WaitingCommand.BlockerAlert(5835, "owner/repo", ["night:1", "night:2"]) };
        var rows = new WaitingRow[]
        {
            Row(Blocked(5835, "pr4595"), Holding(5835), "night:1", "pr4595"),
            Row(Blocked(5835, "pr4600"), Holding(5835), "night:2", "pr4600"),
        };

        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, rows, NoBudget, [], blockerAlerts: alerts);

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        JsonElement blocker = doc.RootElement.GetProperty("blockers")[0];
        Assert.Equal(5835, blocker.GetProperty("number").GetInt32());
        Assert.Equal("owner/repo", blocker.GetProperty("repo").GetString());
        Assert.Equal(2, blocker.GetProperty("dependentCount").GetInt32());
        Assert.Equal(2, blocker.GetProperty("windows").GetArrayLength());
    }
}
