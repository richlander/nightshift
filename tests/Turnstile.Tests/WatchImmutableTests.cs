namespace Turnstile.Tests;

using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Turnstile.Server;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #196: a watch PUT event is a materialized view of the new key state, so it must carry
/// <see cref="WatchEvent.Immutable"/>. A DELETE is a tombstone transition and reports <c>false</c>. These
/// tests pin the direct <see cref="KvStore.ReadEvents"/> path and drive a real <see cref="Daemon"/> over a
/// Unix socket through <see cref="RemoteStore"/> so the SSE wire (put DTO + reconstruction) is proven on the
/// branch that actually runs, for both immutable and mutable keys, on backlog and live event paths.
/// </summary>
public sealed class WatchImmutableTests : IDisposable
{
    private readonly List<string> _dbs = [];

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private string NewDbPath()
    {
        string path = Path.Combine(Path.GetTempPath(), $"turnstile-watchimm-{Guid.NewGuid():N}.db");
        _dbs.Add(path);
        return path;
    }

    [Fact]
    public async Task ReadEvents_CarryImmutableForPuts_AndFalseForDeletes()
    {
        using KvStore store = KvStore.Open(NewDbPath());
        await store.CreateAsync("/spec", Bytes("frozen"), immutable: true);
        await store.CreateAsync("/mut", Bytes("v"));
        WriteResult toDelete = await store.CreateAsync("/gone", Bytes("bye"));
        await store.DeleteAsync("/gone", ifMatch: toDelete.Revision);

        IReadOnlyList<WatchEvent> events = store.ReadEvents("/", fromExclusive: 0, limit: 0);

        WatchEvent spec = events.Single(e => e.Key == "/spec");
        WatchEvent mut = events.Single(e => e.Key == "/mut");
        WatchEvent delete = events.Single(e => e is { Key: "/gone", Deleted: true });

        Assert.True(spec.Immutable);
        Assert.False(mut.Immutable);
        Assert.False(delete.Immutable); // tombstone transition never claims immutability
    }

    [Fact]
    public async Task Watch_OverTheWire_CarriesImmutableForImmutableAndMutablePuts()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonProcess.StartAsync(ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        await remote.CreateAsync("/spec", Bytes("frozen"), immutable: true, ct: ct);
        await remote.CreateAsync("/mut", Bytes("v"), ct: ct);

        List<WatchEvent> backlog = await DrainBacklogAsync(remote, "/", ct);

        WatchEvent spec = backlog.Single(e => e.Key == "/spec");
        WatchEvent mut = backlog.Single(e => e.Key == "/mut");

        Assert.True(spec.Immutable);   // the parity the wire gap broke for immutable puts
        Assert.False(mut.Immutable);
    }

    [Fact]
    public async Task Watch_LiveEvent_CarriesImmutable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonProcess.StartAsync(ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        // Subscribe on an empty store, consume the sync, then write live so the event travels the live SSE path.
        await using IAsyncEnumerator<WatchMessage> stream = remote.WatchAsync("/", fromExclusive: 0, ct).GetAsyncEnumerator(ct);
        await AdvanceToSyncAsync(stream);

        await remote.CreateAsync("/spec", Bytes("frozen"), immutable: true, ct: ct);

        WatchEvent live = await NextEventAsync(stream);
        Assert.Equal("/spec", live.Key);
        Assert.True(live.Immutable);
    }

    [Fact]
    public async Task Watch_DeleteEvent_OverTheWire_ReportsImmutableFalse()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using var daemon = await DaemonProcess.StartAsync(ct);
        using RemoteStore remote = RemoteStore.Connect(daemon.Socket);

        WriteResult created = await remote.CreateAsync("/gone", Bytes("bye"), ct: ct);
        await remote.DeleteAsync("/gone", ifMatch: created.Revision, ct: ct);

        List<WatchEvent> backlog = await DrainBacklogAsync(remote, "/", ct);
        WatchEvent delete = backlog.Single(e => e is { Key: "/gone", Deleted: true });
        Assert.False(delete.Immutable);
    }

    [Fact]
    public void WatchPutEventDto_MissingImmutable_DefaultsFalse()
    {
        // An older server omits the additive field; System.Text.Json leaves the non-nullable bool at default.
        const string json = """{"key":"/k","create_revision":1,"mod_revision":1,"value":null}""";
        WatchPutEventDto dto = JsonSerializer.Deserialize(json, TurnstileJson.Default.WatchPutEventDto)!;
        Assert.False(dto.Immutable);
    }

    [Fact]
    public void WatchPutEventDto_EmitsImmutableAsSnakeCase()
    {
        var dto = new WatchPutEventDto("/spec", 1, 1, null, Immutable: true, Value: null);
        string json = JsonSerializer.Serialize(dto, TurnstileJson.Default.WatchPutEventDto);
        Assert.Contains("\"immutable\":true", json);
    }

    [Fact]
    public void WatchEvent_PreservesSevenFieldConstructorAndDeconstruction()
    {
        // The original public positional record contract must survive: seven-argument construction and
        // seven-value deconstruction still compile, and Immutable is an additive init member defaulting false.
        var evt = new WatchEvent(5, "/k", false, 3, "lease-1", Bytes("v"), null);
        Assert.False(evt.Immutable);

        var (revision, key, deleted, createRevision, lease, value, prevValue) = evt;
        Assert.Equal(5, revision);
        Assert.Equal("/k", key);
        Assert.False(deleted);
        Assert.Equal(3, createRevision);
        Assert.Equal("lease-1", lease);
        Assert.Equal("v", Encoding.UTF8.GetString(value!));
        Assert.Null(prevValue);
    }

    [Fact]
    public void WatchEvent_ImmutableParticipatesInEqualityAndWith()
    {
        var mutable = new WatchEvent(5, "/k", false, 3, null, Bytes("v"), null);
        WatchEvent immutable = mutable with { Immutable = true };

        Assert.True(immutable.Immutable);
        Assert.NotEqual(mutable, immutable);                       // Immutable is part of value equality
        Assert.Equal(immutable, mutable with { Immutable = true }); // and reproducible via with
    }

    private static async Task<List<WatchEvent>> DrainBacklogAsync(RemoteStore remote, string prefix, CancellationToken ct)
    {
        var events = new List<WatchEvent>();
        await foreach (WatchMessage msg in remote.WatchAsync(prefix, fromExclusive: 0, ct))
        {
            if (msg is WatchSyncMessage)
            {
                break;
            }

            if (msg is WatchEventMessage evt)
            {
                events.Add(evt.Event);
            }
        }

        return events;
    }

    private static async Task AdvanceToSyncAsync(IAsyncEnumerator<WatchMessage> stream)
    {
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is WatchSyncMessage)
            {
                return;
            }
        }

        Assert.Fail("watch stream ended before the initial sync");
    }

    private static async Task<WatchEvent> NextEventAsync(IAsyncEnumerator<WatchMessage> stream)
    {
        while (await stream.MoveNextAsync())
        {
            if (stream.Current is WatchEventMessage evt)
            {
                return evt.Event;
            }
        }

        Assert.Fail("watch stream ended before the expected live event");
        throw new InvalidOperationException("unreachable");
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
            string socket = Path.Combine(Path.GetTempPath(), $"ts-watchimm-{Guid.NewGuid():N}.sock");
            string db = Path.Combine(Path.GetTempPath(), $"turnstile-watchimm-d-{Guid.NewGuid():N}.db");
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
