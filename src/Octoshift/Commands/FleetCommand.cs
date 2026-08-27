namespace Octoshift.Commands;

using System.Text.Json;
using Octoshift.Waiting;

/// <summary>
/// Manages the declared fleet: the persistent set of collection targets every <c>waiting</c> and
/// <c>pr</c> sweep reaches. Membership grows on its own — attempting a target (the local machine, or a
/// <c>--host</c> alias) declares it, which is what keeps a first-time failure from being forgotten — so
/// the only thing an operator has to do by hand is <em>retire</em> a member that should no longer count:
/// a decommissioned box, a renamed alias, a typo. Without that, a stale member is attempted forever, and
/// a sweep that cannot reach it reads as narrowed indefinitely, so a complete view — the condition every
/// ownership decision needs — becomes permanently unsatisfiable.
/// </summary>
/// <remarks>
/// The fleet is not GitHub-aware and needs no repo scope: it is a set of tmux collection targets kept in
/// the same credential-free, machine-local history the sweeps use, mutated under the same cross-process
/// transaction lock so a retire cannot race a concurrent sweep.
/// </remarks>
internal static class FleetCommand
{
    /// <summary>
    /// Lists the declared fleet. Leads its first stdout line with a <c>FLEET</c> token so a harness sees
    /// the disposition before the members, then prints one member per line; JSON emits a single document.
    /// </summary>
    public static async Task<int> RunListAsync(bool json, CancellationToken ct, string? historyPath = null)
    {
        PaneHistory? history = null;
        try
        {
            history = await PaneHistory.OpenAsync(historyPath, ct);
            IReadOnlyList<TargetId> members = history.FleetMembers();

            if (json)
            {
                WriteMembersJson(Console.OpenStandardOutput(), members, history.IsInitialized);
            }
            else if (members.Count == 0)
            {
                // An empty fleet is two different things, and the token must not conflate them. A fresh,
                // never-established history scans the local machine by default; a fleet emptied on purpose
                // by retirement scans nothing until a target is added back.
                Console.Out.WriteLine(history.IsInitialized
                    ? "FLEET empty; the fleet was emptied — nothing is swept until a target is added with 'octoshift fleet add'"
                    : "FLEET empty; the local machine is scanned by default until a host is declared");
            }
            else
            {
                Console.Out.WriteLine($"FLEET {members.Count} member(s)");
                foreach (TargetId member in members)
                {
                    Console.Out.WriteLine($"  {DisplayText.Safe(member.HumanLabel)}");
                }
            }

            return ExitCode.Ok;
        }
        catch (HistoryUnavailableException ex)
        {
            return ReportHistoryUnavailable(json, ex);
        }
        finally
        {
            history?.Dispose();
        }
    }

