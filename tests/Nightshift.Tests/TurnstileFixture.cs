namespace Nightshift.Tests;

using Nightshift.Turnstile;
using Xunit;

/// <summary>
/// Hosts a real Turnstile daemon in-process on a temp Unix socket + db, so Nightshift's wire client can be
/// exercised end-to-end (the same HTTP/JSON/SSE path production uses). Referencing Turnstile here is a
/// test-only affordance; the product never links the assembly.
/// </summary>
public sealed class TurnstileFixture : IAsyncLifetime
{
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];
    private readonly CancellationTokenSource _cts = new();
    private Task<int>? _daemon;
    private string _dir = string.Empty;

    public string Socket { get; private set; } = string.Empty;

    internal TurnstileClient Connect() => TurnstileClient.Connect(Socket);

    public async ValueTask InitializeAsync()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"ns-test-{_id}");
        Directory.CreateDirectory(_dir);
        // Keep the socket path short: macOS caps Unix socket paths near 104 bytes.
        Socket = $"/tmp/ns-{_id}.sock";
        string db = Path.Combine(_dir, "test.db");

        _daemon = global::Turnstile.Server.Daemon.RunAsync(Socket, db, _cts.Token);

        using TurnstileClient client = Connect();
        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                await client.CurrentRevisionAsync(CancellationToken.None);
                return;
            }
            catch (Exception)
            {
                await Task.Delay(50);
            }
        }

        throw new InvalidOperationException("turnstile daemon did not become ready in time");
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try
        {
            if (_daemon is not null)
            {
                await _daemon;
            }
        }
        catch (Exception)
        {
            // Cancelling app.RunAsync is a normal shutdown, not a failure.
        }

        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // Best-effort temp cleanup.
        }

        try
        {
            File.Delete(Socket);
        }
        catch (Exception)
        {
            // Best-effort socket cleanup.
        }

        _cts.Dispose();
    }
}
