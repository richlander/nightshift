namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #195: a local transaction GET returns the full <see cref="KeyState"/> including
/// <see cref="KeyState.Immutable"/>, but the txn wire DTO omitted it, so <see cref="RemoteStore"/>
/// reconstructed every GET result as mutable. These tests drive a real <see cref="Daemon"/> over a Unix
/// socket through <see cref="RemoteStore"/> and prove the remote txn GET now agrees with the local one on
/// immutability for both immutable and mutable keys, on the branch that actually runs.
/// </summary>
public sealed class TxnGetImmutableWireTests : IDisposable
{
    private readonly List<string> _dbs = [];

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static TxnOp Get(string key) => new(TxnOpKind.Get, key, null, null, false);

    private static KeyState? StateOf(TxnResult r, string key)
        => r.Responses.First(x => x.Key == key).State;

    private string NewDbPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"turnstile-txnimm-{Guid.NewGuid():N}.db");
        _dbs.Add(path);
        return path;
    }

    [Fact]
    public async Task TxnGet_ImmutableAndMutable_LocalAndRemoteAgree()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonProcess.StartAsync(ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        await remote.CreateAsync("/spec", Bytes("frozen"), immutable: true, ct: ct);
        await remote.CreateAsync("/mut", Bytes("v"), ct: ct);

        // Remote txn GET (success branch: no compares).
        TxnResult remoteResult = await remote.TxnAsync([], [Get("/spec"), Get("/mut")], [], ct);
        Assert.True(remoteResult.Succeeded);
        Assert.True(StateOf(remoteResult, "/spec")!.Immutable);
        Assert.False(StateOf(remoteResult, "/mut")!.Immutable);

        // The same transaction against an in-process store seeded identically.
        using KvStore local = KvStore.Open(NewDbPath());
        await local.CreateAsync("/spec", Bytes("frozen"), immutable: true);
        await local.CreateAsync("/mut", Bytes("v"));
        TxnResult localResult = await local.TxnAsync([], [Get("/spec"), Get("/mut")], []);

        // Local and remote agree on immutability for both keys — the parity the wire gap broke.
        Assert.Equal(StateOf(localResult, "/spec")!.Immutable, StateOf(remoteResult, "/spec")!.Immutable);
        Assert.Equal(StateOf(localResult, "/mut")!.Immutable, StateOf(remoteResult, "/mut")!.Immutable);
    }

    [Fact]
    public async Task TxnGet_OnTheFailureBranch_CarriesImmutableOverTheWire()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonProcess.StartAsync(ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        await remote.CreateAsync("/spec", Bytes("frozen"), immutable: true, ct: ct);

        // A compare that is false (the key already exists, so create_revision != 0) selects the FAILURE
        // branch, whose GET on the immutable key must still carry immutable across the wire.
        var existsAlready = new TxnCompare("/spec", TxnTarget.CreateRevision, TxnCompareOp.Equal, 0, null, null);
        TxnResult result = await remote.TxnAsync([existsAlready], [Get("/spec")], [Get("/spec")], ct);

        Assert.False(result.Succeeded);                       // failure branch ran
        Assert.True(StateOf(result, "/spec")!.Immutable);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (string db in _dbs)
        {
            foreach (string path in new[] { db, db + "-wal", db + "-shm" })
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

    /// <summary>A real daemon on a Unix socket, torn down on dispose. The db is deleted with it.</summary>
    private sealed class DaemonProcess : IAsyncDisposable
    {
        private readonly string _db;
        private readonly CancellationTokenSource _cts;
        private readonly Task _run;

        private DaemonProcess(string socket, string db, CancellationTokenSource cts, Task run)
        {
            Socket = socket;
            _db = db;
            _cts = cts;
            _run = run;
        }

        public string Socket { get; }

        public static async Task<DaemonProcess> StartAsync(CancellationToken ct)
        {
            string socket = Path.Combine(Path.GetTempPath(), $"ts-txnimm-{Guid.NewGuid():N}.sock");
            string db = Path.Combine(Path.GetTempPath(), $"turnstile-txnimm-d-{Guid.NewGuid():N}.db");
            var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task run = Daemon.RunAsync(socket, db, cts.Token);
            for (int i = 0; i < 400 && !File.Exists(socket); i++)
            {
                await Task.Delay(25, ct);
            }

            Assert.True(File.Exists(socket), "daemon socket never appeared");
            return new DaemonProcess(socket, db, cts, run);
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try
            {
                await _run;
            }
            catch
            {
                // Shutdown cancellation is expected.
            }

            _cts.Dispose();
            SqliteConnection.ClearAllPools();
            foreach (string path in new[] { _db, _db + "-wal", _db + "-shm", Socket })
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
