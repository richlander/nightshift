namespace Octoshift.Waiting;

using Octoshift.GitHub;

/// <summary>What a stopped agent's declared wait resolves to once GitHub's side is known.</summary>
internal enum WaitingState
{
    /// <summary>GitHub could not be read; nothing is asserted.</summary>
    Unknown,

    /// <summary>The record describes a sha GitHub is no longer at. Its claims are void.</summary>
    Stale,

    /// <summary>The declared condition has cleared. The agent's own <c>next</c> is ready to release.</summary>
    Ready,

    /// <summary>Something needs doing that is not what the agent was waiting for.</summary>
    Blocked,

    /// <summary>The wait is legitimate and unresolved. Nothing to do but keep holding it.</summary>
    Holding,

    /// <summary>A human decision is required.</summary>
    NeedsOperator,

    /// <summary>The PR merged. The wait is over by other means.</summary>
    Merged,

    /// <summary>The PR closed without merging.</summary>
    Closed,
}

/// <summary>
/// The join of what an agent declared with what GitHub reports — one record with a state, a human-readable
/// reason, and where applicable the directive that would resume the agent.
/// </summary>
/// <param name="State">The resolved state of the wait.</param>
/// <param name="Reason">One line naming the specific fact behind the state.</param>
/// <param name="Directive">The instruction that would resume the agent, or null when there is none.</param>
/// <param name="Releasable">
/// Whether a tool may send <paramref name="Directive"/> unattended. True only when the blocker the agent
/// itself named has cleared and the agent itself declared what comes next — so releasing repeats the
/// agent's decision rather than substituting one. Everything else, including every <see
/// cref="WaitingState.Blocked"/> verdict, carries a suggestion for a human to act on.
/// </param>
internal readonly record struct WaitingVerdict(WaitingState State, string Reason, string? Directive, bool Releasable)
{
    /// <summary>True when the row deserves the operator's attention rather than being scrolled past.</summary>
    public bool NeedsAttention => State is not WaitingState.Holding;

    /// <summary>
    /// Resolves a record against GitHub's account of the same PR. Pure: no I/O, so the whole decision
    /// table is testable without a network or a live pane.
    /// </summary>
    public static WaitingVerdict Resolve(StatusRecord record, PrFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (facts is null)
        {
            return new WaitingVerdict(WaitingState.Unknown, $"could not read PR #{record.PrNumber} from GitHub", null, false);
        }

        if (facts.Merged)
        {
            return new WaitingVerdict(WaitingState.Merged, $"PR #{facts.Number} merged", null, false);
        }

        if (string.Equals(facts.State, "closed", StringComparison.OrdinalIgnoreCase))
        {
            return new WaitingVerdict(WaitingState.Closed, $"PR #{facts.Number} closed without merging", null, false);
        }

        // Head divergence voids the record before any predicate is evaluated: every claim in it — the
        // round, the verdict, the wait — was made about code GitHub is no longer serving. The checks
        // fetched here belong to a different sha, so they cannot answer the question that was asked.
        if (record.Head is not null && !ShaMatches(record.Head, facts.HeadSha))
        {
            return new WaitingVerdict(
                WaitingState.Stale,
                $"record describes {Short(record.Head)}, GitHub head is {Short(facts.HeadSha)}",
                null,
                false);
        }

        if (record.Waiting.Kind == PredicateKind.Operator)
        {
            return new WaitingVerdict(WaitingState.NeedsOperator, "agent escalated; awaiting a human decision", null, false);
        }

        // A conflict outranks whatever the agent was waiting for: a green check on an unmergeable branch
        // still cannot land, so resuming the declared next would waste the round.
        if (facts.IsConflicting)
        {
            return new WaitingVerdict(WaitingState.Blocked, "CONFLICTING — needs a rebase onto main", "rebase onto main and push", false);
        }

        return record.Waiting.Kind switch
        {
            PredicateKind.Check => ResolveCheck(record, facts),
            PredicateKind.Merge => new WaitingVerdict(WaitingState.Ready, "mergeable", Release(record), IsReleasable(record)),
            PredicateKind.Review => new WaitingVerdict(WaitingState.Holding, "review round outstanding; GitHub will not change this", null, false),
            _ => ResolveOverall(record, facts),
        };
    }

    private static WaitingVerdict ResolveCheck(StatusRecord record, PrFacts facts)
    {
        string name = record.Waiting.CheckName!;
        CheckRunFact? check = facts.FindCheck(name);

        if (check is null)
        {
            return new WaitingVerdict(WaitingState.Holding, $"{name} has not reported on {Short(facts.HeadSha)}", null, false);
        }

        if (!check.IsComplete)
        {
            return new WaitingVerdict(WaitingState.Holding, $"{name} is {check.Status}", null, false);
        }

        if (check.IsFailure)
        {
            return new WaitingVerdict(WaitingState.Blocked, $"{name} concluded {check.Conclusion}", $"fix {name} and push", false);
        }

        return new WaitingVerdict(WaitingState.Ready, $"{name} passed", Release(record), IsReleasable(record));
    }

    private static WaitingVerdict ResolveOverall(StatusRecord record, PrFacts facts)
    {
        CheckRunFact? failed = facts.Checks.FirstOrDefault(c => c.IsFailure);
        if (failed is not null)
        {
            return new WaitingVerdict(WaitingState.Blocked, $"{failed.Name} concluded {failed.Conclusion}", $"fix {failed.Name} and push", false);
        }

        int pending = facts.Checks.Count(c => !c.IsComplete);
        if (pending > 0)
        {
            string names = string.Join(", ", facts.Checks.Where(c => !c.IsComplete).Take(3).Select(c => c.Name));
            return new WaitingVerdict(WaitingState.Holding, $"{pending} check(s) pending: {names}", null, false);
        }

        if (facts.Checks.Count == 0)
        {
            return new WaitingVerdict(WaitingState.Holding, $"no checks reported on {Short(facts.HeadSha)}", null, false);
        }

        return new WaitingVerdict(WaitingState.Ready, "all checks green and mergeable", Release(record), IsReleasable(record));
    }

    private static string? Release(StatusRecord record) => record.Next;

    /// <summary>
    /// A directive may be sent unattended only when the agent wrote it down itself. An inferred record's
    /// fields were scraped out of prose, so nothing in it is the agent's word.
    /// </summary>
    private static bool IsReleasable(StatusRecord record)
        => record.Source == RecordSource.Declared && !string.IsNullOrWhiteSpace(record.Next);

    private static bool ShaMatches(string recorded, string actual)
    {
        int length = Math.Min(recorded.Length, actual.Length);
        return length >= 7 && recorded.AsSpan(0, length).Equals(actual.AsSpan(0, length), StringComparison.OrdinalIgnoreCase);
    }

    private static string Short(string sha) => sha.Length <= 9 ? sha : sha[..9];
}
