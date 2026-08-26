namespace Octoshift.Waiting;

/// <summary>Where a window stands in the queue of windows claiming one PR.</summary>
/// <remarks>
/// Modelled on how a keybinding service handles two components binding the same key. Rejecting the
/// second registration loses work that is really happening; accepting it silently gives two owners and
/// a fight. The workable answer is to accept it as second-class: <b>first one wins, the rest are
/// followed.</b> The owner is the one anything may be said to; the followers are watched, reported, and
/// never driven — because driving two agents on one PR is a worse architecture than either agent alone,
/// and pretending the second does not exist is equally bad.
/// </remarks>
internal enum ClaimRank
{
    /// <summary>The only window claiming this PR.</summary>
    Sole,

    /// <summary>Registered first. This is the window that may be acted on.</summary>
    Owner,

    /// <summary>Registered later. Observed and reported, never interacted with.</summary>
    Follower,
}

/// <summary>How ownership between two claims was decided.</summary>
internal enum ClaimBasis
{
    /// <summary>Only one window claims it; nothing had to be decided.</summary>
    Uncontested,

    /// <summary>
    /// The fleet could not be collected in full, so "only one window claims this" is not a fact — the
    /// other claimant may simply be on a host that did not answer.
    /// </summary>
    PartialView,

    /// <summary>The tool watched them register, so the order is a fact.</summary>
    Observed,

    /// <summary>
    /// Both claims were already in place when the tool first looked, so the order is inferred from
    /// seniority. Rivals rarely appear in the same moment, which is what makes registration order
    /// meaningful — and also what makes it unavailable to a run that started after both.
    /// </summary>
    Inferred,
}

