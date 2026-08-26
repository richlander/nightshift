namespace Octoshift.Tests;

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
    /// TLA+ <c>SoleClaimantIsAlwaysOwner</c>: a window that is the only claimant of its PR is always
    /// actionable. The anti-degenerate property — every other rule is satisfied by a tool that never
    /// acts, so one of them must require that it does.
    /// </summary>
    [Fact]
    public void SoleClaimantIsAlwaysOwner()
    {
        TmuxPane only = Window("%1");

        Assert.True(Claim.Register([(only, 4448, null)], _ => null, _ => null)[Claim.Key(only)].OwnsClaim);
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
            _ => t);

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
            _ => DateTimeOffset.UnixEpoch);

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
                    [(a, 4448, null), (b, 4448, null)], history.ClaimedAt, history.SweptAt);

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

        Assert.True(Claim.Register([(visible, 4448, null)], _ => null, _ => null, viewComplete: true)
            [Claim.Key(visible)].OwnsClaim);

        Claim partial = Claim.Register([(visible, 4448, null)], _ => null, _ => null, viewComplete: false)[Claim.Key(visible)];
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
            Assert.Contains("fernie", narrowed.KnownHosts);
            Assert.Contains("merritt", narrowed.KnownHosts);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    /// TLA+ <c>Observed</c>: the order is a fact only when every recorded time is distinct and at most
    /// one claimant has no record. The scenario the model exists to get right — one agent working, a
    /// second joining later, with the tool running throughout.
    /// </summary>
    [Fact]
    public void ObservedRequiresAWitnessedRegistration()
    {
        TmuxPane first = Window("%1");
        TmuxPane joined = Window("%2");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(joined, 4448, null), (first, 4448, null)],
            p => p.PaneId == first.PaneId ? t : null,
            _ => t.AddMinutes(30));

        Assert.Equal(ClaimBasis.Observed, ranked[Claim.Key(first)].Basis);
        Assert.True(ranked[Claim.Key(first)].OwnsClaim);
        Assert.False(ranked[Claim.Key(joined)].OwnsClaim);
    }

    /// <summary>
    /// TLA+ <c>Observed</c>, fleet-expansion case: a registration only orders a claim if its host was
    /// under observation before the time was recorded. A rival on a host first seen this run has "now"
    /// for its registration, which is a first look rather than an appearance — ranking a genuinely
    /// observed record against it would launder a narrow view into a fleet-wide fact.
    /// </summary>
    /// <remarks>
    /// The exact laundering the review found: host A's window is recorded from an earlier run, host B is
    /// added this run, and B's window gets "now". Both times are recorded and distinct, so the old rule
    /// called the order observed and granted A ownership without proving which claim came first.
    /// </remarks>
    [Fact]
    public void ObservedRequiresEveryHostSweptBeforeThisRun()
    {
        TmuxPane onA = Window("%1", host: "a");
        TmuxPane onB = Window("%2", host: "b");
        DateTimeOffset t = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);

        IReadOnlyDictionary<string, Claim> ranked = Claim.Register(
            [(onA, 4448, null), (onB, 4448, null)],
            p => p.PaneId == onA.PaneId ? t : t.AddHours(1),

            // Only A was swept in full before this run; B is newly configured, so its recorded time is a
            // first look, not a witnessed appearance.
            host => host == "a" ? t.AddHours(-1) : null);

        Assert.Equal(ClaimBasis.Inferred, ranked[Claim.Key(onA)].Basis);
        Assert.All(ranked.Values, c => Assert.False(c.OwnsClaim));
    }
}
