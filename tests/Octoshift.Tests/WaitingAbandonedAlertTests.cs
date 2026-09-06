namespace Octoshift.Tests;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The abandoned-PR scan (#225): an open, review-clean, mergeable PR that no window claims this sweep is
/// otherwise invisible to <c>octoshift waiting</c>, which only ever follows a claim in. This is the
/// opt-in outward-looking half — listing what GitHub has open and reporting the PRs no claim covers.
/// </summary>
public class WaitingAbandonedAlertTests
{
    private static readonly WaitingCommand.Budget NoBudget = new(0, 0, 0, null, false);

    private static string Response(int status, string body, params string[] extraHeaders)
    {
        string[] headers = [$"HTTP/2.0 {status}", "etag: \"fresh\"", .. extraHeaders];
        return string.Join('\n', headers) + "\n\n" + body;
    }

    private static AgentState Claiming(int prNumber, string window)
        => AgentState.Parse($"pr={prNumber} head=722512e25 reviews=2/2 rec=merge", window)!;

    private static WaitingRow ClaimedRow(int prNumber, string repo, string window)
        => new()
        {
            Pane = new TmuxPane
            {
                PaneId = "%1",
                Target = window,
                Host = null,
                WindowName = window,
                SessionAttached = false,
                Activity = PaneActivity.Idle,
            },
            Record = Claiming(prNumber, window),
            Verdict = new WaitingVerdict(WaitingState.Ready, RowOwner.Operator, "reviews 2/2, mergeable", Assurance.High),
            Repo = repo,
            StoppedFor = TimeSpan.FromMinutes(5),
        };

    [Fact]
    public async Task BuildAbandonedAlertsAsync_ReportsAnOpenCleanPrNoWindowClaims()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] =
                Response(200, """[{"number":10}]"""),
            ["repos/owner/repo/pulls/10"] = Response(
                200,
                """{"number":10,"state":"open","title":"Add widget","mergeable_state":"clean","head":{"sha":"a1b2c3d"}}"""),
            ["repos/owner/repo/commits/a1b2c3d/check-runs?per_page=100"] =
                Response(200, """{"total_count":0,"check_runs":[]}"""),
        };
        var facts = new GhFleetPrFactsSource(["owner/repo"], new FakeCache(), gh.RunAsync);

        IReadOnlyList<WaitingCommand.AbandonedAlert> alerts =
            await WaitingCommand.BuildAbandonedAlertsAsync([], facts, TestContext.Current.CancellationToken);

        WaitingCommand.AbandonedAlert alert = Assert.Single(alerts);
        Assert.Equal(10, alert.Number);
        Assert.Equal("owner/repo", alert.Repo);
        Assert.Equal("Add widget", alert.Title);
    }

    [Fact]
    public async Task BuildAbandonedAlertsAsync_SkipsAPrAWindowStillClaims()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] =
                Response(200, """[{"number":10}]"""),
            // No per-PR fetch stubbed: a claimed PR must never even be looked up.
        };
        var facts = new GhFleetPrFactsSource(["owner/repo"], new FakeCache(), gh.RunAsync);
        WaitingRow[] rows = [ClaimedRow(10, "owner/repo", "pr10")];

        IReadOnlyList<WaitingCommand.AbandonedAlert> alerts =
            await WaitingCommand.BuildAbandonedAlertsAsync(rows, facts, TestContext.Current.CancellationToken);

        Assert.Empty(alerts);
    }

    [Fact]
    public async Task BuildAbandonedAlertsAsync_SkipsAPrNotYetMergeableClean()
    {
        var gh = new FakeGh
        {
            ["repos/owner/repo/pulls?state=open&per_page=100"] =
                Response(200, """[{"number":10}]"""),
            ["repos/owner/repo/pulls/10"] = Response(
                200,
                """{"number":10,"state":"open","mergeable_state":"blocked","head":{"sha":"a1b2c3d"}}"""),
            ["repos/owner/repo/commits/a1b2c3d/check-runs?per_page=100"] =
                Response(200, """{"total_count":0,"check_runs":[]}"""),
        };
        var facts = new GhFleetPrFactsSource(["owner/repo"], new FakeCache(), gh.RunAsync);

        IReadOnlyList<WaitingCommand.AbandonedAlert> alerts =
            await WaitingCommand.BuildAbandonedAlertsAsync([], facts, TestContext.Current.CancellationToken);

        Assert.Empty(alerts);
    }

    [Fact]
    public void WriteTable_EmitsOneAbandonedLinePerPr()
    {
        var alerts = new[] { new WaitingCommand.AbandonedAlert(10, "owner/repo", "Add widget") };

        var output = new StringWriter(CultureInfo.InvariantCulture);
        WaitingCommand.WriteTable(output, [], NoBudget, [], abandonedAlerts: alerts);
        string text = output.ToString();

        Assert.Contains("ABANDONED owner/repo #10 Add widget — review-clean and mergeable, no window claims it", text, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteJson_CarriesTheAbandonedAlertAsStructuredData()
    {
        var alerts = new[] { new WaitingCommand.AbandonedAlert(10, "owner/repo", "Add widget") };

        using var stream = new MemoryStream();
        WaitingCommand.WriteJson(stream, [], NoBudget, [], abandonedAlerts: alerts);

        using JsonDocument doc = JsonDocument.Parse(Encoding.UTF8.GetString(stream.ToArray()));
        JsonElement abandoned = doc.RootElement.GetProperty("abandoned")[0];
        Assert.Equal(10, abandoned.GetProperty("number").GetInt32());
        Assert.Equal("owner/repo", abandoned.GetProperty("repo").GetString());
        Assert.Equal("Add widget", abandoned.GetProperty("title").GetString());
    }

    /// <summary>A gh stand-in that answers by API path.</summary>
    private sealed class FakeGh : Dictionary<string, string>
    {
        public Task<GhResult> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
        {
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
