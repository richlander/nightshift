namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// GitHub-backed source for idempotent outbound PR opening: returns order branches that already have OPEN or
/// MERGED PRs, so done orders with an existing branch PR are skipped.
/// </summary>
internal sealed class GhExistingOrderPrSource : IExistingOrderPrSource
{
    private const string DefaultBranchPrefix = "nightshift/";
    private readonly string _repo;
    private readonly int _limit;
    private readonly string _branchPrefix;
    private readonly bool _exactBranch;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;

    public GhExistingOrderPrSource(string repo, int limit = 1000)
        : this(repo, limit, RunGhAsync, DefaultBranchPrefix, exactBranch: false)
    {
    }

    internal GhExistingOrderPrSource(
        string repo,
        int limit,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync,
        string branchPrefix = DefaultBranchPrefix,
        bool exactBranch = false)
    {
        _repo = repo;
        _limit = Math.Max(1, limit);
        _runGhAsync = runGhAsync;
        _branchPrefix = branchPrefix;
        _exactBranch = exactBranch;
    }

    public async Task<ExistingOrderPrsSnapshot> FetchOpenOrMergedAsync(CancellationToken ct)
    {
        var args = new List<string>
        {
            "pr", "list",
            "--repo", _repo,
            "--state", "all",
            "--search", $"head:{_branchPrefix}",
            "--limit", _limit.ToString(CultureInfo.InvariantCulture),
            "--json", "headRefName,state",
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode != 0)
        {
            string detail = gh.Stderr.Trim();
            Console.Error.WriteLine($"octoshift: gh pr list (existing order PRs) failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
            return new ExistingOrderPrsSnapshot(Success: false, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal));
        }

        if (!TryParseOpenOrMergedBranches(gh.Stdout, _branchPrefix, _exactBranch, out HashSet<string>? branches))
        {
            Console.Error.WriteLine("octoshift: gh pr list (existing order PRs) returned malformed JSON.");
            return new ExistingOrderPrsSnapshot(Success: false, OpenOrMergedHeadBranches: new HashSet<string>(StringComparer.Ordinal));
        }

        return new ExistingOrderPrsSnapshot(Success: true, OpenOrMergedHeadBranches: branches);
    }

    /// <summary>
    /// Parses <c>gh pr list</c> output into a set of branch names that already have OPEN or MERGED PRs.
    /// </summary>
    internal static bool TryParseOpenOrMergedBranches(
        string body,
        string branchPrefix,
        bool exactBranch,
        out HashSet<string> branches)
    {
        branches = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(body))
        {
            return true;
        }

        PrListDto[]? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize(body, GhJsonContext.Default.PrListDtoArray);
        }
        catch (JsonException)
        {
            return false;
        }

        if (pulls is null)
        {
            return true;
        }

        foreach (PrListDto pull in pulls)
        {
            if (pull.HeadRefName is not { Length: > 0 } headRef
                || !MatchesScope(headRef, branchPrefix, exactBranch)
                || !BlocksOpen(pull.State))
            {
                continue;
            }

            branches.Add(headRef);
        }

        return true;
    }

    private static bool MatchesScope(string branch, string branchPrefix, bool exactBranch)
        => exactBranch
            ? string.Equals(branch, branchPrefix, StringComparison.Ordinal)
            : branch.StartsWith(branchPrefix, StringComparison.Ordinal);

    private static bool BlocksOpen(string? state)
        => state?.ToUpperInvariant() is "OPEN" or "MERGED";

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
