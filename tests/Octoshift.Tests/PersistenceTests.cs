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
            Target = "cp:1",
            Host = host,
            WindowName = "pr4448",
            SessionAttached = false,
            Activity = PaneActivity.Idle,
            Epoch = "1:1",
            AgentStateOption = "pr=4448 head=abc1234 reviews=2/2 rec=merge",
        };

    private static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);

    private static Task<PrFetch> NoneFetch(int _, CancellationToken __) => Task.FromResult(PrFetch.Unavailable);

    // A window on the given host with the given claim ("pr=NNNN …") or no state at all, sharing one pane
    // id and server epoch so a later observation of the same window replaces the earlier one. The window
    // name defaults to a claiming one; pass a non-PR name for a window that identifies nothing.
    private static TmuxPane Window(string? host, string? agentState, string windowName = "pr4448")
        => new()
        {
            PaneId = "%1",
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
                4448, ["shared"], scan, NoneFetch, None, t, ct, historyPath: path);

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

    [Fact]
    public async Task CollectAndResolveAsync_AFirstTimeFailedHostIsRememberedAndReAttemptedSoTheViewStaysHonest()
    {
        // Round 9, blocker 1 (waiting surface), under the round-10 declared-fleet model. A sweep attempts A
        // and a never-before-seen B; B fails before it ever collects, so the sweep is partial and A is not
        // owned. Because B is now persistent fleet membership, a LATER sweep — even one whose --host names
        // only A — re-attempts B (FleetTargets folds the declared fleet into the target set), so B cannot be
        // silently omitted and read as a complete view. While B is still down the later sweep is PARTIAL and
        // A's sole claim stays non-actionable; the round-9 laundering (forget B, then read A-alone as
        // complete) is impossible because B is never dropped by ordinary collection.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-firsttimefail-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        // Both sweeps: A claims 4448; B fails before it collects anything.
        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> sweep = (host, _) =>
            host == "hb"
                ? throw new TmuxUnavailableException("hb: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([Window("ha", "pr=4448 head=abc1234")]);

        try
        {
            WaitingCommand.FleetResult first = await WaitingCommand.CollectAndResolveAsync(
                ["ha", "hb"], sweep, None, None, t, ct, historyPath: path);
            Assert.True(first.Collected.AnyFailure);                       // B failed: sweep 1 is partial
            Assert.False(first.Rows!.Single().Claim.OwnsClaim);           // and A is not owned

            // B is on disk as attempted fleet membership even though it never collected — the persisted
            // distinction the fix introduces.
            var afterFirst = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("hb").Key, afterFirst.KnownHosts);
            Assert.Null(afterFirst.SweptAt("hb"));                         // no successful collection recorded

            // A --host run naming only A still re-attempts B, because the declared fleet drives collection.
            WaitingCommand.FleetResult second = await WaitingCommand.CollectAndResolveAsync(
                ["ha"], sweep, None, None, t.AddMinutes(10), ct, historyPath: path);

            // B is re-attempted and still down, so the second sweep is PARTIAL — not a complete view — and
            // A's sole claim is not actionable.
            Assert.True(second.Collected.AnyFailure);
            Assert.Contains(second.Collected.Unreachable, m => m.Contains("hb", StringComparison.Ordinal));
            WaitingRow aRow = Assert.Single(second.Rows!);
            Assert.False(aRow.Claim.OwnsClaim);
            Assert.False(aRow.MayAct);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CollectAndResolveAsync_RetiringAFailedHostMakesTheViewCompleteAndTheSoleClaimActionable()
    {
        // Round 10, blocker 1: retirement is the deliberate way a stale member leaves the fleet. B was
        // attempted once and failed (a decommissioned box), so every later sweep re-attempts it and reads
        // PARTIAL — completeness is unsatisfiable until an operator retires B. After `fleet retire`, B is no
        // longer a member, the A-only sweep is a complete view, and A's sole claim becomes actionable.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-retirecomplete-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> sweep = (host, _) =>
            host == "hb"
                ? throw new TmuxUnavailableException("hb: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([Window("ha", "pr=4448 head=abc1234")]);

        try
        {
            WaitingCommand.FleetResult first = await WaitingCommand.CollectAndResolveAsync(
                ["ha", "hb"], sweep, None, None, t, ct, historyPath: path);
            Assert.True(first.Collected.AnyFailure);
            Assert.False(first.Rows!.Single().Claim.OwnsClaim);

            // Retire B under the same transaction the sweeps use.
            int retire = await FleetCommand.RunRetireAsync(["hb"], local: false, json: false, ct, historyPath: path);
            Assert.Equal(ExitCode.Ok, retire);

            var afterRetire = new PaneHistory(path);
            Assert.DoesNotContain(TargetId.ForHost("hb").Key, afterRetire.KnownHosts);

            // The A-only sweep now covers the whole declared fleet, so the view is complete and A owns 4448.
            WaitingCommand.FleetResult second = await WaitingCommand.CollectAndResolveAsync(
                [], sweep, None, None, t.AddMinutes(10), ct, historyPath: path);
            Assert.False(second.Collected.AnyFailure);
            Assert.Empty(WaitingCommand.Omitted);
            WaitingRow aRow = Assert.Single(second.Rows!);
            Assert.True(aRow.Claim.OwnsClaim);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CollectAndLocateAsync_AFirstTimeFailedHostIsRememberedAndReAttemptedOnThePrView()
    {
        // Round 9, blocker 1 (pr surface), under the round-10 declared-fleet model. A+B attempted with B
        // failing on first sight; a later A-only lookup re-attempts B (the fleet drives collection), so the
        // view stays PARTIAL while B is down rather than reporting A as the sole claimant of a complete view.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-prfirsttimefail-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 3, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;

        Func<string?, CancellationToken, Task<IReadOnlyList<TmuxPane>>> sweep = (host, _) =>
            host == "hb"
                ? throw new TmuxUnavailableException("hb: no server running")
                : Task.FromResult<IReadOnlyList<TmuxPane>>([Window("ha", "pr=4448 head=abc1234")]);

        try
        {
            PrCommand.PrLocation first = await PrCommand.CollectAndLocateAsync(
                4448, ["ha", "hb"], sweep, NoneFetch, None, t, ct, historyPath: path);
            Assert.Equal(PrCommand.PrDisposition.Partial, first.Disposition);   // B unreachable

            var afterFirst = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("hb").Key, afterFirst.KnownHosts);

            PrCommand.PrLocation second = await PrCommand.CollectAndLocateAsync(
                4448, ["ha"], sweep, NoneFetch, None, t.AddMinutes(10), ct, historyPath: path);

            // B is re-attempted and still down, so the A-only lookup stays PARTIAL, its exit is unavailable,
            // and the located claim's order is left unconfirmed rather than observed.
            Assert.Equal(PrCommand.PrDisposition.Partial, second.Disposition);
            Assert.Equal(ExitCode.Unavailable, second.ExitCode);
            Assert.Equal(ClaimBasis.PartialView, Assert.Single(second.Claims).Claim.Basis);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void TransactionTime_ClampsASampleUpToTheGreatestPersistedTime()
    {
        // Blocker 2: the registration clock never runs backwards. A sample later than everything on disk is
        // used as-is; one earlier — a stepped-back wall clock, or a late-committing waiter that read the
        // clock while queued — is clamped up to the greatest persisted time, so a later transaction can
        // never stamp before an earlier committed one. Equal is allowed and is an inferred order.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-txntime-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var history = new PaneHistory(path);
            history.AdoptEpoch("a", "1:1", t);                              // SweptAt = t
            history.Observe(Pane("a"), t, claimedPr: 4448, registrationWitnessed: false); // ClaimedAt = Since = t
            history.Save([Pane("a")], ["a"]);

            var reloaded = new PaneHistory(path);
            Assert.Equal(t.AddMinutes(5), reloaded.TransactionTime(t.AddMinutes(5))); // later: used as-is
            Assert.Equal(t, reloaded.TransactionTime(t.AddMinutes(-5)));              // earlier: clamped up to t
            Assert.Equal(t, reloaded.TransactionTime(t));                            // equal: allowed
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task CollectAndResolveAsync_DoesNotStampALaterSweepBeforeAnEarlierOneUnderASteppedBackClock()
    {
        // Two serialized sweeps with the clock running backwards between them. The first registers W1 at
        // t2; the second, sampling an earlier t1, adds a new claimant W2 of the same PR. W2's registration
        // is clamped to t2, not t1, so it cannot sort ahead of W1 — the inversion the after-lock sample and
        // the monotonic clamp exist to prevent.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-monotonic-{Guid.NewGuid():N}.json");
        DateTimeOffset t2 = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset t1 = t2.AddHours(-1);
        CancellationToken ct = TestContext.Current.CancellationToken;

        TmuxPane W1 = Claiming("a", "%1", "win1");
        TmuxPane W2 = Claiming("a", "%2", "win2");
        try
        {
            await WaitingCommand.CollectAndResolveAsync(
                ["a"], (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([W1]),
                None, None, now: t2, ct, historyPath: path);
            Assert.Equal(t2, new PaneHistory(path).ClaimedAt(W1));

            // The clock has stepped back to t1; a second sweep sees W1 still there and W2 newly claiming.
            await WaitingCommand.CollectAndResolveAsync(
                ["a"], (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([W1, W2]),
                None, None, now: t1, ct, historyPath: path);

            var reloaded = new PaneHistory(path);
            Assert.Equal(t2, reloaded.ClaimedAt(W1));                 // unchanged: same claim continues
            Assert.Equal(t2, reloaded.ClaimedAt(W2));                 // clamped up to t2, never t1
            Assert.True(reloaded.ClaimedAt(W2) >= reloaded.ClaimedAt(W1));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task CollectAndResolveAsync_CancellationWhileWaitingForTheLockEscapesWithTheCallersToken()
    {
        // The after-lock sampling does not swallow a genuine caller cancellation: a core blocked acquiring
        // the transaction and then cancelled escapes carrying exactly the caller's token.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-corecancel-{Guid.NewGuid():N}.json");
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            using PaneHistory holder = await PaneHistory.OpenAsync(path, ct);
            using var cts = new CancellationTokenSource();
            Task<WaitingCommand.FleetResult> blocked = WaitingCommand.CollectAndResolveAsync(
                ["a"], (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([]),
                None, None, now: null, cts.Token, historyPath: path);
            await Task.Delay(100, ct);
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

    // A window that claims a PR through its published state, with a distinct name so a shared name is never
    // mistaken for an ambiguity.
    private static TmuxPane Claiming(string? host, string paneId, string windowName)
        => new()
        {
            PaneId = paneId,
            Target = "cp:1",
            Host = host,
            WindowName = windowName,
            SessionAttached = false,
            Activity = PaneActivity.Idle,
            Epoch = "1:1",
            AgentStateOption = "pr=4448 head=abc1234 reviews=2/2 rec=merge",
        };

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
            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null], [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history, NoneFetch, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken));
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
        string hosts = $"\"hosts\":{{\"{hostKey}\":{ValidHost}}}";
        string att = $"\"attempted\":[\"{hostKey}\"]";
        const string attEmpty = "\"attempted\":[]";

        // --- Unversioned member set: shapes no version of this scheme ever wrote. ---

        // A file whose member set is not one of the known legacy shapes (panes+hosts; +attempted;
        // +initialized) was not written by any version, so it fails closed rather than migrating. A null
        // map inside an otherwise-recognised shape is a structural reject on the same footing.
        yield return ["{}"];
        yield return ["{\"panes\":{}}"];
        yield return ["{\"hosts\":{}}"];
        yield return ["{\"panes\":null,\"hosts\":null}"];

        // An extra root member on a versioned file, or the wrong casing, which case-insensitive matching
        // would silently drop. The `version:2` file is a versioned document this build cannot read.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},{attEmpty},\"version\":2}}"];
        yield return ["{\"Panes\":{},\"Hosts\":{},\"Attempted\":[]}"];

        // A duplicate member at any level: root, a record, or a repeated dictionary key.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},{attEmpty},\"panes\":{{}}}}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"1:1\",\"epoch\":\"2:2\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true}}}},{att}}}"];
        yield return [$"{{\"panes\":{{\"{paneKey}\":{ValidPane},\"{paneKey}\":{ValidPane}}},{hosts},{att}}}"];

        // An unknown nested member, and a record missing a required one.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"1:1\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true,\"extra\":1}}}},{att}}}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"1:1\",\"continuous\":true}}}},{att}}}"];

        // A null value where a record must be — an object is required.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":null}},{att}}}"];
        yield return [$"{{\"panes\":{{\"{paneKey}\":null}},\"hosts\":{{}},{attEmpty}}}"];

        // --- Attempted set: shapes the persistent fleet membership never takes. ---

        // Not an array; a non-string element; a repeated key; a key this scheme never minted.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":{{}}}}"];
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[1]}"];
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[\"L\",\"L\"]}"];
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[\"RA\"]}"];

        // A host that answered but is absent from the attempted set is no longer rejected: it is a valid
        // legacy shape whose attempted membership migration derives from the hosts map, so it is asserted
        // to migrate in HistoryMigrationTests rather than fail closed here.

        // --- Semantic: full, well-formed records this implementation could never have written. ---

        // An invalid host key: `RA` is not canonical base64url, so it is not a target this scheme minted.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"RA\":{ValidHost}}},{attEmpty}}}"];

        // An impossible pane id in the composite key: `%01` has a leading zero, which tmux never emits.
        yield return [$"{{\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%01")}\":{ValidPane}}},\"hosts\":{{}},{attEmpty}}}"];

        // A witness with no claim.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":null,\"claimedAt\":null,\"witnessed\":true}}}},\"hosts\":{{}},{attEmpty}}}"];
        // A claim number with no claim time.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":4448,\"claimedAt\":null,\"witnessed\":false}}}},\"hosts\":{{}},{attEmpty}}}"];
        // A default (unset) Since.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"0001-01-01T00:00:00+00:00\",\"pr\":null,\"claimedAt\":null,\"witnessed\":false}}}},\"hosts\":{{}},{attEmpty}}}"];
        // A non-positive PR number.
        yield return [$"{{\"panes\":{{\"{paneKey}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":0,\"claimedAt\":\"2026-01-01T00:00:00+00:00\",\"witnessed\":false}}}},\"hosts\":{{}},{attEmpty}}}"];
        // A host claiming continuity with no sweep time.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"1:1\",\"sweptAt\":null,\"continuous\":true}}}},{att}}}"];
        // A host carrying a non-canonical epoch.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{{\"epoch\":\"0:0\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true}}}},{att}}}"];

        // --- Round 11: the initialized flag. The writer only ever persists it true (a first run is the
        //     absent-file case, never written), so a persisted false or absent flag is a shape it never
        //     produced — and tampering it to false must not re-enable the local bootstrap on an emptied
        //     fleet. These fixtures are otherwise valid, so the flag is the tested defect. ---
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":false}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{hostKey}\":{ValidHost}}},\"attempted\":[\"{hostKey}\"],\"initialized\":false}}"];
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":null}"];

        // --- Round 11: a persisted remote alias that decodes to something the collector could never use.
        //     A canonical base64url key round-trips (IsValidKey passes) yet decodes to an option-shaped or
        //     whitespace alias a strict load must reject as HistoryUnavailable, so automatic collection
        //     never builds an ssh argument that throws ArgumentException. HostTarget.Validate is the same
        //     gate the CLI applies. ---
        string dashVKey = TargetId.ForHost("-V").Key;
        string whitespaceKey = TargetId.ForHost(" ").Key;
        // Option-shaped, as a collected host (valid record, unusable alias).
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{dashVKey}\":{ValidHost}}},\"attempted\":[\"{dashVKey}\"],\"initialized\":true}}"];
        // Option-shaped, as an attempted-only member.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{dashVKey}\"],\"initialized\":true}}"];
        // Whitespace alias as an attempted-only member.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{whitespaceKey}\"],\"initialized\":true}}"];
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
    public async Task OpenAsync_AcceptsAHistoryTheWriterActuallyProduced()
    {
        // The strict schema does not over-reject: a file the writer itself produced — both maps, exact
        // members, a real registration and a swept host — loads cleanly and its contents survive the round
        // trip, so the tightened validation cannot lock the tool out of its own history.
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-roundtrip-{Guid.NewGuid():N}.json");
        DateTimeOffset t = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            // Write a genuine history through the product Save path, with a claim and an empty host too.
            using (PaneHistory seed = await PaneHistory.OpenAsync(path, ct))
            {
                seed.AdoptEpoch("fernie", "1:1", t);
                seed.RecordSweptEmpty("banff", t);
                seed.Observe(Pane("fernie"), t, claimedPr: 4448, registrationWitnessed: true);
                seed.Save([Pane("fernie")], ["fernie", "banff"]);
            }

            using PaneHistory reopened = await PaneHistory.OpenAsync(path, ct);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("banff").Key, reopened.KnownHosts);
            Assert.Equal(t, reopened.ClaimedAt(Pane("fernie")));
            Assert.True(reopened.IsWitnessed(Pane("fernie")));
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

            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null], [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history: null, NoneFetch, None, DateTimeOffset.UtcNow, ct, historyPath: path));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task AnOptionShapedPersistedTargetIsUnavailableNotAnArgumentExceptionMidSweep()
    {
        // Finding round 11 / #4: the key for `-V` is canonical base64url — IsValidKey accepts it — but it
        // decodes to an ssh option the collector would throw ArgumentException on mid-sweep, outside the
        // PARTIAL/history-unavailable contract. Strict load rejects it as HistoryUnavailable with the bytes
        // preserved, and the collecting entry point surfaces exactly that, never an ArgumentException.
        string key = TargetId.ForHost("-V").Key;
        string content = $"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{key}\"],\"initialized\":true}}";
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-optiontarget-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PaneHistory.OpenAsync(path, ct));
            Assert.Equal(content, File.ReadAllText(path));

            // The waiting collect path opens the history strictly first, so it surfaces the same unavailable
            // contract before any scanner is constructed — the injected scan is never even reached.
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => WaitingCommand.CollectAndResolveAsync(
                [],
                (_, _) => throw new Xunit.Sdk.XunitException("the scanner must never be constructed for an unusable persisted target"),
                None, None, DateTimeOffset.UtcNow, ct, historyPath: path));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void FleetTargets_DropsAForgivinglyLoadedUnusableRemoteRatherThanCollectingIt()
    {
        // Defense in depth: a strict product load rejects an option-shaped persisted alias, but a forgiving
        // or hand-seeded history might still hold one. FleetTargets must not hand it to the collector — the
        // unusable member is dropped so no ssh argument is ever built from `-V`, while usable members stay.
        string dashV = TargetId.ForHost("-V").Key;
        string fernie = TargetId.ForHost("fernie").Key;
        string content = $"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{dashV}\",\"{fernie}\"],\"initialized\":true}}";
        string path = Path.Combine(Path.GetTempPath(), $"octoshift-ftdrop-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, content);
        try
        {
            var history = new PaneHistory(path);
            IReadOnlyList<string?> targets = history.FleetTargets([]);
            Assert.DoesNotContain("-V", targets);
            Assert.Contains("fernie", targets);
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
            var collected = new WaitingCommand.Collection([Pane(null)], [], 1, [null], [null]);
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => PrCommand.LocateAsync(
                4448, collected, history: null, NoneFetch, None, DateTimeOffset.UtcNow, TestContext.Current.CancellationToken, historyPath: path));
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
