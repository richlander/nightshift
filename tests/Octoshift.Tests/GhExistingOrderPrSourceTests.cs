namespace Octoshift.Tests;

using Octoshift.GitHub;
using Xunit;

/// <summary>
/// Parsing and transport behavior for the existing-order-PR branch source used by outbound PR-open
/// idempotency.
/// </summary>
public class GhExistingOrderPrSourceTests
{
    [Fact]
    public void TryParseOpenOrMergedBranches_FiltersByStateAndScope()
    {
        const string json = """
        [
          { "headRefName": "nightshift/2/op-a", "state": "OPEN" },
          { "headRefName": "nightshift/2/op-b", "state": "MERGED" },
          { "headRefName": "nightshift/2/op-c", "state": "CLOSED" },
          { "headRefName": "feature/x", "state": "OPEN" }
        ]
        """;

        bool ok = GhExistingOrderPrSource.TryParseOpenOrMergedBranches(
            json,
            "nightshift/",
            exactBranch: false,
            out HashSet<string>? branches);

        Assert.True(ok);
        Assert.Equal(
            ["nightshift/2/op-a", "nightshift/2/op-b"],
            branches.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task FetchOpenOrMergedAsync_ReturnsFailureOnGhError()
    {
        var source = new GhExistingOrderPrSource("owner/repo", 200, (_, _) =>
            Task.FromResult(new GhResult(1, string.Empty, "not authenticated")));

        ExistingOrderPrsSnapshot snapshot = await source.FetchOpenOrMergedAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.Success);
        Assert.Empty(snapshot.OpenOrMergedHeadBranches);
    }

    [Fact]
    public async Task FetchOpenOrMergedAsync_UsesHeadSearchPattern()
    {
        IReadOnlyList<string>? captured = null;
        var source = new GhExistingOrderPrSource("owner/repo", 200, (args, _) =>
        {
            captured = args;
            return Task.FromResult(new GhResult(0, "[]", string.Empty));
        });

        ExistingOrderPrsSnapshot snapshot = await source.FetchOpenOrMergedAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.Success);
        Assert.NotNull(captured);
        Assert.Contains("--search", captured!);
        Assert.Contains("head:nightshift/", captured!);
        Assert.Contains("headRefName,state", captured!);
    }
}
