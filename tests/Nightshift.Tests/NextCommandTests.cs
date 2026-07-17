namespace Nightshift.Tests;

using Nightshift.Commands;
using Nightshift.Turnstile;
using Xunit;

public class NextCommandTests
{
    [Fact]
    public async Task WaitForChangeCore_OnCompaction_ReRangesToCurrentRevision()
    {
        long refreshed = await NextCommand.WaitForChangeCoreAsync(
            (_, _) => throw new WatchCompactedException("/", 40, 100),
            _ => Task.FromResult(101L),
            fromRevision: 40,
            budget: TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(101L, refreshed);
    }

    [Fact]
    public async Task WaitForChangeCore_OnSignal_ReturnsSignalRevision()
    {
        long refreshed = await NextCommand.WaitForChangeCoreAsync(
            (_, _) => Replay([new WatchSignal("/ready/1/op", Deleted: false, Revision: 77)]),
            _ => throw new InvalidOperationException("current revision must not be queried on event"),
            fromRevision: 40,
            budget: TimeSpan.FromSeconds(10),
            TestContext.Current.CancellationToken);

        Assert.Equal(77L, refreshed);
    }

    private static async IAsyncEnumerable<WatchSignal> Replay(IEnumerable<WatchSignal> events)
    {
        foreach (WatchSignal signal in events)
        {
            yield return signal;
            await Task.Yield();
        }
    }
}
