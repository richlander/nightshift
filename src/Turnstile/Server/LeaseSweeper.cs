namespace Turnstile.Server;

using Turnstile.Storage;

/// <summary>
/// Runs the lease sweeper on a ~1s tick. Expiry is evaluated on the server clock and produces real
/// delete events — the sweeper is what turns "agent stopped renewing" into "the claim vanished."
/// </summary>
internal sealed class LeaseSweeper : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);
    private readonly KvStore _store;
    private Task? _loop;

    public LeaseSweeper(KvStore store) => _store = store;

    public void Start(CancellationToken ct) => _loop = RunAsync(ct);

    private async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct))
            {
                await _store.SweepExpiredAsync();
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
    }

    public void Dispose()
    {
        try
        {
            _loop?.Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // The loop faults only on cancellation, which is expected during shutdown.
        }
    }
}