    /// <summary>
    /// Retires one or more targets from the declared fleet — a deliberate operator act. Unknown targets
    /// make the whole command a non-success (<c>UNKNOWN</c> token, usage exit) with nothing written, so a
    /// typo cannot silently retire the wrong thing or read as a no-op success; only when every named
    /// target is a member are they all removed and the history rewritten under the transaction lock.
    /// </summary>
    public static async Task<int> RunRetireAsync(
        IReadOnlyList<string> hosts,
        bool local,
        bool json,
        CancellationToken ct,
        string? historyPath = null)
    {
        // A retirement with no target is a usage error, not an empty success: it would rewrite nothing and
        // report a removal that did not happen.
        if (hosts.Count == 0 && !local)
        {
            Console.Error.WriteLine("octoshift: fleet retire requires at least one --host <alias> or --local.");
            return ExitCode.Usage;
        }

        // Every alias becomes a target identity, so an empty or option-shaped one is rejected here rather
        // than turned into a key that could never match a member.
        foreach (string host in hosts)
        {
            if (HostTarget.Validate(host) is { } invalid)
            {
                Console.Error.WriteLine($"octoshift: {invalid}");
                return ExitCode.Usage;
            }
        }

        // The targets to retire, local first, deduplicated by identity so naming an alias twice retires it
        // once. null is the local machine.
        var targets = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (local)
        {
            targets.Add(null);
            seen.Add(TargetId.Local.Key);
        }

        foreach (string host in hosts)
        {
            if (seen.Add(TargetId.ForHost(host).Key))
            {
                targets.Add(host);
            }
        }

        PaneHistory? history = null;
        try
        {
            history = await PaneHistory.OpenAsync(historyPath, ct);

            // Validate membership before mutating, so an all-or-nothing retire leaves the file untouched
            // when any target is unknown rather than removing the recognised ones and reporting the rest as
            // an error beside a partial write.
            TargetId[] unknown = [.. targets.Where(t => !history.IsFleetMember(t)).Select(TargetId.ForHost)];
            if (unknown.Length > 0)
            {
                return ReportUnknown(json, unknown);
            }

            foreach (string? target in targets)
            {
                history.Retire(target);
            }

            // Commit the shrunk membership and pruned per-host state, releasing the transaction lock.
            history.Persist();

            TargetId[] retired = [.. targets.Select(TargetId.ForHost)];
            if (json)
            {
                WriteTargetsJson(Console.OpenStandardOutput(), "retired", retired);
            }
            else
            {
                Console.Out.WriteLine($"RETIRED {string.Join(", ", retired.Select(t => DisplayText.Safe(t.HumanLabel)))}");
            }

            return ExitCode.Ok;
        }
        catch (HistoryUnavailableException ex)
        {
            return ReportHistoryUnavailable(json, ex);
        }
        finally
        {
            history?.Dispose();
        }
    }

    /// <summary>
    /// Adds one or more targets to the declared fleet — the deliberate counterpart to <see
    /// cref="RunRetireAsync"/>, and the only way to bring the local machine back after it has been retired,
    /// since a bare sweep no longer bootstraps local once the fleet has been established. All-or-nothing:
    /// an empty or option-shaped alias is rejected with nothing written, exactly as retire validates its
    /// targets, so a typo cannot half-declare a fleet. Adding a member already present is an idempotent
    /// success. Runs under the same transaction lock as a sweep and a retire.
    /// </summary>
    public static async Task<int> RunAddAsync(
        IReadOnlyList<string> hosts,
        bool local,
        bool json,
        CancellationToken ct,
        string? historyPath = null)
    {
        // An add with no target is a usage error, not an empty success: it would declare nothing and
        // report an addition that did not happen.
        if (hosts.Count == 0 && !local)
        {
            Console.Error.WriteLine("octoshift: fleet add requires at least one --host <alias> or --local.");
            return ExitCode.Usage;
        }

        // Every alias becomes an ssh target, so an empty or option-shaped one is rejected here — before
        // anything is written — rather than persisted as a member the collector would later choke on.
        foreach (string host in hosts)
        {
            if (HostTarget.Validate(host) is { } invalid)
            {
                Console.Error.WriteLine($"octoshift: {invalid}");
                return ExitCode.Usage;
            }
        }

        // The targets to add, local first, deduplicated by identity so naming an alias twice adds it once.
        // null is the local machine.
        var targets = new List<string?>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (local)
        {
            targets.Add(null);
            seen.Add(TargetId.Local.Key);
        }

        foreach (string host in hosts)
        {
            if (seen.Add(TargetId.ForHost(host).Key))
            {
                targets.Add(host);
            }
        }

        PaneHistory? history = null;
        try
        {
            history = await PaneHistory.OpenAsync(historyPath, ct);

            foreach (string? target in targets)
            {
                history.Add(target);
            }

            // Commit the grown membership and the now-initialized flag, releasing the transaction lock.
            history.Persist();

            TargetId[] added = [.. targets.Select(TargetId.ForHost)];
            if (json)
            {
                WriteTargetsJson(Console.OpenStandardOutput(), "added", added);
            }
            else
            {
                Console.Out.WriteLine($"ADDED {string.Join(", ", added.Select(t => DisplayText.Safe(t.HumanLabel)))}");
            }

            return ExitCode.Ok;
        }
        catch (HistoryUnavailableException ex)
        {
            return ReportHistoryUnavailable(json, ex);
        }
        finally
        {
            history?.Dispose();
        }
    }

