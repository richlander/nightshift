namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>Parsing edge cases for the gh-backed open-PR source: branch filtering, merged drop, and both rollup shapes.</summary>
public class GhOpenPrSourceTests
{
    [Fact]
    public void ParseOpenPrs_FiltersForeignBranchesAndDropsMerged()
    {
        const string json = """
        [
          { "number": 1, "headRefName": "nightshift/2/op-a", "state": "OPEN", "mergeable": "CONFLICTING", "statusCheckRollup": [] },
          { "number": 2, "headRefName": "feature/x", "state": "OPEN", "mergeable": "MERGEABLE", "statusCheckRollup": [] },
          { "number": 3, "headRefName": "nightshift/2/op-b", "state": "MERGED", "mergeable": "MERGEABLE", "statusCheckRollup": [] },
          { "number": 4, "headRefName": "nightshift/2/op-c", "state": "CLOSED", "mergeable": "UNKNOWN", "statusCheckRollup": [] }
        ]
        """;

        IReadOnlyList<OpenPr> open = GhOpenPrSource.ParseOpenPrs(json);

        Assert.Equal(new[] { 1, 4 }, open.Select(pr => pr.Number).ToArray());
        Assert.Equal(Mergeability.Conflicting, open[0].Mergeable);
        Assert.Equal(PrLifecycle.Open, open[0].State);
        Assert.Equal(PrLifecycle.Closed, open[1].State);
    }

    [Fact]
    public void ParseOpenPrs_NormalizesBothRollupShapes()
    {
        const string json = """
        [
          {
            "number": 1,
            "headRefName": "nightshift/2/op-a",
            "state": "OPEN",
            "mergeable": "MERGEABLE",
            "statusCheckRollup": [
              { "__typename": "CheckRun", "name": "build", "conclusion": "FAILURE", "status": "COMPLETED", "detailsUrl": "https://ci/1" },
              { "__typename": "StatusContext", "context": "legacy", "state": "ERROR", "targetUrl": "https://ci/2" }
            ]
          }
        ]
        """;

        OpenPr pr = Assert.Single(GhOpenPrSource.ParseOpenPrs(json));

        Assert.Equal(2, pr.Checks.Count);
        Assert.Equal("build", pr.Checks[0].Name);
        Assert.Equal("FAILURE", pr.Checks[0].Conclusion);
        Assert.Equal("https://ci/1", pr.Checks[0].Link);
        Assert.Equal("legacy", pr.Checks[1].Name);
        Assert.Equal("ERROR", pr.Checks[1].State);
        Assert.Equal("https://ci/2", pr.Checks[1].Link);
    }

    [Fact]
    public void ParseOpenPrs_EmptyOrMalformedIsEmpty()
    {
        Assert.Empty(GhOpenPrSource.ParseOpenPrs(""));
        Assert.Empty(GhOpenPrSource.ParseOpenPrs("not json"));
        Assert.Empty(GhOpenPrSource.ParseOpenPrs("[]"));
    }

    [Fact]
    public async Task FetchOpenAsync_ReturnsEmptyOnGhFailure()
    {
        var source = new GhOpenPrSource("owner/repo", 200, (_, _) =>
            Task.FromResult(new GhResult(1, string.Empty, "gh: not authenticated")));

        IReadOnlyList<OpenPr> open = await source.FetchOpenAsync(TestContext.Current.CancellationToken);

        Assert.Empty(open);
    }

    [Fact]
    public async Task FetchOpenAsync_PassesStateAllSearchAndJsonFields()
    {
        IReadOnlyList<string>? captured = null;
        var source = new GhOpenPrSource("owner/repo", 200, (args, _) =>
        {
            captured = args;
            return Task.FromResult(new GhResult(0, "[]", string.Empty));
        });

        await source.FetchOpenAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("--state", captured!);
        Assert.Contains("all", captured!);
        Assert.Contains("--search", captured!);
        Assert.Contains("head:nightshift/ -is:merged", captured!);
        Assert.Contains("number,headRefName,state,mergeable,statusCheckRollup", captured!);
    }
}
