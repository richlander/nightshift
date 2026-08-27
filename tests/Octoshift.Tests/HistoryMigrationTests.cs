namespace Octoshift.Tests;

using System.Text.Json;
using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// The schema-version and legacy-migration contract (#176). A history written by an earlier build of this
/// same tool — before the <c>attempted</c> and <c>initialized</c> members, and before the explicit
/// <c>version</c> — must be carried forward on upgrade rather than bricking the tool with a zero-row
/// report. These fixtures are <em>literal</em> older payloads, checked in as strings and never produced by
/// the current serializer, so a build whose reader and writer always agree cannot hide the skew the way
/// every prior test did.
/// </summary>
public sealed class HistoryMigrationTests
{
    private const string ValidHost = "{\"epoch\":\"1:1\",\"sweptAt\":\"2026-01-01T00:00:00+00:00\",\"continuous\":true}";
    private const string ValidPane = "{\"digest\":\"abc\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":4448,\"claimedAt\":\"2026-01-01T00:00:00+00:00\",\"witnessed\":true}";

    // The exact payload from the bug report: a local pane claiming #4448, written before `attempted` and
    // `initialized` existed, so the hosts map is empty and there is no version member. Under the old
    // strict loader this bricked with "the root is missing its 'attempted' member".
    private const string RealReportedPayload =
        "{\"panes\":{\"L|%615024358\":{\"digest\":\"\",\"since\":\"2026-08-26T21:42:00+00:00\",\"pr\":4448,\"claimedAt\":\"2026-08-26T21:42:00+00:00\",\"witnessed\":false}},\"hosts\":{}}";

