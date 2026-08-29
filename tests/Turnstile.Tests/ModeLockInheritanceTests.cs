namespace Turnstile.Tests;

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202 hardening: the mode lock's OS-release property — "a dead owner never wedges the database,
/// because the OS drops its <c>flock</c> when the owning process exits or crashes" — only holds if no
/// <em>other</em> process keeps the lock's open file description alive. On Unix a child inherits every
/// descriptor that is not close-on-exec across <c>fork</c>+<c>exec</c>, so without <c>FD_CLOEXEC</c> on the
/// lock fd a child spawned while the lock is held would keep the <c>flock</c> alive after the owner released
/// or crashed — silently wedging the next daemon's exclusive acquire.
///
/// <para>These prove the fix at a real process boundary: an owner takes the lock, spawns a genuinely separate
/// long-lived child <em>while holding it</em>, then releases. If the child had inherited the fd the re-acquire
/// would fail with <see cref="TurnstileDatabaseInUseException"/>; because the lock fd is opened
/// <c>O_CLOEXEC</c>, the child never holds it and the re-acquire succeeds. Remove <c>O_CLOEXEC</c> from the
/// open flags and both tests fail — they are the mutation guard for the close-on-exec property (on Unix
/// <see cref="Process"/>.<see cref="Process.Start()"/> <c>fork</c>+<c>exec</c>s and inherits exactly those
/// descriptors that are not close-on-exec, so the boundary is real, not simulated).</para>
///
/// <para>The <c>O_CLOEXEC</c> open, and thus this guard, live on the shared <see cref="FileLock"/> primitive
/// (#212) that both <see cref="ModeLock"/> and <see cref="SocketLock"/> build on. The socket lock therefore
/// inherits exactly this once-tested behaviour; a duplicate socket-flavoured spawn-a-child test would only
/// re-exercise the same primitive.</para>
/// </summary>
public sealed class ModeLockInheritanceTests : IDisposable
{
    private readonly List<string> _dbs = [];
    private readonly List<Process> _children = [];

    [Fact]
    public void ExclusiveLock_NotInheritedByASpawnedChild_SoOwnershipReleasesForTheNextDaemon()
    {
        string db = NewDb();

        // A daemon-style exclusive owner holds the lock, then spawns a long-lived child while holding it — the
        // child inherits every fd that is not close-on-exec. The owner then releases (the using scope ends).
        Process child;
        using (ModeLock owner = ModeLock.AcquireExclusive(db))
        {
            child = SpawnLongLivedChild();
        }

        // Premise check: the inheritance window is only real while the child is alive. If it had already exited
        // this test would prove nothing, so make that a loud failure rather than a silent pass.
        Assert.False(child.HasExited, "the spawned child must still be alive for the inheritance test to be meaningful");

        // The owner has released, so the only thing that could still hold the lock is a descriptor the child
        // inherited. A fresh exclusive acquire must succeed — the child did not inherit it. This is exactly the
        // "next daemon starts cleanly after the previous owner exits" case the OS-release property promises.
        using ModeLock next = AcquireExclusiveToleratingTransientForkWindows(db);
        Assert.NotNull(next);
    }

    [Fact]
    public void SharedLock_NotInheritedByASpawnedChild_SoDirectOwnershipReleases()
    {
        string db = NewDb();

        // The direct-store (LocalStore) side of the same property: a shared holder spawns a child, then closes.
        Process child;
        using (ModeLock owner = ModeLock.AcquireShared(db))
        {
            child = SpawnLongLivedChild();
        }

        Assert.False(child.HasExited, "the spawned child must still be alive for the inheritance test to be meaningful");

        // With the shared holder gone and the child not retaining the fd, a daemon can now take exclusive
        // ownership. A lingering inherited shared lock would make this exclusive acquire conflict and throw.
        using ModeLock next = AcquireExclusiveToleratingTransientForkWindows(db);
        Assert.NotNull(next);
    }

    // Distinguishes "released and stays released" from a real inherited-fd leak. A genuine leak keeps this db's
    // lock held for the child's whole lifetime (sleep 60s), so it persists across every retry here and the
    // final throw still fails the test — the mutation guard is intact. A *transient* conflict needs no leak at
    // all: xUnit runs other suites in parallel, and any fork() in this shared test process momentarily
    // duplicates this held lock fd into the forking child until its exec closes it (O_CLOEXEC acts at exec, not
    // fork). That window is sub-millisecond and clears on its own, so a short bounded retry — far below the
    // child's 60s lifetime — passes through it without masking a real leak.
    private static ModeLock AcquireExclusiveToleratingTransientForkWindows(string db)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                return ModeLock.AcquireExclusive(db);
            }
            catch (TurnstileDatabaseInUseException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(20);
            }
        }
    }

    private Process SpawnLongLivedChild()
    {
        // A real, separate OS process that outlives the lock scope. Process.Start on Unix fork+execs and the
        // child inherits every descriptor that is not close-on-exec, so if the lock fd were inheritable it
        // would keep the flock's open file description alive here — precisely the leak O_CLOEXEC prevents.
        // `sleep` takes no input, needs no working files, and exists on both Linux and macOS.
        var psi = new ProcessStartInfo("sleep") { UseShellExecute = false };
        psi.ArgumentList.Add("60");
        Process p = Process.Start(psi)!;
        _children.Add(p);
        return p;
    }

    private string NewDb()
    {
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-inherit-{Guid.NewGuid():N}.db");
        _dbs.Add(db);
        return db;
    }

    public void Dispose()
    {
        foreach (Process p in _children)
        {
            try
            {
                if (!p.HasExited)
                {
                    p.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // The child may have exited between the check and the kill; nothing to clean up then.
            }

            p.Dispose();
        }

        SqliteConnection.ClearAllPools();
        foreach (string db in _dbs)
        {
            // ModeLock only ever touches the sidecar; no SQLite file is opened here.
            foreach (string path in new[] { db, db + "-modelock" })
            {
                if (File.Exists(path))
                {
                    try
                    {
                        File.Delete(path);
                    }
                    catch (IOException)
                    {
                        // Unique per run; a locked handle is harmless.
                    }
                }
            }
        }
    }
}
