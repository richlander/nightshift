namespace Octoshift.Waiting;

/// <summary>How much the evidence behind a verdict can be relied on.</summary>
/// <remarks>
/// This exists because agents do not follow the published contract reliably, and that is the rule rather
/// than the exception. Measured across one live fleet in a single day: a state naming another window's PR
/// (four windows at once), `blocked=` entries naming nothing openable in three different spellings, a PR
/// listing itself as its own blocker, an invented `pr=none head=pending`, and — the costly one —
/// `reviews=2/2` published by two windows whose own round reports read "converging", which is not clean.
///
/// So a verdict is only as good as the field it rests on, and the fields are not equally trustworthy. A
/// tool that speaks to an agent must do so only where the evidence is strong; everywhere else it reports
/// its best guess, says how sure it is, and says plainly that it did nothing.
/// </remarks>
internal enum Confidence
{
    /// <summary>Contradicted, unreadable, or resting on a claim nothing corroborates.</summary>
    Low,

    /// <summary>Coherent, but resting on a single self-reported field or an inferred identity.</summary>
    Medium,

    /// <summary>A GitHub fact, or two independent fields of a clean record agreeing.</summary>
    High,
}

/// <summary>
/// The confidence in one verdict, and — when it is not high — the specific reason, so the dashboard can
/// say what it would have needed rather than only that it was unsure.
/// </summary>
internal readonly record struct Assurance(Confidence Level, string? Caveat)
{
    public static Assurance High { get; } = new(Confidence.High, null);

    public static Assurance Medium(string caveat) => new(Confidence.Medium, caveat);

    public static Assurance Low(string caveat) => new(Confidence.Low, caveat);

    /// <summary>
    /// Whether a tool may act on this row unattended. High only, and deliberately not a separate dial:
    /// one threshold that is visible in every row is easier to trust than a hidden second rule.
    /// </summary>
    public bool MayAct => Level == Confidence.High;

    public string Label => Level switch
    {
        Confidence.High => "high",
        Confidence.Medium => "med",
        _ => "low",
    };
}
