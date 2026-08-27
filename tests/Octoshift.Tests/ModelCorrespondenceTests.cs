namespace Octoshift.Tests;

using Octoshift.Commands;
using Octoshift.GitHub;
using Octoshift.Waiting;
using Xunit;

/// <summary>
/// Each test here corresponds to one operator or property in <c>docs/model/Waiting.tla</c>, checked
/// against the real implementation rather than the model.
/// </summary>
/// <remarks>
/// A model that has been checked exhaustively proves things about the model. It says nothing about the
/// code unless the correspondence is demonstrated, and an unchecked correspondence is how a specification
/// ends up describing a system nobody built. These are named for the TLA+ definitions they mirror so a
/// change to either side has an obvious counterpart to update.
///
/// The model is the authority on ordering and memory; these tests are the evidence that the C# agrees.
/// </remarks>
public class ModelCorrespondenceTests
{
    private static TmuxPane Window(string paneId, string? host = "fernie", string epoch = "100:1")
        => new()
        {
            PaneId = paneId,
            Target = $"cp:{paneId.TrimStart('%')}",
            Host = host,
            WindowName = "w",
            SessionAttached = true,
            Epoch = epoch,
        };

    private static string TempPath() => Path.Combine(Path.GetTempPath(), $"octoshift-model-{Guid.NewGuid():N}.json");

    /// <summary>
    /// TLA+ every action advances the logical clock (<c>now' = now + 1</c>), so a registration recorded by
    /// a later transaction never carries an earlier time than one already recorded — which is what keeps a
    /// witnessed order from inverting. The code realises that monotone clock with
    /// <see cref="PaneHistory.TransactionTime"/>, sampled inside the held transaction and clamped up to the
    /// greatest time already on disk: a sample from a stepped-back clock, or from a late-committing waiter,
    /// is raised to the persisted floor rather than allowed to run backwards.
    /// </summary>
    [Fact]
    public void TheTransactionClockNeverRunsBackwards()
    {
        string path = TempPath();
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        try
        {
            var history = new PaneHistory(path);
            history.AdoptEpoch("a", "1:1", t);
            history.Observe(Window("%1", host: "a", epoch: "1:1"), t, claimedPr: 4448, registrationWitnessed: false);
            history.Save([Window("%1", host: "a", epoch: "1:1")], ["a"]);

            var reloaded = new PaneHistory(path);
            Assert.True(reloaded.TransactionTime(t.AddMinutes(-30)) >= t);
            Assert.Equal(t.AddMinutes(10), reloaded.TransactionTime(t.AddMinutes(10)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>SoleClaimantIsAlwaysOwner</c>: a window that is the only claimant of its PR is always
    /// actionable. The anti-degenerate property — every other rule is satisfied by a tool that never
    /// acts, so one of them must require that it does.
    /// </summary>
    [Fact]
    public void SoleClaimantIsAlwaysOwner()
    {
        TmuxPane only = Window("%1");

        Assert.True(Claim.Register([(only, 4448, null)], _ => null, _ => false)[Claim.Key(only)].OwnsClaim);
    }

    /// <summary>
    /// TLA+ <c>AtMostOneOwner</c>: two windows can never both own one PR, whatever the evidence.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AtMostOneOwner(bool witnessed)
    {
        TmuxPane a = Window("%1");
        TmuxPane b = Window("%2");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(a, 4448, null), (b, 4448, null)],
            p => witnessed ? (p.PaneId == a.PaneId ? t : t.AddMinutes(5)) : null,
            _ => witnessed);

        Assert.True(ranked.Values.Count(c => c.OwnsClaim) <= 1);
    }

    /// <summary>
    /// TLA+ <c>NeverActOnUnwitnessedOrder</c>: when two or more claimants were never seen registering,
    /// nobody owns the PR. Stated over the underlying facts, because the obvious phrasing —
    /// <c>OwnsClaim ⇒ Observed</c> — is a tautology that passes with the guard deleted.
    /// </summary>
    [Fact]
    public void NeverActOnUnwitnessedOrder()
    {
        TmuxPane a = Window("%1");
        TmuxPane b = Window("%2");

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(a, 4448, 3), (b, 4448, 15)],
            _ => null,
            _ => false);

        Assert.All(ranked.Values, c => Assert.False(c.OwnsClaim));
    }

