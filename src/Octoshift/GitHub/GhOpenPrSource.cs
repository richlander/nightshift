namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The live <see cref="IOpenPrSource"/>: open (and closed-unmerged) nightshift order-PRs sourced from GitHub
/// via <c>gh pr list --state all --json number,headRefName,state,mergeable,statusCheckRollup</c>. It filters
/// to nightshift branches, drops merged PRs (the merge→land reconciler owns those), and normalizes the two
/// <c>statusCheckRollup</c> shapes (<c>CheckRun</c>/<c>StatusContext</c>) into <see cref="PrCheck"/>. JSON is
/// parsed with System.Text.Json source generation (no reflection) to stay NativeAOT-safe. I/O only; all
/// decisions live in the pure <see cref="Octoshift.Commands.ReworkDecision"/>.
/// </summary>
internal sealed class GhOpenPrSource : IOpenPrSource
{
    private const string BranchPrefix = "nightshift/";
    private readonly string _repo;
    private readonly int _limit;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;

    public GhOpenPrSource(string repo, int limit = 200)
        : this(repo, limit, RunGhAsync)
    {
    }

    internal GhOpenPrSource(string repo, int limit, Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        _repo = repo;
        _limit = Math.Max(1, limit);
        _runGhAsync = runGhAsync;
    }

    public async Task<IReadOnlyList<OpenPr>> FetchOpenAsync(CancellationToken ct)
    {
        var args = new List<string>
        {
            "pr", "list",
            "--repo", _repo,
            "--state", "all",
            "--limit", _limit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--json", "number,headRefName,state,mergeable,statusCheckRollup",
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode != 0)
        {
            string detail = gh.Stderr.Trim();
            Console.Error.WriteLine($"octoshift: gh pr list failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
            return [];
        }

        return ParseOpenPrs(gh.Stdout);
    }

    /// <summary>Parses the <c>gh pr list</c> payload into nightshift open/closed-unmerged PRs, dropping merged and foreign branches.</summary>
    internal static IReadOnlyList<OpenPr> ParseOpenPrs(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        PrListDto[]? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize(body, GhJsonContext.Default.PrListDtoArray);
        }
        catch (JsonException)
        {
            return [];
        }

        if (pulls is null)
        {
            return [];
        }

        var open = new List<OpenPr>();
        foreach (PrListDto pull in pulls)
        {
            if (pull.HeadRefName is not { Length: > 0 } headRef
                || !headRef.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                continue; // not a nightshift branch — never invent an order
            }

            PrLifecycle? lifecycle = LifecycleOf(pull.State);
            if (lifecycle is not { } state)
            {
                continue; // merged (handled by the merge->land reconciler) or unrecognized
            }

            open.Add(new OpenPr(pull.Number, headRef, state, MergeabilityOf(pull.Mergeable), MapChecks(pull.StatusCheckRollup)));
        }

        return open;
    }

    private static PrLifecycle? LifecycleOf(string? state) => state?.ToUpperInvariant() switch
    {
        "OPEN" => PrLifecycle.Open,
        "CLOSED" => PrLifecycle.Closed,
        _ => null, // MERGED or unknown — not this loop's concern
    };

    private static Mergeability MergeabilityOf(string? mergeable) => mergeable?.ToUpperInvariant() switch
    {
        "CONFLICTING" => Mergeability.Conflicting,
        "MERGEABLE" => Mergeability.Mergeable,
        _ => Mergeability.Unknown,
    };

    private static IReadOnlyList<PrCheck> MapChecks(RollupDto[]? rollup)
    {
        if (rollup is null || rollup.Length == 0)
        {
            return [];
        }

        var checks = new List<PrCheck>(rollup.Length);
        foreach (RollupDto entry in rollup)
        {
            string name = entry.Name ?? entry.Context ?? string.Empty;
            string? link = entry.DetailsUrl ?? entry.TargetUrl;
            checks.Add(new PrCheck(name, entry.Conclusion, entry.State, entry.Status, link));
        }

        return checks;
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

/// <summary>The slice of a <c>gh pr list</c> object octoshift reads for the rework loop.</summary>
internal sealed record PrListDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("headRefName")]
    public string? HeadRefName { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("mergeable")]
    public string? Mergeable { get; init; }

    [JsonPropertyName("statusCheckRollup")]
    public RollupDto[]? StatusCheckRollup { get; init; }
}

/// <summary>One <c>statusCheckRollup</c> entry — the union of the <c>CheckRun</c> and <c>StatusContext</c> shapes gh returns.</summary>
internal sealed record RollupDto
{
    [JsonPropertyName("__typename")]
    public string? TypeName { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("context")]
    public string? Context { get; init; }

    [JsonPropertyName("conclusion")]
    public string? Conclusion { get; init; }

    [JsonPropertyName("status")]
    public string? Status { get; init; }

    [JsonPropertyName("state")]
    public string? State { get; init; }

    [JsonPropertyName("detailsUrl")]
    public string? DetailsUrl { get; init; }

    [JsonPropertyName("targetUrl")]
    public string? TargetUrl { get; init; }
}
