namespace Turnstile.Tests;

using System.Text;
using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #197: range and watch read rows/events on one SQLite snapshot and sampled the revision on another, so
/// they could advertise a revision N without the state or event at N — and a client resuming from N skipped it
/// permanently. These tests pin the coherent-snapshot contract: the published revision/boundary and the
/// items/events it accompanies come from one read transaction, and a change racing after that snapshot is
/// excluded from the boundary and delivered on reconnect rather than skipped.
/// </summary>
public sealed class SnapshotConsistencyTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"turnstile-snap-{Guid.NewGuid():N}.db");

    private KvStore Open() => KvStore.Open(_dbPath);

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Range_RevisionAndItemsComeFromOneSnapshot_NotTwo()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));   // rev 1

        // The split order the daemon used to run: read the items on one snapshot, then — with a commit racing
        // in between — sample the revision on another. It advertises rev 2 beside items that predate /b, so a
        // client caching at rev 2 never learns about /b.
        IReadOnlyList<KeyState> itemsSplit = store.Range("/", 0, false);   // snapshot A: only /a
        await store.CreateAsync("/b", Bytes("2"));                         // rev 2 races in
        long revisionSplit = store.CurrentRevision;                        // snapshot B: 2
        Assert.Single(itemsSplit);                                         // items miss /b ...
        Assert.Equal(2, revisionSplit);                                    // ... while the revision says 2

        // The coherent read: revision and items are one snapshot. /b (rev 2) is present because the revision is
        // 2, and a commit after the read cannot retroactively change what was advertised.
        RangeReadResult range = store.RangeSnapshot("/", 0, false);
        Assert.Equal(2, range.Revision);
        Assert.Equal(2, range.Items.Count);
        Assert.All(range.Items, i => Assert.True(i.ModRevision <= range.Revision));

        await store.CreateAsync("/c", Bytes("3"));   // rev 3, after the snapshot
        Assert.Equal(2, range.Revision);             // unchanged: the snapshot was fixed
        Assert.Equal(2, range.Items.Count);
    }

    [Fact]
    public async Task Watch_SyncBoundaryAndItsEventsComeFromOneSnapshot_NotTwo()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));   // rev 1

        // The old split: drain events on one snapshot, then sample the sync revision on another with a commit
        // racing between. Sync 2 is advertised though only event 1 was delivered — event 2 is skipped.
        IReadOnlyList<WatchEvent> eventsSplit = store.ReadEvents("/", 0, 256);   // [rev 1]
        await store.CreateAsync("/b", Bytes("2"));                              // rev 2 races in
        long syncSplit = store.CurrentRevision;                                 // 2
        Assert.True(syncSplit > eventsSplit[^1].Revision);                     // over-advertised past delivered

        // The coherent batch: boundary and events are one snapshot. The boundary never exceeds the last
        // delivered matching event here, so a sync at the boundary cannot skip one.
        EventBatch batch = store.ReadEventBatch("/", 0, 256);
        Assert.All(batch.Events, e => Assert.True(e.Revision <= batch.Boundary));
        Assert.Equal(batch.Boundary, batch.Events[^1].Revision);

        // A commit after the batch is beyond its boundary and is delivered on reconnect from that boundary.
        await store.CreateAsync("/c", Bytes("3"));   // rev 3
        Assert.Equal(2, batch.Boundary);             // unchanged
        EventBatch reconnect = store.ReadEventBatch("/", batch.Boundary, 256);
        WatchEvent racing = Assert.Single(reconnect.Events);
        Assert.Equal("/c", racing.Key);
        Assert.Equal(3, racing.Revision);
    }

    [Fact]
    public async Task Watch_CommitRacingBetweenBoundaryAndSync_IsExcludedAndDeliveredOnReconnect()
    {
        using KvStore store = Open();
        await store.CreateAsync("/a", Bytes("1"));   // rev 1

        // Fire a racing commit in exactly the #197 window: after the watcher's catch-up boundary snapshot is
        // taken, before it publishes its sync. Under the old order (sync = a later CurrentRevision) this rev-2
        // commit would be advertised in the sync though never delivered; under the coherent order the boundary
        // was fixed from the same snapshot as the events, so it excludes rev 2.
        store.OnCaughtUpBeforeSyncForTests = async () =>
        {
            store.OnCaughtUpBeforeSyncForTests = null;   // once only
            await store.CreateAsync("/b", Bytes("2"));   // rev 2 races in
        };

        long sync = -1;
        var deliveredBeforeSync = new List<long>();
        await foreach (WatchMessage msg in store.WatchAsync("/", 0, Ct))
        {
            if (msg is WatchEventMessage e)
            {
                deliveredBeforeSync.Add(e.Event.Revision);
            }
            else if (msg is WatchSyncMessage s)
            {
                sync = s.Revision;
                break;
            }
        }

        // The sync excludes the racing rev 2 — it advertises only the boundary that was actually delivered.
        Assert.Equal(1, sync);
        Assert.Equal([1], deliveredBeforeSync);
        Assert.DoesNotContain(2, deliveredBeforeSync);

        // Resuming from the advertised sync delivers rev 2 rather than skipping it — the #197 failure mode.
        EventBatch reconnect = store.ReadEventBatch("/", sync, 256);
        Assert.Contains(reconnect.Events, ev => ev.Key == "/b" && ev.Revision == 2);
    }

    [Fact]
    public async Task Watch_SyncBoundaryMayExceedLastMatchingEvent_WhenLatestCommitIsAnotherPrefix()
    {
        // The boundary is the committed revision, not the last matching event — a commit under a different
        // prefix legitimately advances it. Resuming from that boundary is still safe: there is no matching
        // event in (lastMatching, boundary], so nothing is skipped.
        using KvStore store = Open();
        await store.CreateAsync("/watched/a", Bytes("1"));   // rev 1 (matches)
        await store.CreateAsync("/other/b", Bytes("2"));     // rev 2 (does not match)

        EventBatch batch = store.ReadEventBatch("/watched/", 0, 256);
        Assert.Equal(2, batch.Boundary);                     // committed revision
        WatchEvent only = Assert.Single(batch.Events);
        Assert.Equal(1, only.Revision);                      // only the matching event
        Assert.True(batch.Boundary >= only.Revision);

        // Resuming from the boundary skips nothing under the watched prefix.
        Assert.Empty(store.ReadEventBatch("/watched/", batch.Boundary, 256).Events);
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
