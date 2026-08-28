namespace Turnstile.Tests;

using System.Net;
using System.Text;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// The immutable+lease rejection across the HTTP/CLI-visible surface: a real <see cref="Daemon"/> over a Unix
/// socket, driven by the same <see cref="RemoteStore"/> the CLI uses. The invariant is enforced once in the
/// shared storage layer (<c>KvStore</c>), so it reaches this surface as the daemon's uniform <c>400</c> — the
/// existing validation-error contract — rather than a silent success or a 500.
/// </summary>
public sealed class LeaseImmutableHttpTests
{
    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    [Fact]
    public async Task Create_ImmutableWithLease_OverHttp_IsRejectedWith400_AndCreatesNothing()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string socket = Path.Combine(Path.GetTempPath(), $"ts-immlease-{Guid.NewGuid():N}.sock");
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-immlease-{Guid.NewGuid():N}.db");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = Daemon.RunAsync(socket, db, cts.Token);
        try
        {
            for (int i = 0; i < 400 && !File.Exists(socket); i++)
            {
                await Task.Delay(25, ct);
            }

            Assert.True(File.Exists(socket), "daemon socket never appeared");

            using RemoteStore store = RemoteStore.Connect(socket);
            LeaseInfo lease = await store.CreateLeaseAsync(3600, ct);

            // The daemon's create endpoint accepts both ?immutable and ?lease, but the combination is refused
            // at the shared storage point and surfaces as the uniform validation 400.
            HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(
                () => store.CreateAsync("/spec", Bytes("frozen"), immutable: true, lease: lease.Id, ct: ct));
            Assert.Equal(HttpStatusCode.BadRequest, ex.StatusCode);

            // Nothing was created on the wire-visible surface.
            Assert.Null(await store.GetAsync("/spec", ct));

            // No regression: an immutable-only create over the same surface still succeeds.
            WriteResult ok = await store.CreateAsync("/spec2", Bytes("frozen"), immutable: true, ct: ct);
            Assert.Equal(WriteStatus.Created, ok.Status);
        }
        finally
        {
            cts.Cancel();
            try
            {
                await run;
            }
            catch
            {
                // Shutdown cancellation is expected.
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (string path in new[] { db, db + "-wal", db + "-shm", socket })
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                    }
                }
            }
        }
    }
}