    private static int ReportUnknown(bool json, IReadOnlyList<TargetId> unknown)
    {
        if (json)
        {
            using var writer = new Utf8JsonWriter(Console.OpenStandardOutput(), new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            writer.WriteString("error", $"not in the declared fleet: {string.Join(", ", unknown.Select(t => t.HumanLabel))}");
            writer.WriteStartArray("unknown");
            foreach (TargetId target in unknown)
            {
                WriteIdentity(writer, target);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
            writer.Flush();
            Console.OpenStandardOutput().Write("\n"u8);
        }
        else
        {
            Console.Out.WriteLine($"UNKNOWN {string.Join(", ", unknown.Select(t => DisplayText.Safe(t.HumanLabel)))} not in the declared fleet");
        }

        return ExitCode.Usage;
    }

    private static int ReportHistoryUnavailable(bool json, HistoryUnavailableException ex)
    {
        // A history failure leaves the declared fleet unknown, so — like waiting and pr — the human output
        // leads with the shared PARTIAL token and the cause goes to stderr, while JSON stays one error
        // document. A genuine caller cancellation is a different exception, not caught here.
        if (json)
        {
            WaitingCommand.WriteJsonError(Console.OpenStandardOutput(), ex.Message);
        }
        else
        {
            Console.Out.WriteLine("PARTIAL pane history unavailable; the declared fleet is unknown");
            Console.Error.WriteLine($"octoshift: {DisplayText.Safe(ex.Message)}");
        }

        return ExitCode.Unavailable;
    }

    /// <summary>
    /// Emits the fleet as one JSON document whose members preserve target kind: <c>{"kind":"local"}</c>
    /// for the real local machine, <c>{"kind":"host","host":"…"}</c> for an ssh alias — so a consumer can
    /// tell a real local from an alias literally named <c>local</c> and knows whether to pass <c>--local</c>
    /// or <c>--host local</c>. The <c>initialized</c> flag lets a reader tell an empty-on-purpose fleet from
    /// a never-established one.
    /// </summary>
    internal static void WriteMembersJson(Stream output, IReadOnlyList<TargetId> members, bool initialized)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteBoolean("initialized", initialized);
        writer.WriteStartArray("members");
        foreach (TargetId member in members)
        {
            WriteIdentity(writer, member);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        output.Write("\n"u8);
        output.Flush();
    }

    /// <summary>Emits a retire/add result as one JSON document under the given key, each target a
    /// kind-preserving identity so success output carries the same distinction the human labels do.</summary>
    internal static void WriteTargetsJson(Stream output, string key, IReadOnlyList<TargetId> targets)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteStartArray(key);
        foreach (TargetId target in targets)
        {
            WriteIdentity(writer, target);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        output.Write("\n"u8);
        output.Flush();
    }

    /// <summary>
    /// Writes one target's structured identity: <c>{"kind":"local"}</c> for the local machine, or
    /// <c>{"kind":"host","host":"&lt;alias&gt;"}</c> for a remote — the machine-readable form of the
    /// distinction <see cref="TargetId.Display"/> collapses, so a consumer never confuses the local machine
    /// with an ssh alias named <c>local</c>. The alias is the decoded value, not the opaque key.
    /// </summary>
    private static void WriteIdentity(Utf8JsonWriter writer, TargetId id)
    {
        writer.WriteStartObject();
        writer.WriteString("kind", id.KindTag);
        if (!id.IsLocal)
        {
            writer.WriteString("host", id.Display);
        }

        writer.WriteEndObject();
    }
}
