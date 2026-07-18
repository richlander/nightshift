namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.Coordination;
using Octoshift.GitHub;
using Xunit;

/// <summary>Pure snapshot and transition logic for read-only wait/watch decisions.</summary>
public class ObservationDecisionTests
{
    [Fact]
    public void BuildSnapshot_ClassifiesScopedStates()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        IReadOnlyDictionary<int, PrObservation> snapshot = ObservationDecision.BuildSnapshot(
            scope,
            [
                new OpenPr(1, "nightshift/3/op-open", PrLifecycle.Open, Mergeability.Mergeable, []),
                new OpenPr(2, "nightshift/3/op-conflict", PrLifecycle.Open, Mergeability.Conflicting, []),
                new OpenPr(3, "nightshift/3/op-closed", PrLifecycle.Closed, Mergeability.Unknown, []),
                new OpenPr(99, "nightshift/9/other", PrLifecycle.Open, Mergeability.Conflicting, []),
            ],
            [
                new MergedPr(4, "nightshift/3/op-merged", DateTimeOffset.UtcNow),
            ]);

        Assert.Equal(4, snapshot.Count);
        Assert.Equal(ObservationState.Open, snapshot[1].State);
        Assert.Equal(ObservationState.Conflicting, snapshot[2].State);
        Assert.Equal(ObservationState.Closed, snapshot[3].State);
        Assert.Equal(ObservationState.Merged, snapshot[4].State);
    }

    [Fact]
    public void BuildSnapshot_MergedWinsForDuplicatePr()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        IReadOnlyDictionary<int, PrObservation> snapshot = ObservationDecision.BuildSnapshot(
            scope,
            [new OpenPr(10, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Conflicting, [])],
            [new MergedPr(10, "nightshift/3/op-a", DateTimeOffset.UtcNow)]);

        Assert.Equal(ObservationState.Merged, snapshot[10].State);
    }

    [Fact]
    public void Transitions_ReportsStateChangesAndToken()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        IReadOnlyDictionary<int, PrObservation> previous = ObservationDecision.BuildSnapshot(
            scope,
            [new OpenPr(1, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Mergeable, [])],
            []);
        IReadOnlyDictionary<int, PrObservation> current = ObservationDecision.BuildSnapshot(
            scope,
            [new OpenPr(1, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Conflicting, [])],
            []);

        PrTransition transition = Assert.Single(ObservationDecision.Transitions(previous, current));

        Assert.True(transition.IsTerminal);
        Assert.Equal(ObserveTokens.Conflict, transition.Token);
        Assert.Equal(ObservationState.Conflicting, transition.Current.State);
    }

    [Fact]
    public void TerminalNow_AndHasUnresolved_TrackTerminalSet()
    {
        ObservationScope scope = Assert.IsType<ObservationScope>(ObservationScope.Parse("/plan/3"));
        IReadOnlyDictionary<int, PrObservation> snapshot = ObservationDecision.BuildSnapshot(
            scope,
            [
                new OpenPr(1, "nightshift/3/op-a", PrLifecycle.Open, Mergeability.Conflicting, []),
                new OpenPr(2, "nightshift/3/op-b", PrLifecycle.Open, Mergeability.Mergeable, []),
            ],
            [new MergedPr(3, "nightshift/3/op-c", DateTimeOffset.UtcNow)]);

        Assert.Equal([1, 3], ObservationDecision.TerminalNow(snapshot).Select(static pr => pr.Number).ToArray());
        Assert.True(ObservationDecision.HasUnresolved(snapshot));
    }

    [Theory]
    [InlineData(0, ObserveTokens.Open)]
    [InlineData(1, ObserveTokens.Conflict)]
    [InlineData(2, ObserveTokens.Closed)]
    [InlineData(3, ObserveTokens.Merged)]
    public void TokenOf_CoversEveryObservationToken(int state, string expectedToken)
        => Assert.Equal(expectedToken, ObservationDecision.TokenOf((ObservationState)state));

    [Fact]
    public void ObserveTokens_AllResolved_IsStable()
        => Assert.Equal("ALL_RESOLVED", ObserveTokens.AllResolved);
}
