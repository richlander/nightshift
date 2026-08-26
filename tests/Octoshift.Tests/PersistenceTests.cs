namespace Octoshift.Tests;

using System.Text.Json;
using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// Persistence is load-bearing: a sweep whose memory does not reach disk has not narrowed the hosts it
/// failed to see, so a later run could read a stale witnessed ownership as current. A write failure is
/// therefore surfaced, not swallowed — both the <c>waiting</c> resolve path and the <c>pr</c> locate path
/// let it escape so their command layer can report the unavailable contract, and the JSON error document
/// they emit is valid.
/// </summary>
public sealed class PersistenceTests
{
    private static TmuxPane Pane(string? host)
        => new()
        {
            PaneId = "%1",
            WindowId = "@1",
            Target = "cp:1",
            Host = host,
            WindowName = "pr4448",
            SessionAttached = false,
            Activity = PaneActivity.Idle,
            Epoch = "1:1",
            AgentStateOption = "pr=4448 head=abc1234 reviews=2/2 rec=merge",
        };

    private static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);

    // A history whose file sits under a path that is a regular file, not a directory, so creating the
    // directory for the atomic write fails — a stand-in for any write denial.
    private static (PaneHistory History, string Blocker) UnwritableHistory()
    {
        string blocker = Path.Combine(Path.GetTempPath(), $"octoshift-blocker-{Guid.NewGuid():N}");
        File.WriteAllText(blocker, "not a directory");
        return (new PaneHistory(Path.Combine(blocker, "panes.json")), blocker);
    }

    [Fact]
    public async Task ResolveAllAsync_SurfacesAWriteDenialRatherThanReturningRows()
    {
        (PaneHistory history, string blocker) = UnwritableHistory();
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => WaitingCommand.ResolveAllAsync(
                [Pane(null)], None, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken,
                collectedHosts: [null], allHostsAnswered: true, history: history));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public async Task LocateAsync_SurfacesAWriteDenialRatherThanAnswering()
    {
        (PaneHistory history, string blocker) = UnwritableHistory();
        try
        {
            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history, None, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.Delete(blocker);
        }
    }

    [Fact]
    public void Save_LeavesThePriorHistoryIntactWhenTheWriteIsDenied()
    {
        // The write is atomic — a temp file then a rename — so a denied write cannot truncate the last
        // good history. Seed a valid file, deny the directory, and confirm the bytes are unchanged.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), $"octoshift-persist-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "panes.json");
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane w = Pane("fernie");
            var seed = new PaneHistory(path);
            seed.AdoptEpoch("fernie", "1:1", t);
            seed.Observe(w, t, claimedPr: 4448, registrationWitnessed: true);
            seed.Save([w], ["fernie"]);
            string before = File.ReadAllText(path);

            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                var history = new PaneHistory(path);
                history.Observe(w, t.AddMinutes(5), claimedPr: 4448, registrationWitnessed: true);
                Assert.Throws<HistoryUnavailableException>(() => history.Save([w], ["fernie"]));
            }
            finally
            {
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }

            Assert.Equal(before, File.ReadAllText(path));
        }
        finally
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void WriteJsonError_IsAValidJsonErrorDocument()
    {
        using var stream = new MemoryStream();
        WaitingCommand.WriteJsonError(stream, "could not persist pane history to /x/panes.json: denied");
        stream.Position = 0;

        using JsonDocument doc = JsonDocument.Parse(stream);
        Assert.Equal("could not persist pane history to /x/panes.json: denied", doc.RootElement.GetProperty("error").GetString());
        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("rows").ValueKind);
        Assert.Empty(doc.RootElement.GetProperty("rows").EnumerateArray());
    }

    [Theory]
    [InlineData("{ this is not valid json ]")]  // malformed
    [InlineData("null")]                          // a null root is an existing file, not a first run
    public async Task OpenAsync_FailsClosedOnAMalformedExistingHistoryWithoutOverwritingIt(string content)
    {
        // The load side is load-bearing too: an existing file that cannot be parsed is a history whose
        // known hosts and witnessed orders are unknown, not an empty one. Treating it as empty would let a
        // narrowed sweep read complete and then overwrite the evidence. So a product transaction fails
        // closed and leaves the bytes untouched.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-badload-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(
                () => PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task OpenAsync_FailsClosedOnAnUnreadableExistingHistory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string path = Path.Combine(Path.GetTempPath(), $"octoshift-unreadable-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{\"panes\":{},\"hosts\":{}}");
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(
                () => PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task OpenAsync_TreatsAMissingFileAsAnEmptyFirstRun()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-firstrun-{Guid.NewGuid():N}", "panes.json");
        try
        {
            using PaneHistory history = await PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.Empty(history.KnownHosts);
        }
        finally
        {
            try
            {
                Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void TheDirectConstructorLoadsAMalformedHistoryForgivinglyForUnitTests()
    {
        // The test-only constructor tolerates a malformed file (returns empty) so the sanitization unit
        // tests can seed corrupt files without each acquiring a lock; product code never uses it.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-forgiving-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json ]");
        try
        {
            Assert.Empty(new PaneHistory(path).KnownHosts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ResolveAllAsync_SurfacesAMalformedHistoryAsUnavailable()
    {
        // The waiting command path: a malformed history makes ResolveAllAsync fail closed with the same
        // exception RunAsync maps to the unavailable JSON/human contract, rather than owning a sole claim
        // off a forgotten fleet.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-waitbad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ bad ]");
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => WaitingCommand.ResolveAllAsync(
                [Pane(null)], None, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken,
                collectedHosts: [null], allHostsAnswered: true, history: null, historyPath: path));
            Assert.Equal("{ bad ]", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task LocateAsync_SurfacesAMalformedHistoryAsUnavailable()
    {
        // The pr command path, symmetrically.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prbad-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ bad ]");
        try
        {
            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history: null, None, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, historyPath: path));
            Assert.Equal("{ bad ]", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task OpenAsync_SerializesTheTransactionAcrossConcurrentOpens()
    {
        // Blocker 6: the whole transaction is serialized. A holds the lock and adds a host but has not
        // committed; B cannot even load until A commits; B then sees A's host and adds its own; the final
        // history retains both. Two OpenAsync in one process stand in for a concurrent waiting and pr,
        // since FileShare.None excludes even a second handle in the same process.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-lock-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            PaneHistory a = await PaneHistory.OpenAsync(path, ct);
            a.AdoptEpoch("hosta", "1:1", t);

            // B cannot acquire the lock while A holds it, so its OpenAsync does not complete.
            Task<PaneHistory> bOpen = PaneHistory.OpenAsync(path, ct);
            await Task.Delay(250, ct);
            Assert.False(bOpen.IsCompleted);

            // A commits — Save releases the lock — and only now can B load.
            a.Save([], ["hosta"]);
            PaneHistory b = await bOpen;
            Assert.Contains(TargetId.ForHost("hosta").Key, b.KnownHosts);
            b.AdoptEpoch("hostb", "2:1", t.AddMinutes(1));
            b.Save([], ["hosta", "hostb"]);
            a.Dispose();
            b.Dispose();

            var reloaded = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("hosta").Key, reloaded.KnownHosts);
            Assert.Contains(TargetId.ForHost("hostb").Key, reloaded.KnownHosts);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task OpenAsync_CancellationWhileWaitingForTheLockEscapesWithTheCallersToken()
    {
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-lockcancel-{Guid.NewGuid():N}.json");
        try
        {
            using PaneHistory holder = await PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken);
            using var cts = new CancellationTokenSource();
            Task<PaneHistory> blocked = PaneHistory.OpenAsync(path, cts.Token);
            await Task.Delay(100, TestContext.Current.CancellationToken);
            await cts.CancelAsync();

            OperationCanceledException oce = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
            Assert.Equal(cts.Token, oce.CancellationToken);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }
}
