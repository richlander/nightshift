namespace Octoshift.Tests;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>A canned <see cref="IMergedPrSource"/> that returns a fixed page — no network.</summary>
internal sealed class FakeMergedPrSource : IMergedPrSource
{
    private readonly MergedPrPage _page;

    public FakeMergedPrSource(params MergedPr[] merged)
        => _page = new MergedPrPage { MergedPrs = merged };

    public FakeMergedPrSource(MergedPrPage page) => _page = page;

    public int FetchCount { get; private set; }

    public Task<MergedPrPage> FetchMergedAsync(DateTimeOffset? since, string? etag, CancellationToken ct)
    {
        FetchCount++;
        return Task.FromResult(_page);
    }
}

/// <summary>A recording <see cref="INightshiftClient"/> fake: a fixed board plus a log of land calls.</summary>
internal sealed class FakeNightshiftClient : INightshiftClient
{
    private readonly BoardState _board;

    public FakeNightshiftClient(BoardState board) => _board = board;

    public List<(string OrderBase, string Reason)> Lands { get; } = [];

    public Task<BoardState> GetBoardAsync(CancellationToken ct) => Task.FromResult(_board);

    public Task<bool> LandAsync(string orderBase, string reason, CancellationToken ct)
    {
        Lands.Add((orderBase, reason));
        return Task.FromResult(true);
    }
}
