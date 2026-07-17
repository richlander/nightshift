namespace Octoshift.Tests;

using Octoshift.Coordination;
using Octoshift.GitHub;

/// <summary>A canned <see cref="IMergedPrSource"/> that returns a fixed page — no network.</summary>
internal sealed class FakeMergedPrSource : IMergedPrSource
{
    private readonly Queue<MergedPrPage> _pages;

    public FakeMergedPrSource()
        => _pages = new Queue<MergedPrPage>([new MergedPrPage { MergedPrs = [] }]);

    public FakeMergedPrSource(params MergedPr[] merged)
        => _pages = new Queue<MergedPrPage>([new MergedPrPage { MergedPrs = merged }]);

    public FakeMergedPrSource(params MergedPrPage[] pages) => _pages = new Queue<MergedPrPage>(pages);

    public int FetchCount { get; private set; }
    public List<DateTimeOffset?> SinceArgs { get; } = [];

    public Task<MergedPrPage> FetchMergedAsync(DateTimeOffset? since, string? etag, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FetchCount++;
        SinceArgs.Add(since);
        return Task.FromResult(_pages.Count > 0 ? _pages.Dequeue() : new MergedPrPage { MergedPrs = [] });
    }
}

/// <summary>A canned <see cref="IOpenPrSource"/> that returns fixed pages of open PRs — no network.</summary>
internal sealed class FakeOpenPrSource : IOpenPrSource
{
    private readonly Queue<IReadOnlyList<OpenPr>> _pages;

    public FakeOpenPrSource(params OpenPr[] open)
        => _pages = new Queue<IReadOnlyList<OpenPr>>([open]);

    public FakeOpenPrSource(params IReadOnlyList<OpenPr>[] pages)
        => _pages = new Queue<IReadOnlyList<OpenPr>>(pages);

    public int FetchCount { get; private set; }

    public Task<IReadOnlyList<OpenPr>> FetchOpenAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        FetchCount++;
        return Task.FromResult(_pages.Count > 1 ? _pages.Dequeue() : _pages.Count == 1 ? _pages.Peek() : (IReadOnlyList<OpenPr>)[]);
    }
}

/// <summary>A recording <see cref="INightshiftClient"/> fake: a fixed board plus a log of land calls.</summary>
internal sealed class FakeNightshiftClient : INightshiftClient
{
    private readonly Queue<BoardState> _boards;
    private readonly Queue<bool> _landResults;

    public FakeNightshiftClient(BoardState board, params bool[] landResults)
        : this([board], landResults)
    {
    }

    public FakeNightshiftClient(IReadOnlyList<BoardState> boards, params bool[] landResults)
    {
        _boards = new Queue<BoardState>(boards);
        _landResults = new Queue<bool>(landResults);
    }

    public List<(string OrderBase, string Reason)> Lands { get; } = [];

    public List<(string OrderBase, string Directive)> Reworks { get; } = [];

    public Task<BoardState> GetBoardAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_boards.Count > 1 ? _boards.Dequeue() : _boards.Peek());
    }

    public Task<bool> LandAsync(string orderBase, string reason, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Lands.Add((orderBase, reason));
        return Task.FromResult(_landResults.Count > 0 ? _landResults.Dequeue() : true);
    }

    public Task<bool> ReworkAsync(string orderBase, string directive, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Reworks.Add((orderBase, directive));
        return Task.FromResult(true);
    }
}