    private static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);

    private static string TempPath(string tag) => Path.Combine(Path.GetTempPath(), $"octoshift-mig-{tag}-{Guid.NewGuid():N}.json");

    private static TmuxPane LocalPane(string paneId, int? pr = 4448)
        => new()
        {
            PaneId = paneId,
            Target = "cp:1",
            Host = null,
            WindowName = pr is { } p ? $"pr{p}" : "shell",
            SessionAttached = false,
            Activity = PaneActivity.Idle,
            Epoch = "1:1",
            AgentStateOption = pr is { } n ? $"pr={n} head=abc1234 reviews=2/2 rec=merge" : null,
        };

    public static IEnumerable<object[]> MigratableLegacyHistories()
    {
        string banffHost = $"\"hosts\":{{\"{TargetId.ForHost("banff").Key}\":{ValidHost}}}";
        string banffPane = $"\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%1")}\":{ValidPane}}}";
        string banffKey = TargetId.ForHost("banff").Key;

        // panes+hosts, the earliest shape, empty — a first run that collected nothing.
        yield return ["{\"panes\":{},\"hosts\":{}}"];

        // panes+hosts with the real reported payload: a local claim, an empty hosts map.
        yield return [RealReportedPayload];

        // panes+hosts+attempted (the intermediate shape) with a collected host, its attempted array empty:
        // migration derives the membership from the hosts map.
        yield return [$"{{\"panes\":{{}},{banffHost},\"attempted\":[]}}"];

        // panes+hosts+attempted with a pane on a host absent from the hosts map (and from attempted):
        // migration derives the membership from the pane's composite host, exactly the fix the empty-hosts
        // real payload needs.
        yield return [$"{{{banffPane},\"hosts\":{{}},\"attempted\":[]}}"];

        // panes+hosts+attempted, fully consistent.
        yield return [$"{{{banffPane},{banffHost},\"attempted\":[\"{banffKey}\"]}}"];

        // panes+hosts+attempted+initialized, the immediate pre-version shape, fully consistent.
        yield return [$"{{{banffPane},{banffHost},\"attempted\":[\"{banffKey}\"],\"initialized\":true}}"];

        // The same pre-version shape, established but emptied by retirement — initialized true, no members.
        yield return ["{\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];
    }
    [Theory]
    [MemberData(nameof(MigratableLegacyHistories))]
    public async Task OpenAsync_MigratesAKnownLegacyShapeRatherThanBricking(string content)
    {
        // A structurally-recognised unversioned file, with records a writer produced, loads cleanly: the
        // old strict loader would have failed every one of these closed. The memory is preserved, not
        // thrown away.
        string path = TempPath("open");
        File.WriteAllText(path, content);
        try
        {
            using PaneHistory history = await PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.True(history.IsInitialized || history.KnownHosts.Count == 0);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task OpenAsync_PreservesTheClaimFromTheRealReportedPayload()
    {
        // The whole point of migrating rather than bricking this shape: the local pane's claim on #4448,
        // its registration time, and local's fleet membership all survive, so ownership stays truthful
        // across the upgrade instead of resetting.
        string path = TempPath("real");
        File.WriteAllText(path, RealReportedPayload);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            TmuxPane pane = LocalPane("%615024358");
            using PaneHistory history = await PaneHistory.OpenAsync(path, ct);

            // Local membership is recovered from the pane's composite host, though the hosts map is empty.
            Assert.Contains(TargetId.Local.Key, history.KnownHosts);
            Assert.True(history.IsInitialized);
            Assert.Equal(new DateTimeOffset(2026, 8, 26, 21, 42, 0, TimeSpan.Zero), history.ClaimedAt(pane));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task Migration_ThenSave_WritesTheCurrentVersionAndReloadsStrictly()
    {
        // After migrating the real payload, the next commit writes a versioned file — the pane's host lives
        // in the derived attempted set rather than the (still-empty) hosts map — and that versioned file
        // reloads through the strict path without a second brick, closing the upgrade loop.
        string path = TempPath("save");
        File.WriteAllText(path, RealReportedPayload);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            TmuxPane pane = LocalPane("%615024358");
            using (PaneHistory migrated = await PaneHistory.OpenAsync(path, ct))
            {
                migrated.Save([pane], [null]);
            }

            string saved = File.ReadAllText(path);
            using (JsonDocument doc = JsonDocument.Parse(saved))
            {
                Assert.Equal(JsonValueKind.Number, doc.RootElement.GetProperty("version").ValueKind);
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            }

            using PaneHistory reopened = await PaneHistory.OpenAsync(path, ct);
            Assert.Contains(TargetId.Local.Key, reopened.KnownHosts);
            Assert.Equal(new DateTimeOffset(2026, 8, 26, 21, 42, 0, TimeSpan.Zero), reopened.ClaimedAt(pane));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task WaitingCollect_OverALegacyPayload_IsTruthfulNotBricked()
    {
        // The product command path: a bare sweep over the real legacy payload migrates on open, collects
        // the local machine, resolves rows, and commits a versioned file — a truthful report where the old
        // loader produced a silent zero-row "empty fleet".
        string path = TempPath("collect");
        File.WriteAllText(path, RealReportedPayload);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            TmuxPane pane = LocalPane("%615024358");
            WaitingCommand.FleetResult result = await WaitingCommand.CollectAndResolveAsync(
                [],
                (_, _) => Task.FromResult<IReadOnlyList<TmuxPane>>([pane]),
                None, None, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), ct, historyPath: path);

            Assert.False(result.EmptyFleet);
            Assert.NotNull(result.Rows);
            Assert.Contains(result.Rows!, r => r.Record?.PrNumber == 4448);

            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public async Task PrLocate_OverALegacyPayload_IsTruthfulNotBricked()
    {
        // The pr command path over the same migrated payload finds the claim rather than failing closed.
        string path = TempPath("pr");
        File.WriteAllText(path, RealReportedPayload);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            TmuxPane pane = LocalPane("%615024358");
            var collected = new WaitingCommand.Collection([pane], [], 1, [null], [null]);
            PrCommand.PrLocation location = await PrCommand.LocateAsync(
                4448, collected, history: null,
                (_, _) => Task.FromResult(PrFetch.Unavailable), None,
                new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), ct, historyPath: path);

            Assert.Equal(4448, location.PrNumber);
            Assert.NotEmpty(location.Claims);
            using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(1, doc.RootElement.GetProperty("version").GetInt32());
            }
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    public static IEnumerable<object[]> LegacyHistoriesWithUnusablePersistedAliases()
    {
        // A NUL alias, held in each place a legacy file can name a remote: a pane's composite host, the
        // hosts map, and the attempted array. A NUL cannot be carried through an OS process argument, so no
        // real invocation ever produced it — it is not upgrade skew — and it fails closed with the bytes
        // preserved exactly as an option-shaped or whitespace alias does, matching the versioned path.
        string nulKey = TargetId.ForHost("\0").Key;
        yield return [$"{{\"panes\":{{\"{TargetId.ForHost("\0").ComposeWith("%1")}\":{ValidPane}}},\"hosts\":{{}}}}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{\"{nulKey}\":{ValidHost}}},\"attempted\":[\"{nulKey}\"]}}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{nulKey}\"]}}"];

        // A DEL and a C1 control, the same story as a NUL.
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{TargetId.ForHost("\u007f").Key}\"]}}"];

        // An option-shaped and a whitespace alias: rejected by every past CLI too, so hand-editing.
        yield return [$"{{\"panes\":{{\"{TargetId.ForHost("-V").ComposeWith("%1")}\":{ValidPane}}},\"hosts\":{{}}}}"];
        yield return [$"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{TargetId.ForHost(" ").Key}\"]}}"];
    }

    [Theory]
    [MemberData(nameof(LegacyHistoriesWithUnusablePersistedAliases))]
    public async Task OpenAsync_FailsClosedOnALegacyShapeWithAnUnusablePersistedAlias(string content)
    {
        // #173, unconditional: an invalid persisted alias surfaces HistoryUnavailable and preserves the
        // original bytes, before any scanner is constructed — a NUL is not treated as valid writer-produced
        // upgrade skew to migrate-then-overwrite. The file is left for a human to inspect.
        string path = TempPath("aliaslegacy");
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
    public async Task WaitingCollect_OverALegacyNulAlias_IsUnavailableAndNeverConstructsTheScanner()
    {
        // The command path: a bare sweep over a legacy history carrying a NUL alias surfaces the unavailable
        // contract at open — before collection — so the injected scan is never even reached, no
        // ArgumentException or process-start exception escapes, and the bytes are left untouched.
        string nulKey = TargetId.ForHost("\0").Key;
        string content = $"{{\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{nulKey}\"]}}";
        string path = TempPath("aliascollect");
        File.WriteAllText(path, content);
        CancellationToken ct = TestContext.Current.CancellationToken;
        try
        {
            await Assert.ThrowsAsync<HistoryUnavailableException>(() => WaitingCommand.CollectAndResolveAsync(
                [],
                (_, _) => throw new Xunit.Sdk.XunitException("the scanner must never be constructed for an unusable persisted alias"),
                None, None, new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero), ct, historyPath: path));
            Assert.Equal(content, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    public static IEnumerable<object[]> AcceptedVersionedHistories()
    {
        string banffHost = $"\"hosts\":{{\"{TargetId.ForHost("banff").Key}\":{ValidHost}}}";
        string banffPane = $"\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%1")}\":{ValidPane}}}";
        string banffKey = TargetId.ForHost("banff").Key;

        // The empty established fleet, versioned.
        yield return ["{\"version\":1,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];

        // A fully consistent versioned file.
        yield return [$"{{\"version\":1,{banffPane},{banffHost},\"attempted\":[\"{banffKey}\"],\"initialized\":true}}"];

        // A pane on a host that is a known fleet member via the attempted set but not the hosts map — the
        // exact shape a migrated payload persists after a sweep that did not re-collect that host, which
        // must reload strictly rather than brick a second time.
        yield return [$"{{\"version\":1,{banffPane},\"hosts\":{{}},\"attempted\":[\"{banffKey}\"],\"initialized\":true}}"];
    }

    [Theory]
    [MemberData(nameof(AcceptedVersionedHistories))]
    public async Task OpenAsync_AcceptsAWellFormedVersionedHistory(string content)
    {
        string path = TempPath("vok");
        File.WriteAllText(path, content);
        try
        {
            using PaneHistory history = await PaneHistory.OpenAsync(path, TestContext.Current.CancellationToken);
            Assert.True(history.IsInitialized);
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    public static IEnumerable<object[]> RejectedVersionedHistories()
    {
        string banffHost = $"\"hosts\":{{\"{TargetId.ForHost("banff").Key}\":{ValidHost}}}";
        string banffPane = $"\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%1")}\":{ValidPane}}}";
        string banffKey = TargetId.ForHost("banff").Key;

        // A newer version this build cannot read, and an unknown lower one — never guessed at.
        yield return ["{\"version\":2,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];
        yield return ["{\"version\":0,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];

        // A malformed version: a string, or a non-integer number.
        yield return ["{\"version\":\"1\",\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];
        yield return ["{\"version\":1.5,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true}"];

        // A versioned file must carry every current member exactly — no missing member, no unknown one.
        yield return ["{\"version\":1,\"panes\":{},\"hosts\":{},\"attempted\":[]}"];
        yield return ["{\"version\":1,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":true,\"extra\":1}"];

        // initialized is never persisted false — a tampered false would re-enable the local bootstrap.
        yield return ["{\"version\":1,\"panes\":{},\"hosts\":{},\"attempted\":[],\"initialized\":false}"];

        // A pane on a host in neither the hosts map nor the attempted set: an invisible host a narrowed
        // sweep could not detect.
        yield return [$"{{\"version\":1,{banffPane},\"hosts\":{{}},\"attempted\":[],\"initialized\":true}}"];

        // An impossible record still fails closed under a version.
        yield return [$"{{\"version\":1,\"panes\":{{\"{TargetId.ForHost("banff").ComposeWith("%1")}\":{{\"digest\":\"a\",\"since\":\"2026-01-01T00:00:00+00:00\",\"pr\":null,\"claimedAt\":null,\"witnessed\":true}}}},{banffHost},\"attempted\":[\"{banffKey}\"],\"initialized\":true}}"];

        // A persisted alias that decodes to a NUL is unusable in a versioned file, exactly as in a legacy
        // one: a NUL cannot be carried through a process argument, so no writer produced it and it fails
        // closed with the bytes preserved.
        yield return [$"{{\"version\":1,\"panes\":{{}},\"hosts\":{{}},\"attempted\":[\"{TargetId.ForHost("\0").Key}\"],\"initialized\":true}}"];
    }

    [Theory]
    [MemberData(nameof(RejectedVersionedHistories))]
    public async Task OpenAsync_FailsClosedOnABadVersionedHistoryWithoutOverwritingIt(string content)
    {
        string path = TempPath("vbad");
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
}
