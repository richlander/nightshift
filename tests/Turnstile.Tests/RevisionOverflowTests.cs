namespace Turnstile.Tests;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #198: revision allocation must never wrap <see cref="long"/>. When the durable
/// <c>committed_revision</c> sits at or one below <see cref="long.MaxValue"/>, a mutation that needs another
/// revision fails closed — before any overflowed row or counter move becomes visible — so the whole SQLite
/// transaction rolls back, the committed revision does not move, and no change pulse fires. Reads, no-op
/// transactions, and lease-only operations stay valid at exhaustion, and <c>long.MaxValue</c> itself is
/// allocatable exactly once.
///
/// These are product-level outcomes on a real <see cref="KvStore"/> seeded in the legitimate
/// compacted-history state (a committed revision above <c>MAX(kv.id)</c>, which <see cref="Schema"/> accepts).
/// A third test pins the daemon's wire contract: exhaustion surfaces as HTTP 500 with the uniform error
/// envelope, not a client-input 400.
/// </summary>
public sealed class RevisionOverflowTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-overflow-{Guid.NewGuid():N}.db");

    private const long Max = long.MaxValue;

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    /// <summary>
    /// Seeds the database at <see cref="_dbPath"/> so its durable committed revision is
    /// <paramref name="committed"/> with an empty <c>kv</c> log — the legitimate post-compaction state where
    /// the counter sits above <c>MAX(kv.id)</c>. The value is written through the real schema and the store is
    /// later reopened, so <see cref="Schema"/> reconciliation validates it rather than resetting it; nothing
    /// bypasses the schema invariants.
    /// </summary>
    private void SeedCommittedRevision(long committed)
    {
        using (KvStore init = KvStore.Open(_dbPath))
        {
            // Creates the real schema with committed_revision = 0.
        }

        SqliteConnection.ClearAllPools();
        using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            conn.Open();
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE meta SET v = $v WHERE k = 'committed_revision';";
            cmd.Parameters.AddWithValue("$v", committed.ToString(CultureInfo.InvariantCulture));
            cmd.ExecuteNonQuery();
        }

        SqliteConnection.ClearAllPools();
    }

    private KvStore OpenStoreSeeded(long committed)
    {
        SeedCommittedRevision(committed);
        KvStore store = KvStore.Open(_dbPath);   // Schema.Ensure accepts committed >= MAX(kv.id)
        Assert.Equal(committed, store.CurrentRevision);
        return store;
    }

    [Fact]
    public async Task AtMax_WriteFailsClosed_WhileReadsNoOpTxnAndLeasesStayUsable()
    {
        using KvStore store = OpenStoreSeeded(Max);

        // A write that needs a revision fails closed: no row, no counter move, no pulse.
        Task changed = store.WaitForChangeAsync();
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.CreateAsync("/k", Bytes("v")));
        Assert.Equal(Max, store.CurrentRevision);
        Assert.Null(store.Get("/k"));
        Assert.False(changed.IsCompleted);       // the failed write pulsed no watcher

        // A no-op transaction (only Gets) allocates nothing and reports the durable committed revision.
        TxnResult noop = await store.TxnAsync([], [new TxnOp(TxnOpKind.Get, "/k", null, null, false)], []);
        Assert.True(noop.Succeeded);
        Assert.Equal(Max, noop.Revision);

        // A lease create allocates no log revision, so it commits even at exhaustion.
        LeaseInfo lease = await store.CreateLeaseAsync(60);
        Assert.NotNull(lease.Id);

        // Plain reads keep working and the revision is unchanged throughout.
        Assert.Null(store.Get("/anything"));
        Assert.Equal(Max, store.CurrentRevision);
    }

    [Fact]
    public async Task AtMaxMinusOne_TwoWriteTxnRollsBackWhole_ThenOneConsumesMax_ThenFails()
    {
        using KvStore store = OpenStoreSeeded(Max - 1);

        // A transaction with two puts needs two revisions: the second overflows, so the whole txn rolls back.
        Task changed = store.WaitForChangeAsync();
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.TxnAsync(
            [],
            [
                new TxnOp(TxnOpKind.Put, "/a", Bytes("1"), null, false),
                new TxnOp(TxnOpKind.Put, "/b", Bytes("2"), null, false),
            ],
            []));

        Assert.Equal(Max - 1, store.CurrentRevision);   // whole rollback: counter unmoved
        Assert.Null(store.Get("/a"));
        Assert.Null(store.Get("/b"));
        Assert.False(changed.IsCompleted);              // no pulse

        // A single-revision write may still consume MaxValue exactly.
        WriteResult created = await store.CreateAsync("/c", Bytes("3"));
        Assert.Equal(Max, created.Revision);
        Assert.Equal(Max, store.CurrentRevision);
        Assert.Equal("3", Encoding.UTF8.GetString(store.Get("/c")!.Value!));

        // After that, every revision-requiring write fails deterministically.
        await Assert.ThrowsAsync<TurnstileRevisionExhaustedException>(() => store.CreateAsync("/d", Bytes("4")));
        Assert.Equal(Max, store.CurrentRevision);
        Assert.Null(store.Get("/d"));
    }

    [Fact]
    public async Task OverTheWire_Exhaustion_IsHttp500_WithUniformEnvelope()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Seed the on-disk store to exhaustion BEFORE the daemon opens it, then run the daemon against it.
        SeedCommittedRevision(Max);
        string socket = Path.Combine(Path.GetTempPath(), $"ts-overflow-{Guid.NewGuid():N}.sock");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task run = Daemon.RunAsync(socket, _dbPath, cts.Token);
        try
        {
            for (int i = 0; i < 400 && !File.Exists(socket); i++)
            {
                await Task.Delay(25, ct);
            }

            Assert.True(File.Exists(socket), "daemon socket never appeared");

            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (_, c) =>
                {
                    var s = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await s.ConnectAsync(new UnixDomainSocketEndPoint(socket), c);
                    return new NetworkStream(s, ownsSocket: true);
                },
            };
            using var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };

            using var content = new ByteArrayContent(Bytes("v"));
            using HttpResponseMessage res = await http.PostAsync("/kv/k", content, ct);

            // Server-side resource exhaustion is a 500 with the uniform {"error": ...} envelope, not a 400.
            Assert.Equal(HttpStatusCode.InternalServerError, res.StatusCode);
            ErrorResponse? envelope = await res.Content.ReadFromJsonAsync(TurnstileJson.Default.ErrorResponse, ct);
            Assert.NotNull(envelope);
            Assert.Contains("revision space exhausted", envelope!.Error);
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

            if (File.Exists(socket))
            {
                File.Delete(socket);
            }
        }
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
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