    /// <summary>
    /// TLA+ <c>NoCrossEpochMemory</c>: a registration recorded under a previous tmux server never counts.
    /// This is the property the epoch mechanism exists for — pane ids restart at <c>%0</c>, so without it
    /// a new window inherits a departed one's place in the queue and the result is labelled observed.
    /// </summary>
    [Fact]
    public void NoCrossEpochMemory()
    {
        string path = TempPath();
        try
        {
            TmuxPane before = Window("%1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.Observe(before, t, claimedPr: 4448);
            first.Save([before]);
            Assert.NotNull(new PaneHistory(path).ClaimedAt(before));

            // Same pane id, different server.
            var after = new PaneHistory(path);
            after.AdoptEpoch("fernie", "200:2", t.AddHours(1));

            Assert.Null(after.ClaimedAt(before));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>ServerRestarts</c> leaves <c>regWitnessed</c> unchanged: the tool cannot alter persisted
    /// provenance for a host it did not collect, so a witness survives a restart on disk until the next
    /// sweep reaches the host and <c>AdoptEpoch</c> sees the epoch mismatch and invalidates it. Clearing
    /// it eagerly would be a phantom change to an uncollected host, which <c>NoPhantomDepartureStep</c>
    /// forbids — and it is unnecessary, because a cross-epoch registration confers nothing anyway.
    /// </summary>
    [Fact]
    public void ARestartDoesNotClearWitnessUntilTheHostIsCollectedAgain()
    {
        string path = TempPath();
        try
        {
            TmuxPane w = Window("%1", host: "fernie", epoch: "100:1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.Observe(w, t, claimedPr: 4448, registrationWitnessed: true);
            first.Save([w], ["fernie"]);
            Assert.True(new PaneHistory(path).IsWitnessed(w));

            // A server restart happens, but this run does not collect fernie: its witness is untouched on
            // disk, exactly as the model leaves regWitnessed alone on ServerRestarts.
            var reloaded = new PaneHistory(path);
            Assert.True(reloaded.IsWitnessed(w));

            // Only when fernie is collected under the new epoch does AdoptEpoch invalidate the stale
            // registration — the next collecting Sweep, not the restart itself.
            reloaded.AdoptEpoch("fernie", "999:9", t.AddHours(1));
            Assert.False(reloaded.IsWitnessed(w));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>RegistrationStableStep</c>: a window's registration is unchanged while it keeps claiming
    /// the same PR, and is renewed when it switches. TLC refuted the first phrasing of this property
    /// because it missed the switching case.
    /// </summary>
    [Fact]
    public void RegistrationStableStep()
    {
        string path = TempPath();
        try
        {
            TmuxPane w = Window("%1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var history = new PaneHistory(path);
            history.AdoptEpoch("fernie", "100:1", t);
            history.Observe(w, t, claimedPr: 4448);
            DateTimeOffset? first = history.ClaimedAt(w);

            // Same claim, later sweep: the place in the queue is kept.
            history.Observe(w with { Capture = "moved on" }, t.AddHours(1), claimedPr: 4448);
            Assert.Equal(first, history.ClaimedAt(w));

            // Switching PRs is a fresh registration, and goes to the back.
            history.Observe(w, t.AddHours(2), claimedPr: 4600);
            Assert.Equal(t.AddHours(2), history.ClaimedAt(w));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>RegWitnessedStableStep</c>: a registration's witness is as stable as its time. While one
    /// claim continues under one server, a later sweep that finally sees the whole fleet cannot flip a
    /// first look's witness from false to true — the temporal half of the fleet-expansion fix. Only a
    /// genuine re-registration takes a fresh witness.
    /// </summary>
    [Fact]
    public void RegWitnessedStableStep()
    {
        string path = TempPath();
        try
        {
            TmuxPane w = Window("%1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var history = new PaneHistory(path);
            history.AdoptEpoch("fernie", "100:1", t);

            // First look: recorded unwitnessed because a narrow view could not corroborate it.
            history.Observe(w, t, claimedPr: 4448, registrationWitnessed: false);
            Assert.False(history.IsWitnessed(w));

            // Later sweeps of the same claim offer witness = true (the fleet is now complete), but the
            // persisted first look does not move: the same registration keeps its trust across every
            // subsequent sweep, which is what stops fleet expansion from laundering it.
            history.Observe(w, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: true);
            Assert.False(history.IsWitnessed(w));
            history.Observe(w, t.AddMinutes(20), claimedPr: 4448, registrationWitnessed: true);
            Assert.False(history.IsWitnessed(w));

            // Only a genuine re-registration — switching PRs — takes a fresh, witnessed provenance.
            history.Observe(w, t.AddMinutes(30), claimedPr: 4600, registrationWitnessed: true);
            Assert.True(history.IsWitnessed(w));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>OwnerStableAcrossSweepStep</c>: sweeping an unchanged fleet does not change who owns what.
    /// An owner whose identity flips between runs is worse than no owner at all.
    /// </summary>
    [Fact]
    public void OwnerStableAcrossSweepStep()
    {
        string path = TempPath();
        try
        {
            TmuxPane a = Window("%1");
            TmuxPane b = Window("%2");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var history = new PaneHistory(path);
            history.AdoptEpoch("fernie", "100:1", t);
            history.Observe(a, t, claimedPr: 4448);

            string? owner = null;
            for (int sweep = 1; sweep <= 4; sweep++)
            {
                DateTimeOffset at = t.AddMinutes(sweep * 10);
                history.Observe(a, at, claimedPr: 4448);
                history.Observe(b, at, claimedPr: 4448);

                IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
                    [(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.IsWitnessed);

                string current = ranked.Single(e => e.Value.Rank == ClaimRank.Owner).Key;
                owner ??= current;
                Assert.Equal(owner, current);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>ViewCompleteOrNoOwner</c>: while any host is unseen, no claim is owned.
    /// </summary>
    /// <remarks>
    /// Measured against the live fleet: PR 4448 was claimed by a window on merritt and one on fernie,
    /// and the merritt window was the follower. Collected with fernie unreachable, that follower is the
    /// only claimant visible — and a sole claimant is the one shape that is always actionable. A partial
    /// view is therefore exactly the condition under which the tool would drive the wrong agent, which
    /// is why an incomplete sweep owns nothing rather than owning what it can see.
    /// </remarks>
    [Fact]
    public void ViewCompleteOrNoOwner()
    {
        TmuxPane visible = Window("%9", host: "merritt");

        Assert.True(Claim.Register([(visible, 4448, null)], _ => null, _ => false, viewComplete: true)
            [Claim.Key(visible)].OwnsClaim);

        Claim partial = Claim.Register([(visible, 4448, null)], _ => null, _ => false, viewComplete: false)[Claim.Key(visible)];
        Assert.Equal(ClaimBasis.PartialView, partial.Basis);
        Assert.False(partial.OwnsClaim);
    }

    /// <summary>
    /// TLA+ <c>ViewCompleteOrNoOwner</c>, second half: a host nobody asked about is as absent as a host
    /// that failed.
    /// </summary>
    /// <remarks>
    /// A run cannot tell from its own arguments that it was given fewer hosts than the fleet has — a
    /// host not named is indistinguishable from a host that does not exist. It can only know by
    /// remembering which hosts it has collected before. Without this, narrowing to one host produces no
    /// unreachable entries, the view reads as complete, and a window that is a follower on the full
    /// fleet becomes the sole claimant and is actionable.
    /// </remarks>
    [Fact]
    public void ViewIsNarrowerThanTheFleetItHasSeen()
    {
        string path = TempPath();
        try
        {
            TmuxPane onFernie = Window("%1", host: "fernie");
            TmuxPane onMerritt = Window("%9", host: "merritt");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.AdoptEpoch("merritt", "200:1", t);
            first.Save([onFernie, onMerritt], ["fernie", "merritt"]);

            // A later run that collects only merritt is looking at less than it has already seen.
            var narrowed = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("fernie").Key, narrowed.KnownHosts);
            Assert.Contains(TargetId.ForHost("merritt").Key, narrowed.KnownHosts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Sweep</c>, the round-9 clause: <c>knownHosts' = knownHosts ∪ attempted</c> (was
    /// <c>∪ collected</c>), the property <c>CompletenessCoversEveryAttemptedHost</c> refutes reverting.
    /// Persistent fleet membership grows with the hosts a sweep ATTEMPTED, not only those that answered, so
    /// a target that fails on its very first attempt — no epoch, no continuity, never in the hosts map — is
    /// still remembered. The code keeps this attempted set apart from successful collection behind
    /// <see cref="PaneHistory.KnownHosts"/>, so a later sweep that omits the failed target reads as narrowed
    /// rather than complete.
    /// </summary>
    [Fact]
    public void PersistentFleetMembershipGrowsWithAttemptedNotCollected()
    {
        string path = TempPath();
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane onFernie = Window("%1", host: "fernie");

            // A sweep attempts fernie and banff; only fernie answers, banff fails before it ever collects.
            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.Save([onFernie], hosts: ["fernie"], attempted: ["fernie", "banff"]);

            // banff is remembered as fleet membership even though it never collected — it has no successful
            // sweep time and no epoch, only membership. fernie, which answered, carries its collection.
            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.ForHost("banff").Key, reopened.KnownHosts);
            Assert.Null(reopened.SweptAt("banff"));
            Assert.NotNull(reopened.SweptAt("fernie"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Retire</c> and the <c>NoOwnerFromRetiredHost</c> invariant. Membership shrinks only through
    /// the explicit operator act — never ordinary collection — and retiring a host removes it from the
    /// declared fleet and clears the registration state kept under it, so no ownership can be derived from a
    /// retired host's stale claim. The code realises <c>Retire</c> as <see cref="PaneHistory.Retire"/>: it
    /// drops the target from <see cref="PaneHistory.KnownHosts"/> and prunes its pane entries (its claim and
    /// witness), while leaving every other host's registration intact. An unknown target is reported rather
    /// than silently retired, which is what keeps the operator act unambiguous.
    /// </summary>
    [Fact]
    public void RetiringAHostRemovesItAndClearsItsClaims()
    {
        string path = TempPath();
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            TmuxPane onFernie = Window("%1", host: "fernie");
            TmuxPane onMerritt = Window("%9", host: "merritt");

            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.AdoptEpoch("merritt", "200:1", t);
            first.Observe(onFernie, t, claimedPr: 4448, registrationWitnessed: true);
            first.Observe(onMerritt, t, claimedPr: 4449, registrationWitnessed: true);
            first.Save([onFernie, onMerritt], ["fernie", "merritt"]);

            // Retire fernie: it must be a known member (so an unknown target is a non-success), it leaves the
            // fleet, its claim and witness are gone, and merritt is untouched.
            var retiring = new PaneHistory(path);
            Assert.True(retiring.IsFleetMember("fernie"));
            Assert.False(retiring.IsFleetMember("banff"));
            Assert.True(retiring.Retire("fernie"));           // known member: retired
            Assert.False(retiring.Retire("banff"));           // unknown: reported, not a silent success
            retiring.Persist();

            var reopened = new PaneHistory(path);
            Assert.DoesNotContain(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
            Assert.Null(reopened.ClaimedAt(onFernie));        // the retired host's registration is gone
            Assert.False(reopened.IsWitnessed(onFernie));
            Assert.Contains(TargetId.ForHost("merritt").Key, reopened.KnownHosts);
            Assert.NotNull(reopened.ClaimedAt(onMerritt));    // an untouched host keeps its registration
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Add</c>: membership grows deliberately through the operator act, the counterpart to
    /// <c>Retire</c>, and the only way to re-declare the local machine once it has been retired — because a
    /// bare sweep bootstraps local only while the fleet is genuinely uninitialized, never again after it has
    /// been emptied on purpose. The code realises <c>Add</c> as <see cref="PaneHistory.Add"/>: it puts the
    /// target into the persistent membership (exactly where a first attempt would) and marks the fleet
    /// initialized, so the added host enters <see cref="PaneHistory.KnownHosts"/> and a subsequent bare
    /// sweep reaches it, while an emptied fleet that was <em>not</em> added back stays empty rather than
    /// re-bootstrapping local. Mirrors the model's `initialized`/explicitly-empty distinction and the
    /// `OwnerStableAcrossSweepStep` re-stamp reasoning at the membership boundary.
    /// </summary>
    [Fact]
    public void AddingAHostDeclaresItAndReDeclaresLocalAfterRetirement()
    {
        string path = TempPath();
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

            // Establish a fleet of local plus fernie, then retire the sole local member: the fleet is now
            // empty of local on purpose, and a bare sweep must NOT re-add it.
            var first = new PaneHistory(path);
            first.AdoptEpoch("fernie", "100:1", t);
            first.Observe(Window("%1", host: "fernie"), t, claimedPr: 4448, registrationWitnessed: true);
            first.Save([Window("%1", host: "fernie")], hosts: [null, "fernie"], attempted: [null, "fernie"]);

            var retiring = new PaneHistory(path);
            Assert.True(retiring.Retire(null));               // local was a member
            retiring.Persist();

            var emptiedOfLocal = new PaneHistory(path);
            Assert.True(emptiedOfLocal.IsInitialized);
            Assert.DoesNotContain(TargetId.Local.Key, emptiedOfLocal.KnownHosts);
            // A bare sweep reaches fernie but NOT local — retirement is not undone.
            Assert.DoesNotContain(null, emptiedOfLocal.FleetTargets([]));

            // Add local back: an operator act, the only way to re-declare it.
            var adding = new PaneHistory(path);
            Assert.True(adding.Add(null));                    // newly declared
            Assert.False(adding.Add(null));                   // idempotent: already a member
            adding.Persist();

            var reopened = new PaneHistory(path);
            Assert.Contains(TargetId.Local.Key, reopened.KnownHosts);
            Assert.Contains(TargetId.ForHost("fernie").Key, reopened.KnownHosts);
            Assert.Contains(null, reopened.FleetTargets([])); // a bare sweep reaches local again
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Sweep</c> completeness, the round-8 clause: <c>viewComplete' = (attempted ⊆ collected) ∧
    /// (knownHosts ⊆ collected)</c>. A target attempted for the very first time — never in KnownHosts —
    /// that fails leaves the second conjunct holding vacuously (<c>{} ⊆ collected</c>), so deriving
    /// completeness from known coverage alone would read the view complete and own a sole claim. The
    /// first conjunct is what production carries as <c>allHostsAnswered</c>; it is false for that failed
    /// attempt, so the lone visible claimant is not owned.
    /// </summary>
    /// <remarks>
    /// The history is fresh, so KnownHosts is empty and nothing can be omitted — the failure is a target
    /// that did not answer, not a host left out. That is the case the earlier model missed: it is
    /// invisible to the known-coverage half, and only the attempted-answered half rules it out. The
    /// counterpart with the same fleet answering in full is complete, and the sole claim is owned — so
    /// the difference is exactly <c>allHostsAnswered</c>.
    /// </remarks>
    [Fact]
    public async Task ViewIsIncompleteWhenACurrentTargetFailsBeforeItIsEverKnown()
    {
        static Task<PrFacts?> None(int _, CancellationToken __) => Task.FromResult<PrFacts?>(null);
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
        TmuxPane sole = Window("%1", host: "merritt") with { AgentStateOption = "pr=4448 head=abc1234" };

        // A first-time target fails: with KnownHosts empty nothing is omitted, so only the
        // attempted-answered half of completeness can make the view incomplete.
        string failedPath = TempPath();
        try
        {
            var history = new PaneHistory(failedPath);
            Assert.Empty(history.KnownHosts);

            IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
                [sole], None, None, t, all: true, TestContext.Current.CancellationToken,
                collectedHosts: ["merritt"], allHostsAnswered: false, history: history);

            Claim claim = Assert.Single(rows).Claim;
            Assert.Equal(ClaimBasis.PartialView, claim.Basis);
            Assert.False(claim.OwnsClaim);
        }
        finally
        {
            File.Delete(failedPath);
        }

        // The identical fleet with every attempted target answering IS complete, so the sole claim is owned.
        string answeredPath = TempPath();
        try
        {
            var history = new PaneHistory(answeredPath);

            IReadOnlyList<WaitingRow> rows = await WaitingCommand.BuildRowsAsync(
                [sole], None, None, t, all: true, TestContext.Current.CancellationToken,
                collectedHosts: ["merritt"], allHostsAnswered: true, history: history);

            Assert.True(Assert.Single(rows).Claim.OwnsClaim);
        }
        finally
        {
            File.Delete(answeredPath);
        }
    }

    /// <summary>
    /// TLA+ <c>Observed</c>: the order is a fact only when every claim was recorded, witnessed, and the
    /// times are distinct. The scenario the model exists to get right — one agent working, a second
    /// joining later, with the tool watching both hosts throughout, so both registrations are witnessed.
    /// </summary>
    [Fact]
    public void ObservedRequiresAWitnessedRegistration()
    {
        TmuxPane first = Window("%1");
        TmuxPane joined = Window("%2");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(joined, 4448, null), (first, 4448, null)],
            p => p.PaneId == first.PaneId ? t : t.AddMinutes(5),
            _ => true);

        Assert.Equal(ClaimBasis.Observed, ranked[Claim.Key(first)].Basis);
        Assert.True(ranked[Claim.Key(first)].OwnsClaim);
        Assert.False(ranked[Claim.Key(joined)].OwnsClaim);
    }

    /// <summary>
    /// TLA+ <c>NoOwnerFromUnwitnessedRegistration</c>, the three-sweep counterexample: a claim first
    /// recorded under a narrow view must stay untrusted across every later sweep, even once the whole
    /// fleet is finally collected. Recomputing trust from the current sweep's coverage is what let fleet
    /// expansion promote a first look; persisting per-registration provenance is what stops it.
    /// </summary>
    /// <remarks>
    /// B truly predates A, but B's host joins the tool's view later. Sweep 1 collects only A's host, so
    /// A's registration is unwitnessed. Sweep 2 adds B's host — B is new, so also unwitnessed — and A
    /// continues, keeping its unwitnessed status. Sweep 3 collects the same full fleet: both witness
    /// conditions now hold, but both claims are unchanged, so the persisted unwitnessed status survives and
    /// the order is STILL inferred. Only a witnessed re-registration — B releases and re-claims while the
    /// tool watches — can establish a trustworthy order.
    /// </remarks>
    [Fact]
    public void ObservedNeverPromotesAClaimFirstSeenUnderANarrowView()
    {
        string path = TempPath();
        try
        {
            TmuxPane a = Window("%1", host: "a");
            TmuxPane b = Window("%2", host: "b");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            // Sweep 1 (narrow): A's host is seen for the first time, so its registration is unwitnessed.
            history.AdoptEpoch("a", "1:1", t);
            history.Observe(a, t, claimedPr: 4448, registrationWitnessed: false);

            // Sweep 2 (B's host joins): B is new — unwitnessed — and A continues, keeping unwitnessed.
            history.AdoptEpoch("a", "1:1", t.AddMinutes(10));
            history.AdoptEpoch("b", "2:1", t.AddMinutes(10));
            history.Observe(a, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: true);
            history.Observe(b, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: false);

            Claim sweep2 = Claim.Register([(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.IsWitnessed)[Claim.Key(a)];
            Assert.Equal(ClaimBasis.Inferred, sweep2.Basis);
            Assert.False(sweep2.OwnsClaim);

            // Sweep 3 (same full fleet): both witness conditions now hold, but both claims are unchanged,
            // so the persisted unwitnessed status survives and the order is STILL inferred.
            history.Observe(a, t.AddMinutes(20), claimedPr: 4448, registrationWitnessed: true);
            history.Observe(b, t.AddMinutes(20), claimedPr: 4448, registrationWitnessed: true);
            IReadOnlyDictionary<string, Claim> sweep3 = Claim.Register([(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.IsWitnessed);
            Assert.Equal(ClaimBasis.Inferred, sweep3[Claim.Key(a)].Basis);
            Assert.All(sweep3.Values, c => Assert.False(c.OwnsClaim));

            // Only a witnessed re-registration establishes a trustworthy order. Both release and re-claim
            // while the tool watches: now both are witnessed and the order is observed.
            history.Observe(a, t.AddMinutes(30), claimedPr: null);
            history.Observe(b, t.AddMinutes(30), claimedPr: null);
            history.Observe(a, t.AddMinutes(40), claimedPr: 4448, registrationWitnessed: true);
            history.Observe(b, t.AddMinutes(50), claimedPr: 4448, registrationWitnessed: true);
            Claim sweep5 = Claim.Register([(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.IsWitnessed)[Claim.Key(a)];
            Assert.Equal(ClaimBasis.Observed, sweep5.Basis);
            Assert.True(sweep5.OwnsClaim);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Sweep</c> gap semantics, and the continuity antecedent
    /// (<c>HostOf(w) \in lastCollected</c>) the step properties now carry: a registration is frozen only
    /// across CONTINUOUS observation. A host absent from the previous sweep and collected now is a gap —
    /// the window may have released and reclaimed unseen — so both its place in the queue and its witness
    /// are reset. This is the reset the model permits outside continuity, and the counterexample the
    /// earlier Sweep transition missed by preserving a registration whenever the epoch and claim matched,
    /// regardless of whether the host was in the prior sweep.
    /// </summary>
    [Fact]
    public void AGapBreaksContinuityAndResetsBothTimeAndWitness()
    {
        string path = TempPath();
        try
        {
            TmuxPane a = Window("%1", host: "a", epoch: "1:1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            // Sweep 1: host "a" collected, establishing continuity; the window claims nothing yet.
            history.AdoptEpoch("a", "1:1", t);
            history.Observe(a, t, claimedPr: null);
            history.Save([a], ["a"]);

            // Sweep 2: continuous (a was in the prior collected set) under a complete view, so the window's
            // new claim is witnessed and takes its place in the queue.
            Assert.True(history.AdoptEpoch("a", "1:1", t.AddMinutes(10)));
            history.Observe(a, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: true);
            DateTimeOffset? placedAt = history.ClaimedAt(a);
            Assert.True(history.IsWitnessed(a));
            history.Save([a], ["a"]);

            // Sweep 3: a gap — a different host is collected and "a" is omitted, so Save records the break
            // in continuity. Its window is unseen, not departed, so the registration is retained for now.
            history.Save([], ["b"]);
            Assert.Equal(placedAt, history.ClaimedAt(a));

            // Sweep 4: "a" reappears at the same epoch. AdoptEpoch reports it is no longer continuous and
            // clears the stale registration; the reclaim is a fresh, unwitnessed one — the model's
            // gap-return resetting regTime and setting regWitnessed FALSE, which the continuity antecedent
            // on the stability steps is what permits.
            Assert.False(history.AdoptEpoch("a", "1:1", t.AddMinutes(30)));
            history.Observe(a, t.AddMinutes(30), claimedPr: 4448, registrationWitnessed: false);

            Assert.Equal(t.AddMinutes(30), history.ClaimedAt(a));
            Assert.NotEqual(placedAt, history.ClaimedAt(a));
            Assert.False(history.IsWitnessed(a));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>ServerRestarts</c> advances one host's epoch and resets only its windows; every other host
    /// and its registrations remain. Production: <c>AdoptEpoch</c> under a new epoch invalidates only that
    /// host, while a sibling collected under an unchanged epoch keeps its witnessed registration.
    /// </summary>
    [Fact]
    public void ARestartOnOneHostLeavesTheOtherHostsRegistrationIntact()
    {
        string path = TempPath();
        try
        {
            TmuxPane a = Window("%1", host: "a", epoch: "1:1");
            TmuxPane b = Window("%1", host: "b", epoch: "2:1");
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            history.AdoptEpoch("a", "1:1", t);
            history.AdoptEpoch("b", "2:1", t);
            history.AdoptEpoch("a", "1:1", t.AddMinutes(10));
            history.AdoptEpoch("b", "2:1", t.AddMinutes(10));
            history.Observe(a, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: true);
            history.Observe(b, t.AddMinutes(10), claimedPr: 4600, registrationWitnessed: true);
            Assert.True(history.IsWitnessed(a));
            Assert.True(history.IsWitnessed(b));

            // Host a's server restarts (a new epoch); host b is collected unchanged.
            Assert.False(history.AdoptEpoch("a", "9:9", t.AddMinutes(20)));
            Assert.True(history.AdoptEpoch("b", "2:1", t.AddMinutes(20)));

            // a's registration is gone with its old server; b's is untouched.
            Assert.Null(history.ClaimedAt(a));
            Assert.False(history.IsWitnessed(a));
            Assert.Equal(t.AddMinutes(10), history.ClaimedAt(b));
            Assert.True(history.IsWitnessed(b));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Sweep</c> ranges over every subset of hosts, the empty set included: a total failure
    /// records <c>lastCollected = {}</c>, so the next return of any host is a gap that resets its
    /// continuity. Production: Save with no collected hosts marks every known host discontinuous.
    /// </summary>
    [Fact]
    public void AnEmptySweepBreaksEveryKnownHostsContinuity()
    {
        string path = TempPath();
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);
            history.AdoptEpoch("a", "1:1", t);
            history.AdoptEpoch("b", "2:1", t);
            history.Save([], ["a", "b"]);
            Assert.True(history.AdoptEpoch("a", "1:1", t.AddMinutes(10)));

            // A total failure: nothing collected this sweep.
            history.Save([], []);

            // The next return of a is a gap — its continuity was broken by the empty sweep.
            Assert.False(history.AdoptEpoch("a", "1:1", t.AddMinutes(20)));
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>AgentActs</c>: opening the first live window on a host advances that host's epoch — a
    /// server start. Production records no epoch for a host observed empty (<c>RecordSweptEmpty</c> stores
    /// <c>Epoch = null</c>: no tmux server was running), so the first window that appears there is under a
    /// new, unknown generation. Its claim therefore cannot be witnessed as continuous from the empty
    /// sweep, and a contest between the first windows on a just-started server owns nothing until a
    /// continuous sweep records it.
    /// </summary>
    [Fact]
    public void AnEmptyHostsFirstWindowStartsANewServerSoAContestIsNotWitnessed()
    {
        string path = TempPath();
        try
        {
            DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
            var history = new PaneHistory(path);

            // Sweep 1: host "a" collected but EMPTY — no windows, so no server generation was observed.
            history.RecordSweptEmpty("a", t);
            history.Save([], ["a"]);

            // Sweep 2: two windows now claim the same PR on "a" — a contest. The server started since the
            // empty sweep, so AdoptEpoch is not continuous, and registrationWitnessed (computed exactly as
            // the commands do: viewComplete AND the host was swept in full before) is false even under a
            // complete view.
            DateTimeOffset? prior = history.SweptAt("a");
            bool continuous = history.AdoptEpoch("a", "100:1", t.AddMinutes(10));
            Assert.False(continuous);
            bool witnessed = true /* viewComplete */ && (continuous ? prior : null) is not null;
            Assert.False(witnessed);

            TmuxPane w1 = Window("%1", host: "a", epoch: "100:1");
            TmuxPane w2 = Window("%2", host: "a", epoch: "100:1");
            history.Observe(w1, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: witnessed);
            history.Observe(w2, t.AddMinutes(10), claimedPr: 4448, registrationWitnessed: witnessed);

            Assert.False(history.IsWitnessed(w1));
            Assert.False(history.IsWitnessed(w2));

            // With no witnessed order, the contest owns nothing — neither first window on the just-started
            // server can be driven.
            IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
                [(w1, 4448, null), (w2, 4448, null)], history.ClaimedAt, history.IsWitnessed, viewComplete: true);
            Assert.All(ranked.Values, c => Assert.False(c.OwnsClaim));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
