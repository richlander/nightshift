namespace Octoshift.Waiting;

/// <summary>
/// A record an agent published that names nothing this reader can look up.
/// </summary>
/// <remarks>
/// Deliberately not an <see cref="AgentState"/> with a placeholder number. Every consumer of that type
/// treats <c>PrNumber</c> as a thing on GitHub — it is fetched, printed as <c>#n</c>, compared against a
/// head, and reported to an operator as the work the window is doing — so a sentinel there would be a
/// fake identity travelling everywhere a real one goes. <c>#0</c> is not a PR, and a row saying it is
/// would be a second wrong fact in a report about wrongness.
///
/// What survives instead is what can be said without an identity: the defects, which name exactly how
/// the record fails its own grammar, and the recommendation, because <c>rec=stop</c> is an agent asking
/// to be released and that request is not made less real by the field that should have named its
/// subject. The record was dropped entirely before this existed: an idle window named <c>worker</c>
/// publishing <c>pr=none head=pending rec=stop</c> vanished from the default report, and the escalation
/// with it.
/// </remarks>
internal sealed record UnidentifiedState
{
    /// <summary>What the record asked for. Reported so an escalation is not lost; never acted on.</summary>
    public Recommendation Recommendation { get; init; }

    /// <summary>Every way the record fails the contract, including the missing identity itself.</summary>
    public required IReadOnlyList<string> Defects { get; init; }
}

/// <summary>
/// What reading a window's <c>@agent_state</c> produced: an identified record, a published record that
/// identified nothing, or nothing at all.
/// </summary>
/// <remarks>
/// The three are different facts about a window and the report treats them differently, so they are
/// three cases rather than a nullable record. "Nothing published and nothing in the name" is an empty
/// shell — usually a plain shell prompt — and belongs under <c>--all</c>. "Published something that
/// names nothing" is an agent that tried to report and got it wrong, which is a defect an operator
/// wants to see by default. Collapsing them, as a bare null did, hides the second behind the first.
/// </remarks>
internal readonly record struct StateReading
{
    /// <summary>The record when something identified a PR or an issue.</summary>
    public AgentState? Identified { get; private init; }

    /// <summary>The record when nothing did, and the option was not empty.</summary>
    public UnidentifiedState? Unidentified { get; private init; }

    /// <summary>Nothing was published and the window name identifies nothing.</summary>
    public static StateReading Absent => default;

    public static StateReading For(AgentState state) => new() { Identified = state };

    public static StateReading Unusable(UnidentifiedState state) => new() { Unidentified = state };

    /// <summary>Ways the record contradicts its own contract, whichever case it fell into.</summary>
    public IReadOnlyList<string> Defects => Identified?.Defects ?? Unidentified?.Defects ?? [];
}
