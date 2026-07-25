namespace Nightshift.Tests;

using System.Text;
using Nightshift.Commands;
using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// Prereq-resolution-via-escalation (stacked orders §4, issue 123): a worker that cannot reach its
/// <c>base-ref</c> in the local object database self-raises a prereq-unreachable escalation on the existing
/// andon cord; the coordinator publishes the base to origin; <c>check</c> re-arms the worker once the base is
/// reachable. These cover the pure decisions on that path — the reachability probe (<c>next</c>), the re-arm
/// classification (<c>check</c>), the reason recognizer (<c>escalate</c>), and the coordinator's distinct
/// prereq transition (<c>coordinate</c>).
/// </summary>
public class PrereqEscalationTests
{
    [Fact]
    public void PrereqReason_IsRecognizedByPrefix()
    {
        string reason = EscalateCommand.PrereqUnreachableReason("a1b2c3d", "nightshift/stacked/child");

        Assert.StartsWith(EscalateCommand.PrereqUnreachablePrefix, reason);
        Assert.True(EscalateCommand.IsPrereqUnreachableReason(reason));
        Assert.Contains("a1b2c3d", reason);
        Assert.Contains("nightshift/stacked/child", reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("review did not converge after 4 rounds")]
    [InlineData("  prefixed by spaces then unrelated")]
    public void NonPrereqReasons_AreNotRecognized(string? reason)
        => Assert.False(EscalateCommand.IsPrereqUnreachableReason(reason));

    [Fact]
    public void PrereqReason_ToleratesLeadingWhitespace()
        => Assert.True(EscalateCommand.IsPrereqUnreachableReason($"   {EscalateCommand.PrereqUnreachablePrefix} base-ref 'x' ..."));

    [Fact]
    public void BaseRefReachable_ResolvedRef_IsReachable()
        => Assert.True(NextCommand.BaseRefReachable("feature/contract", _ => "a1b2c3dsha"));

    [Fact]
    public void BaseRefReachable_UnresolvedRef_IsNotReachable()
        => Assert.False(NextCommand.BaseRefReachable("feature/contract", _ => null));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("main")]
    public void BaseRefReachable_DefaultBaseRef_IsAlwaysReachableWithoutProbing(string? baseRef)
    {
        bool probed = false;
        bool reachable = NextCommand.BaseRefReachable(baseRef, _ =>
        {
            probed = true;
            return null;
        });

        Assert.True(reachable);
        Assert.False(probed);
    }

    [Fact]
    public void ClassifyPrereq_PrereqReasonReachable_Resolves()
    {
        string reason = EscalateCommand.PrereqUnreachableReason("sha", "branch");
        Assert.Equal(CheckCommand.PrereqOutcome.Resolved, CheckCommand.ClassifyPrereq("escalated", reason, reachable: true));
    }

    [Fact]
    public void ClassifyPrereq_PrereqReasonUnreachable_Parks()
    {
        string reason = EscalateCommand.PrereqUnreachableReason("sha", "branch");
        Assert.Equal(CheckCommand.PrereqOutcome.Parked, CheckCommand.ClassifyPrereq("escalated", reason, reachable: false));
    }

    [Theory]
    [InlineData("escalated", "review did not converge")]
    [InlineData("done", null)]
    [InlineData(null, null)]
    public void ClassifyPrereq_NonPrereqEscalation_IsNotPrereq(string? status, string? reason)
        => Assert.Equal(CheckCommand.PrereqOutcome.NotPrereq, CheckCommand.ClassifyPrereq(status, reason, reachable: false));

    [Fact]
    public async Task Coordinate_PrereqEscalation_SurfacesPrereqTransition()
    {
        string orderBase = "/plan/stacked/order/child";
        string stateKey = $"{orderBase}/state";
        string reason = EscalateCommand.PrereqUnreachableReason("a1b2c3d", "nightshift/stacked/child");
        var values = new Dictionary<string, string>
        {
            [stateKey] = $"{{\"status\":\"escalated\",\"reason\":{System.Text.Json.JsonSerializer.Serialize(reason)}}}",
        };
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(stateKey, Deleted: false, Revision: 60)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal("prereq", outcome!.Transition);
        Assert.Equal("escalated", outcome.Status);
        Assert.Equal(
            $"COORD plan=/plan/stacked order={orderBase} transition=prereq status=escalated",
            outcome.Render());
    }

    [Fact]
    public async Task Coordinate_JudgmentEscalation_StaysEscalatedTransition()
    {
        string orderBase = "/plan/stacked/order/child";
        string stateKey = $"{orderBase}/state";
        var values = new Dictionary<string, string>
        {
            [stateKey] = "{\"status\":\"escalated\",\"reason\":\"design looks wrong as I build it\"}",
        };
        var predicate = new CoordinateCommand.CoordinatePredicate();

        CoordinateCommand.CoordinateOutcome? outcome = await predicate.TryMatchAsync(
            new FilteredWaitEngine.WatchEdge("plan", new WatchSignal(stateKey, Deleted: false, Revision: 61)),
            BuildGetter(values),
            TestContext.Current.CancellationToken);

        Assert.NotNull(outcome);
        Assert.Equal("escalated", outcome!.Transition);
    }

    private static Func<string, CancellationToken, Task<KvItem?>> BuildGetter(Dictionary<string, string> values)
        => (key, _) => Task.FromResult(values.TryGetValue(key, out string? value)
            ? new KvItem(key, CreateRevision: 1, ModRevision: 1, Lease: null, Immutable: false, Encoding.UTF8.GetBytes(value))
            : null);
}
