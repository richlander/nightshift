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

    // A window on the given host with the given claim ("pr=NNNN …") or no state at all, sharing one pane
    // id and server epoch so a later observation of the same window replaces the earlier one. The window
    // name defaults to a claiming one; pass a non-PR name for a window that identifies nothing.
    private static TmuxPane Window(string? host, string? agentState, string windowName = "pr4448")
        => new()
        {
            PaneId = "%1",
            WindowId = "@1",
            Target = "cp:1",
            Host = host,
            WindowName = windowName,
            SessionAttached = false,
            Activity = PaneActivity.Idle,
            Epoch = "1:1",
            AgentStateOption = agentState,
        };

    [Fact]
    public async Task CollectAndResolveAsync_BracketsCollectionSoAnOlderScanCannotCommitOverANewerOne()
    {
        // Blocker 1: the transaction is acquired BEFORE collection, so a slower older scan holds the lock
        // across its own collection and a newer sweep cannot even begin — cannot collect, cannot commit —
        // until the older one commits and releases. Run A opens first and blocks inside its scan (the lock
        // held); run B cannot start collecting; when A commits its now-older snapshot and releases, B
        // collects fresh and its newer observation wins. Under the old ordering (lock taken after
        // collection) B would collect concurrently and could commit first, then A's stale snapshot would
        // land last and resurrect the released claim — which the invocation-count assertion rules out.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-stale-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        var aCollecting = new TaskCompletionSource();
        var releaseA = new TaskCompletionSource();
        int bScans = 0;

        // A (older): a claim on the shared window. Its scan signals that A is collecting — holding the
        // lock — then blocks until the test releases it.
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanA = async (host, token) =>
        {
            aCollecting.TrySetResult();
            await releaseA.Task.WaitAsync(token);
            return [Window(host, "pr=4448 head=abc1234 reviews=2/2 rec=merge")];
        };

        // B (newer): the same window with its claim released. Records that it ran, which must not happen
        // until A has committed and freed the lock.
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scanB = (host, token) =>
        {
            Interlocked.Increment(ref bScans);
            return Task.FromResult<IReadOnlyList<TmuxPane>>([Window(host, null, windowName: "shell")]);
        };

        try
        {
            Task<WaitingCommand.FleetResult> aRun = WaitingCommand.CollectAndResolveAsync(
                ["shared"], scanA, None, None, t, ct, historyPath: path);

            await aCollecting.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);

            Task<WaitingCommand.FleetResult> bRun = WaitingCommand.CollectAndResolveAsync(
                ["shared"], scanB, None, None, t.AddMinutes(1), ct, historyPath: path);

            await Task.Delay(250, ct);
            Assert.False(bRun.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref bScans)); // B is blocked at OpenAsync, before any collection

            releaseA.TrySetResult();
            await aRun;
            await bRun;
            Assert.Equal(1, bScans); // B collected only after A committed and released

            // The final on-disk state is B's: the shared window released #4448, so no claim survives. Had
            // A's stale snapshot committed last, the window would still be registered to #4448.
            var reloaded = new PaneHistory(path);
            Assert.Null(reloaded.ClaimedAt(Window("shared", null)));
        }
        finally
        {
            releaseA.TrySetResult();
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CollectAndLocateAsync_AlsoTakesTheTransactionBeforeCollecting()
    {
        // The pr command core has the same ordering: its scan does not run until it holds the lock, so a
        // concurrent transaction blocks it at OpenAsync rather than letting it collect a snapshot it could
        // commit out of order.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prstale-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;
        int prScans = 0;
        try
        {
            // Hold the transaction open, then confirm the pr core cannot collect while it is held.
            PaneHistory holder = await PaneHistory.OpenAsync(path, ct);

            Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> scan = (host, token) =>
            {
                Interlocked.Increment(ref prScans);
                return Task.FromResult<IReadOnlyList<TmuxPane>>([Window(host, "pr=4448 head=abc1234")]);
            };

            Task<PrCommand.PrLocation> run = PrCommand.CollectAndLocateAsync(
                4448, ["shared"], scan, None, None, t, ct, historyPath: path);

            await Task.Delay(250, ct);
            Assert.False(run.IsCompleted);
            Assert.Equal(0, Volatile.Read(ref prScans)); // blocked at OpenAsync, before collecting

            holder.Save([], []); // commit and release
            holder.Dispose();

            await run;
            Assert.Equal(1, prScans);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

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

    private const string ValidHost = "{\"epoch\":\"1:1\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true}";
    private const string ValidPane = "{\"digest\":\"abc\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":4448,\"claimedAt\":\"2026-01-01T00:00:00+00:00\",\"witnessed\":true}";

    public static IEnumerable<object[]> StrictlyRejectedHistories()
    {
        string hostKey = TargetId.ForHost("banff").Key;
        string paneKey = TargetId.ForHost("banff").ComposeWith("%1");

        // The writer always emits both maps, so a file missing either was not written by this scheme.
        yield return ["{}"];
        yield return ["{\"panes\":{}}"];
        yield return ["{\"hosts\":{}}"];
        yield return ["{\"panes\":null,\"hosts\":null}"];

        // A null value where a record must be. Deserialization can produce these despite the annotations,
        // and they crash Save — so a strict load rejects them rather than dropping them.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":null}}}}"];
        yield return [$"{{\"panes\":{{\"{paneKey}\":null}},\"hosts\":{{}}}}"];

        // An invalid host key: `RA` is not canonical base64url, so it is not a target this scheme minted.
        // Dropping it (as the forgiving loader does) would forget the host it was meant to be.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"RA\":{ValidHost}}}}}"];

        // An impossible pane id in the composite key: `%01` has a leading zero, which tmux never emits.
        yield return [$"{{\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%01")}\":{ValidPane}}},\"hosts\":{{}}}}"];

        // Semantically impossible records this implementation could never have written.
        // A witness with no claim.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"witnessed\":true}}}},\"hosts\":{{}}}}"];
        // A claim number with no claim time.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":4448}}}},\"hosts\":{{}}}}"];
        // A default (unset) Since.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"0001-01-01T00:00:00+00:00\"}}}},\"hosts\":{{}}}}"];
        // A non-positive PR number.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":0,\"claimedAt\":\"2026-01-01T00:00:00+00:00\"}}}},\"hosts\":{{}}}}"];
        // A host claiming continuity with no sweep time.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"1:1\",\"continuous\":true}}}}}}"];
        // A host carrying a non-canonical epoch.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"0:0\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true}}}}}}"];
    }

    [Theory]
    [MemberData(nameof(StrictlyRejectedHistories))]
    public async Task OpenAsync_RejectsTheWholeFileForAnyRecordThisSchemeCouldNotHaveWritten(string content)
    {
        // A strict (product) load treats a single corrupt or impossible record as evidence the whole file
        // is untrustworthy — a corrupted entry for a known host, if silently dropped, would forget that
        // host and let a narrowed sweep read complete. So it rejects the file, leaves the bytes for a human
        // to inspect, and the transaction is unavailable.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-strict-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(
                () => PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken));
            Assert.Equal(content, File.ReadAllText(path));

            // The forgiving (test-only) loader keeps its lenient behaviour: it drops the bad entry rather
            // than rejecting the file, so the sanitisation unit tests still work.
            Assert.NotNull(new PaneHistory(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task AnAOnlySweepOverACorruptHistoryIsUnavailableAndLeavesTheFileUnchanged()
    {
        // The scenario the strict load exists for: host banff's entry is corrupted (an invalid key). If a
        // run collecting only host A dropped it and read on, banff would be forgotten, the view would read
        // as complete, and a sole claim would be owned — then the corrupt file overwritten with the empty
        // -derived snapshot. Instead both waiting and pr fail closed and leave the file exactly as it was.
        string content = $"{{\"panes\":{{}},\"hosts\":{{\"RA\":{ValidHost}}}}}";
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-aonly-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => WaitingCommand.ResolveAllAsync(
                [Pane(null)], None, None, DateTimeOffset.UtcNow, ct,
                collectedHosts: [null], allHostsAnswered: true, history: null, historyPath: path));
            Assert.Equal(content, File.ReadAllText(path));

            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history: null, None, None, DateTimeOffset.UtcNow, ct, historyPath: path));
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
