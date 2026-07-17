namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The live <see cref="IOrderIssuePrSource"/> for §4.3 fan-out closing. It fetches unmerged and merged
/// nightshift order PRs separately so the ever-growing merged history can never crowd out the unmerged set
/// (the premature-close hazard). The decision layer then collapses per-order state.
/// </summary>
internal sealed class GhOrderIssuePrSource : IOrderIssuePrSource
{
    private const string BranchPrefix = "nightshift/";
    private readonly string _repo;
    private readonly int _unmergedLimit;
    private readonly int _mergedLimit;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;

    public GhOrderIssuePrSource(string repo, int limit = 1000)
        : this(repo, limit, limit, RunGhAsync)
    {
    }

    internal GhOrderIssuePrSource(string repo, int limit, Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
        : this(repo, limit, limit, runGhAsync)
    {
    }

    internal GhOrderIssuePrSource(string repo, int unmergedLimit, int mergedLimit, Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        _repo = repo;
        _unmergedLimit = Math.Max(1, unmergedLimit);
        _mergedLimit = Math.Max(1, mergedLimit);
        _runGhAsync = runGhAsync;
    }

    public async Task<IReadOnlyList<OrderIssuePr>> FetchOrderIssuePrsAsync(CancellationToken ct)
    {
        // Safety-critical set: if this fetch fails, return no actions (never close on partial evidence).
        FetchOutcome unmerged = await FetchBySearchAsync($"head:{BranchPrefix} -is:merged", _unmergedLimit, "unmerged issue bindings", ct);
        if (!unmerged.Success)
        {
            return [];
        }

        // Opportunistic set: missing merged evidence only delays close, never causes a premature close.
        FetchOutcome merged = await FetchBySearchAsync($"head:{BranchPrefix} is:merged", _mergedLimit, "merged issue bindings", ct);
        if (!merged.Success)
        {
            return unmerged.OrderPrs;
        }

        return [.. unmerged.OrderPrs, .. merged.OrderPrs];
    }

    /// <summary>Parses order PRs with issue bindings, dropping foreign/invalid branches and unrecognized states.</summary>
    internal static IReadOnlyList<OrderIssuePr> ParseOrderIssuePrs(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        OrderIssuePrListDto[]? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize(body, GhOrderIssueJsonContext.Default.OrderIssuePrListDtoArray);
        }
        catch (JsonException)
        {
            return [];
        }

        if (pulls is null)
        {
            return [];
        }

        var orderPrs = new List<OrderIssuePr>();
        foreach (OrderIssuePrListDto pull in pulls)
        {
            if (pull.HeadRefName is not { Length: > 0 } headRef
                || !headRef.StartsWith(BranchPrefix, StringComparison.Ordinal)
                || StateOf(pull.State) is not { } state)
            {
                continue;
            }

            var issues = new HashSet<int>();
            if (pull.ClosingIssuesReferences is { Length: > 0 } refs)
            {
                foreach (ClosingIssueRefDto issue in refs)
                {
                    if (issue.Number > 0)
                    {
                        issues.Add(issue.Number);
                    }
                }
            }

            if (issues.Count == 0)
            {
                continue;
            }

            orderPrs.Add(new OrderIssuePr(pull.Number, headRef, state, issues.ToArray()));
        }

        return orderPrs;
    }

    private static OrderPrState? StateOf(string? state) => state?.ToUpperInvariant() switch
    {
        "OPEN" => OrderPrState.Open,
        "CLOSED" => OrderPrState.Closed,
        "MERGED" => OrderPrState.Merged,
        _ => null,
    };

    private async Task<FetchOutcome> FetchBySearchAsync(string search, int limit, string label, CancellationToken ct)
    {
        var args = new List<string>
        {
            "pr", "list",
            "--repo", _repo,
            "--state", "all",
            "--search", search,
            "--limit", limit.ToString(CultureInfo.InvariantCulture),
            "--json", "number,headRefName,state,closingIssuesReferences",
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode != 0)
        {
            string detail = gh.Stderr.Trim();
            Console.Error.WriteLine($"octoshift: gh pr list ({label}) failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
            return new FetchOutcome(false, []);
        }

        return new FetchOutcome(true, ParseOrderIssuePrs(gh.Stdout));
    }

    private static async Task<GhResult> RunGhAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("gh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = new Process { StartInfo = psi };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data is not null) { stdout.AppendLine(e.Data); } };
        proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) { stderr.AppendLine(e.Data); } };

        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return new GhResult(127, stdout.ToString(), ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return new GhResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}

internal sealed record OrderIssuePrListDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("headRefName")]
    public string? HeadRefName { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("closingIssuesReferences")]
    public ClosingIssueRefDto[]? ClosingIssuesReferences { get; init; }
}

internal sealed record ClosingIssueRefDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(OrderIssuePrListDto[]))]
internal partial class GhOrderIssueJsonContext : JsonSerializerContext
{
}

internal readonly record struct FetchOutcome(bool Success, IReadOnlyList<OrderIssuePr> OrderPrs);
