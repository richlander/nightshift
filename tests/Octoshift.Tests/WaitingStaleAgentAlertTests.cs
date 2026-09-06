namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The stalled-agent join (#228): a claimed PR whose own commit/comment activity has gone quiet past
/// threshold while the window still claims active progress — a case that looks, from the window's own
/// self-report alone, identical to an agent still working.
/// </summary>
public class WaitingStaleAgentAlertTests
{
    private static readonly WaitingCommand.Budget NoBudget = new(0, 0, 0, null, false);

    private static AgentState InProgress(string window, string rec = "continue")
        => AgentState.Parse($"pr=4595 head=722512e25 reviews=0/2 rec={rec}", window)!;

    private static WaitingVerdict Holding()
        => new(WaitingState.Holding, RowOwner.Nobody, "in progress", Assurance.High);

    private static WaitingRow Row(
        string windowName,
        string? repo = "owner/repo",
        string rec = "continue",
        TimeSpan? pushAge = null,
        TimeSpan? commentAge = null,
        bool? chasingMain = null)
        => new()
        {
            Pane = new TmuxPane
            {
                PaneId = "%1",
                Target = windowName,
                Host = null,
                WindowName = windowName,
                SessionAttached = false,
                Activity = PaneActivity.Idle,
            },
            Record = InProgress(windowName, rec),
            Verdict = Holding(),
            Repo = repo,
            StoppedFor = TimeSpan.FromMinutes(5),
            PushAge = pushAge,
            CommentAge = commentAge,
            ChasingMain = chasingMain,
        };

    [Fact]
    public void BuildStaleAgentAlerts_FlagsARowPastThePushThreshold()
    {
        WaitingRow[] rows = [Row("pr4595", pushAge: TimeSpan.FromHours(3), commentAge: TimeSpan.FromHours(3))];

        IReadOnlyList<WaitingCommand.StaleAgentAlert> alerts = WaitingCommand.BuildStaleAgentAlerts(rows);

        WaitingCommand.StaleAgentAlert alert = Assert.Single(alerts);
        Assert.Equal("pr4595", alert.Window);
        Assert.Equal(4595, alert.Number);
        Assert.Equal("owner/repo", alert.Repo);
    }

    [Fact]
    public void BuildStaleAgentAlerts_ARowUnderThresholdIsNotFlagged()
    {
        WaitingRow[] rows = [Row("pr4595", pushAge: TimeSpan.FromMinutes(30))];

        Assert.Empty(WaitingCommand.BuildStaleAgentAlerts(rows));
    }

    [Fact]
    public void BuildStaleAgentAlerts_ARowTheJoinNeverRanForIsNotFlagged()
    {
        // PushAge null means the activity join never ran (or failed) for this row — not evidence of
        // staleness, just an absence of evidence.
        WaitingRow[] rows = [Row("pr4595", pushAge: null)];

        Assert.Empty(WaitingCommand.BuildStaleAgentAlerts(rows));
    }

    [Fact]
    public void WriteTable_EmitsOneStaleLinePerFlaggedRow()
    {
        WaitingRow[] rows = [Row("pr4595", pushAge: TimeSpan.FromHours(3), commentAge: TimeSpan.FromHours(4), chasingMain: true)];
        var alerts = new[] { new WaitingCommand.StaleAgentAlert("pr4595", 4595, "owner/repo", TimeSpan.FromHours(3), TimeSpan.FromHours(4), true) };

        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, rows, NoBudget, [], staleAgentAlerts: alerts);
        string text = output.ToString();

        Assert.Contains("STALE pr4595 owner/repo #4595", text, StringComparison.Ordinal);
        Assert.Contains("chasing main, not advancing", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteTable_RowDetailAlwaysShowsRawActivityWhenTheJoinRan()
    {
        // Visibility, not a gate: the raw ages show up in every row's DETAIL even when the row does not
        // cross the alert threshold, same discipline as #224's FailedCheck.
        WaitingRow[] rows = [Row("pr4595", pushAge: TimeSpan.FromMinutes(20), commentAge: TimeSpan.FromMinutes(10))];

        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, rows, NoBudget, []);
        string text = output.ToString();

        Assert.Contains("activity: push", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteJson_CarriesTheStaleAgentAlertAsStructuredData()
    {
        WaitingRow[] rows = [Row("pr4595", pushAge: TimeSpan.FromHours(3), commentAge: TimeSpan.FromHours(4), chasingMain: true)];
        var alerts = new[] { new WaitingCommand.StaleAgentAlert("pr4595", 4595, "owner/repo", TimeSpan.FromHours(3), TimeSpan.FromHours(4), true) };

        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, rows, NoBudget, [], staleAgentAlerts: alerts);

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        JsonElement stale = doc.RootElement.GetProperty("stale")[0];
        Assert.Equal("pr4595", stale.GetProperty("window").GetString());
        Assert.Equal(4595, stale.GetProperty("number").GetInt32());
        Assert.Equal("owner/repo", stale.GetProperty("repo").GetString());
        Assert.True(stale.GetProperty("chasingMain").GetBoolean());
    }

    [Fact]
    public async Task EnrichWithActivityAsync_SkipsARowParkedBehindABlocker()
    {
        // "parked behind #N" is legitimately quiet, already visible in its own reason — the join must not
        // spend a call second-guessing it.
        AgentState blocked = AgentState.Parse("pr=4595 head=722512e25 blocked=100 rec=wait", "pr4595")!;
        WaitingRow row = new()
        {
            Pane = new TmuxPane { PaneId = "%1", Target = "night:pr4595", Host = null, WindowName = "pr4595", SessionAttached = false, Activity = PaneActivity.Idle },
            Record = blocked,
            Verdict = new WaitingVerdict(WaitingState.Holding, RowOwner.Nobody, "parked behind #100", Assurance.High),
            Repo = "owner/repo",
        };

        var gh = new FakeGh(); // deliberately empty: any call here is a defect
        var facts = new GhFleetPrFactsSource(["owner/repo"], new FakeCache(), gh.RunAsync);

        IReadOnlyList<WaitingRow> enriched = await WaitingCommand.EnrichWithActivityAsync([row], facts, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(enriched).PushAge);
        Assert.Empty(gh.Requests);
    }

    [Fact]
    public async Task EnrichWithActivityAsync_FetchesActivityForARowClaimingActiveProgress()
    {
        WaitingRow row = Row("pr4595");
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls/4595/commits?per_page=100"] =
                "HTTP/2.0 200\netag: \"x\"\n\n[{\"commit\":{\"committer\":{\"date\":\"2020-01-01T00:00:00Z\"}},\"parents\":[{\"sha\":\"p\"}]}]",
            ["repos/owner/repo/issues/4595/comments?per_page=1&sort=created&direction=desc"] =
                "HTTP/2.0 200\netag: \"x\"\n\n[]",
        };
        var facts = new GhFleetPrFactsSource(["owner/repo"], new FakeCache(), gh.RunAsync);

        IReadOnlyList<WaitingRow> enriched = await WaitingCommand.EnrichWithActivityAsync([row], facts, TestContext.Current.CancellationToken);

        Assert.NotNull(Assert.Single(enriched).PushAge);
    }

    /// <summary>A gh stand-in that answers by API path and records what it was asked.</summary>
    private sealed class FakeGh : Dictionary<string, string>
    {
        public List<IReadOnlyList<string>> Requests { get; } = [];

        public Task<GhResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
            Requests.Add(args);
            string path = args.Count > 1 ? args[1] : string.Empty;
            return Task.FromResult(TryGetValue(path, out string? response)
                ? new GhResult(0, response, string.Empty)
                : new GhResult(1, string.Empty, "not found (HTTP 404)"));
        }
    }

    private sealed class FakeCache : IConditionalCache
    {
        private readonly Dictionary<string, (string? ETag, string Body)> _entries = [];

        public (string? ETag, string? Body) Get(string path)
            => _entries.TryGetValue(path, out (string? ETag, string Body) entry) ? (entry.ETag, entry.Body) : (null, null);

        public void Put(string path, string? etag, string body) => _entries[path] = (etag, body);
    }
}
