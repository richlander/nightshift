namespace Turnstile.Tests;

using Turnstile.Storage;
using Xunit;

/// <summary>
/// Issue #202 hardening: <see cref="ModeLock"/> must <em>classify</em> lock failures, not flatten them. Only
/// EWOULDBLOCK/EAGAIN from <c>flock(LOCK_NB)</c> is an ownership conflict
/// (<see cref="TurnstileDatabaseInUseException"/>, proven at the process boundary in
/// <see cref="DatabaseOwnershipTests"/>). A genuine system failure — a sidecar that cannot be opened for a
/// reason other than "absent" — must surface as an <see cref="IOException"/> carrying its errno, never as a
/// success-shaped result and never dressed up as a conflict that would falsely claim another owner holds the
/// database. <c>EnsureOpen</c> likewise creates only on ENOENT, so a non-ENOENT open error is reported rather
/// than masked by a <c>creat()</c> attempt.
/// </summary>
public sealed class ModeLockErrorClassificationTests : IDisposable
{
    private readonly List<string> _sidecarDirs = [];

    [Fact]
    public void Acquire_ReportsASystemFailureAsIOExceptionWithErrno_NotAsAnOwnershipConflict()
    {
        string db = Path.Combine(Path.GetTempPath(), $"turnstile-errno-{Guid.NewGuid():N}.db");
        string sidecar = ModeLock.SidecarPath(db);

        // Turn the sidecar path itself into a directory. open(O_RDWR) on a directory fails with EISDIR — a real
        // error that is neither ENOENT (so EnsureOpen must not answer it by creat-ing a file) nor EWOULDBLOCK
        // (so Acquire must not report it as an ownership conflict). This is the exact misclassification the fix
        // removes: before it, every non-EINTR failure collapsed into TurnstileDatabaseInUseException.
        Directory.CreateDirectory(sidecar);
        _sidecarDirs.Add(sidecar);

        // Exact-match matters: xUnit's Assert.Throws<IOException> requires the type to be *exactly* IOException,
        // and TurnstileDatabaseInUseException does not derive from it — so this passing proves the failure took
        // the system-failure branch, not the conflict branch.
        IOException shared = Assert.Throws<IOException>(() => ModeLock.AcquireShared(db));
        Assert.Contains("errno", shared.Message);

        // The exclusive (daemon) acquire classifies identically — the same EnsureOpen/Acquire path serves both,
        // so a daemon start against a broken sidecar reports a real error rather than a phantom conflict.
        IOException exclusive = Assert.Throws<IOException>(() => ModeLock.AcquireExclusive(db));
        Assert.Contains("errno", exclusive.Message);
    }

    public void Dispose()
    {
        foreach (string dir in _sidecarDirs)
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
                // Unique per run; a leftover directory is harmless.
            }
        }
    }
}