/// <summary>A window's standing among the windows claiming its PR.</summary>
/// <param name="Rank">Sole, owner, or follower.</param>
/// <param name="Others">The other windows claiming the same PR.</param>
/// <param name="SinceRegistered">When this window first claimed it, if known.</param>
internal readonly record struct Claim(
    ClaimRank Rank,
    IReadOnlyList<TmuxPane> Others,
    DateTimeOffset? SinceRegistered,
    ClaimBasis Basis = ClaimBasis.Uncontested)
{
    public static Claim Sole { get; } = new(ClaimRank.Sole, [], null);

    /// <summary>
    /// True when this window may be spoken to as the claim's owner. Ownership decided by inference is
    /// not ownership decided: if the tool cannot tell which agent started first, driving either of them
    /// is a coin toss, and a wrong guess drives the agent that is not doing the work.
    /// </summary>
    public bool OwnsClaim
        => Basis switch
        {
            ClaimBasis.Uncontested => Rank == ClaimRank.Sole,
            ClaimBasis.Observed => Rank is ClaimRank.Sole or ClaimRank.Owner,
            _ => false,
        };

    /// <summary>True when this window must never be spoken to because another owns the PR.</summary>
    public bool IsFollower => Rank == ClaimRank.Follower;

    /// <summary>True when more than one window claims the PR at all.</summary>
    public bool IsContested => Rank is ClaimRank.Owner or ClaimRank.Follower;

    /// <summary>
    /// True when the window holding the claim is trying to put it down — asking to stop, or already
    /// finished. Registration order decides ownership between two live claims, but an owner on its way
    /// out should not keep the claim away from a follower that is still working. Reported rather than
    /// acted on: `rec=stop` is a request awaiting an operator, and promoting on an unresolved request
    /// would settle a decision that is not the tool's to make.
    /// </summary>
    public static bool IsReleasing(AgentState state, WaitingVerdict verdict)
        => state.Recommendation == Recommendation.Stop
            || verdict.State is WaitingState.Merged or WaitingState.Closed;

    /// <summary>
    /// Ranks every window claiming a PR by when it registered, so the ordering is stable across sweeps.
    /// Windows the tool has not seen register yet sort last, and ties break on a fixed key rather than
    /// collection order — an owner that changes identity between runs would be worse than no owner.
    /// </summary>
    /// <param name="registeredAt">When a window was first seen claiming its PR, or null if never seen.</param>
    /// <param name="sweptAt">
    /// When a host was last collected in full under its current tmux server <em>before this run</em>, or
    /// null if it has not been. The prior sweep, deliberately, not this one's: a registration time orders
    /// a claim only if the host was under observation when it was recorded, so a host first seen this run
    /// contributes null here and its windows' registrations are treated as first looks rather than
    /// witnessed appearances. A window with no registration on a host swept before must have appeared
    /// since that sweep, which places it after everything already recorded without guessing.
    /// </param>
    /// <param name="viewComplete">
    /// Whether every host answered. When one did not, a window that appears to be the only claimant of
    /// its PR may simply be the only one visible — measured live: with two hosts claiming one PR and one
    /// host unreachable, the remaining window was a follower and looked sole. Since a sole claim is the
    /// one shape that is always actionable, a partial view is exactly the condition under which the tool
    /// would drive the wrong agent, so no claim is owned while the fleet is incompletely seen.
    /// </param>
    public static IReadOnlyDictionary<string, Claim> Register(
        IEnumerable<(TmuxPane Pane, int PrNumber, int? Round)> claims,
        Func<TmuxPane, DateTimeOffset?> registeredAt,
        Func<string?, DateTimeOffset?>? sweptAt = null,
        bool viewComplete = true)
    {
        var ranked = new Dictionary<string, Claim>(StringComparer.Ordinal);

        foreach (IGrouping<int, (TmuxPane Pane, int PrNumber, int? Round)> group in claims.GroupBy(c => c.PrNumber))
        {
            (TmuxPane Pane, int PrNumber, int? Round)[] contenders = [.. group];
            if (contenders.Length == 1)
            {
                ranked[Key(contenders[0].Pane)] = viewComplete
                    ? Sole
                    : new Claim(ClaimRank.Sole, [], registeredAt(contenders[0].Pane), ClaimBasis.PartialView);
                continue;
            }

            DateTimeOffset?[] recorded = [.. contenders.Select(c => registeredAt(c.Pane))];
            int unrecorded = recorded.Count(r => r is null);
            DateTimeOffset[] known = [.. recorded.OfType<DateTimeOffset>()];

            // The order is a fact only when every contender's host was already under observation before
            // its registration was recorded. A recorded time is a genuine "when it first claimed" only if
            // the tool was watching that host beforehand; a host first seen this run has "now" for every
            // window on it, which is a first look rather than an appearance. Ranking a genuinely observed
            // rival against one of those would award ownership without proving which claim came first —
            // fleet expansion cannot launder a narrow view into a fleet-wide fact. Given that, the usual
            // conditions hold: every recorded time is distinct, and at most one window has no record at all
            // (that one appeared since its host's prior sweep, which places it last). Two unrecorded
            // windows cannot be ordered against each other by anything but a guess.
            bool everyHostObservedBefore = contenders.All(c => sweptAt?.Invoke(c.Pane.Host) is not null);
            bool observed = known.Length == known.Distinct().Count()
                && unrecorded <= 1
                && everyHostObservedBefore;

            (TmuxPane Pane, int PrNumber, int? Round)[] ordered = [.. contenders
                .OrderBy(c => registeredAt(c.Pane) ?? DateTimeOffset.MaxValue)

                // Failing that, seniority: an agent at round 15 has held this work longer than one that
                // just arrived. Self-reported and therefore weak, which is why it only breaks a tie and
                // never by itself grants the right to be driven.
                .ThenByDescending(c => c.Round ?? -1)

                // Then a fixed key, so an owner does not change identity between sweeps.
                .ThenBy(c => c.Pane.Host ?? string.Empty, StringComparer.Ordinal)
                .ThenBy(c => c.Pane.PaneId, StringComparer.Ordinal)];

            for (int i = 0; i < ordered.Length; i++)
            {
                TmuxPane pane = ordered[i].Pane;
                ranked[Key(pane)] = new Claim(
                    i == 0 ? ClaimRank.Owner : ClaimRank.Follower,
                    [.. ordered.Select(c => c.Pane).Where(p => Key(p) != Key(pane))],
                    registeredAt(pane),
                    observed && viewComplete ? ClaimBasis.Observed : ClaimBasis.Inferred);
            }
        }

        return ranked;
    }

    internal static string Key(TmuxPane pane) => $"{pane.Host ?? "local"}|{pane.PaneId}";
}
