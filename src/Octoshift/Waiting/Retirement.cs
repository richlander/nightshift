namespace Octoshift.Waiting;

/// <summary>Why a window no longer needs to exist.</summary>
internal enum RetirementReason
{
    /// <summary>It is still doing something.</summary>
    None,

    /// <summary>The PR it was working on merged.</summary>
    Merged,

    /// <summary>The PR it was working on closed without merging.</summary>
    Closed,

    /// <summary>The agent reported its work finished and is waiting to be given something else.</summary>
    Declared,

    /// <summary>The window is gone. Recorded rather than silently forgotten.</summary>
    Departed,
}

/// <summary>
/// What should happen to a window whose work is over.
/// </summary>
/// <remarks>
/// A finished window is not free. It holds a slot on a host, it appears in every sweep, and — the part
/// that matters — it holds a context that has spent its value. The transcript of work that is already
/// merged helps nothing further and costs on every turn: tokens, and an agent reasoning from assumptions
/// that were true for a PR nobody is working on any more.
///
/// So the recommendation is to clear the context, not to kill the window. The window and its session are
/// worth keeping; what is in them is not. That asymmetry is the whole of this type: a window observed
/// idle for eleven hours on work that merged three days ago is not a window to be tidied away, it is a
/// working slot nobody knew was free.
/// </remarks>
internal readonly record struct Retirement(RetirementReason Reason, string Advice)
{
    public static Retirement None { get; } = new(RetirementReason.None, string.Empty);

    public bool IsRetirable => Reason != RetirementReason.None;

    /// <summary>Whether the window itself is gone, as opposed to merely finished.</summary>
    public bool HasDeparted => Reason == RetirementReason.Departed;

    /// <summary>
    /// Decides retirement from the verdict and what the agent said. Deliberately separate from the
    /// verdict: a window can be in any state and still be spent, and conflating the two would make
    /// "this work is over" compete with "this work needs you" for one field.
    /// </summary>
    public static Retirement For(WaitingVerdict verdict, AgentState? state, PaneActivity activity)
    {
        // Retirement says a window's work is over, and only an idle pane that has handed over can be spent.
        // A pane mid-turn, holding a prompt, stalled, or unreadable is not finished whatever it last
        // published, so neither a stale `rec=done` nor a Merged/Closed verdict it should never have reached
        // while non-idle may clear the context out from under live work. This is the same activity gate the
        // verdict and follower promotion pass through (WaitingVerdict.IsHandover), so the ancillary
        // retirement decision cannot disagree with the verdict about whether the record still describes the
        // pane. A non-idle window's disposition comes from its activity-derived verdict alone.
        if (!WaitingVerdict.IsHandover(activity))
        {
            return None;
        }

        if (state?.Recommendation == Recommendation.Done)
        {
            return new(RetirementReason.Declared, "agent reports its work finished — clear the context and reuse the window");
        }

        return verdict.State switch
        {
            WaitingState.Merged => new(RetirementReason.Merged, "work merged — clear the context and reuse the window"),
            WaitingState.Closed => new(RetirementReason.Closed, "PR closed — clear the context and reuse the window"),
            _ => None,
        };
    }

    /// <summary>A window that was present at the last sweep and is not present now.</summary>
    public static Retirement Departed(string where)
        => new(RetirementReason.Departed, $"{where} is gone since the last sweep");
}
