namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

/// <summary>
/// GitHub-backed outbound PR opener. This is the authored membrane act for remote-dev (§5):
/// <c>gh pr create --fill --base main --head nightshift/&lt;plan&gt;/&lt;order&gt;</c>, with optional
/// labels/reviewers/milestone metadata and an audit callback.
/// </summary>
internal sealed class GhPrOpenSource : IPrOpenSource
{
    private readonly string _repo;
    private readonly GitHubActorIdentity _actor;
    private readonly IPrOpenMetadataProvider _metadataProvider;
    private readonly IPrOpenAuditSink _auditSink;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;
    private readonly Func<DateTimeOffset> _clock;

    public GhPrOpenSource(
        string repo,
        GitHubActorIdentity actor,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
        : this(repo, actor, NullPrOpenMetadataProvider.Instance, NullPrOpenAuditSink.Instance, runGhAsync, () => DateTimeOffset.UtcNow)
    {
    }

    internal GhPrOpenSource(
        string repo,
        GitHubActorIdentity actor,
        IPrOpenMetadataProvider metadataProvider,
        IPrOpenAuditSink auditSink,
        Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync,
        Func<DateTimeOffset> clock)
    {
        _repo = repo;
        _actor = actor;
        _metadataProvider = metadataProvider;
        _auditSink = auditSink;
        _runGhAsync = runGhAsync;
        _clock = clock;
    }

    public async Task<PrOpenOutcome> OpenAsync(string orderBase, string headBranch, CancellationToken ct)
    {
        try
        {
            PrOpenMetadata metadata = await _metadataProvider.GetMetadataAsync(orderBase, ct);
            IReadOnlyList<string> args = BuildCreateArgs(headBranch, metadata);

            GhResult gh = await _runGhAsync(args, ct);
            if (gh.ExitCode != 0)
            {
                if (LooksLikeAlreadyExists(gh.Stdout, gh.Stderr))
                {
                    return new PrOpenOutcome(PrOpenOutcomeKind.AlreadyExists);
                }

                string detail = gh.Stderr.Trim();
                Console.Error.WriteLine($"octoshift: gh pr create failed for {orderBase} ({headBranch}) (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
                return new PrOpenOutcome(PrOpenOutcomeKind.Failed);
            }

            int? prNumber = ParsePrNumber(gh.Stdout)
                ?? await LookupPrNumberAsync(headBranch, ct);
            if (prNumber is not { } number)
            {
                Console.Error.WriteLine($"octoshift: gh pr create succeeded for {orderBase} ({headBranch}) but PR number was not discoverable.");
                return new PrOpenOutcome(PrOpenOutcomeKind.Failed);
            }

            var auditRecord = new PrOpenedAuditRecord(_actor, orderBase, headBranch, number, _clock());
            await _auditSink.RecordPrOpenedAsync(auditRecord, ct);

            return new PrOpenOutcome(PrOpenOutcomeKind.Opened, number);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Console.Error.WriteLine($"octoshift: outbound PR open failed for {orderBase} ({headBranch}): {ex.Message}");
            return new PrOpenOutcome(PrOpenOutcomeKind.Failed);
        }
    }

    /// <summary>
    /// Parses a PR number from <c>gh pr create</c> output URL text.
    /// </summary>
    internal static int? ParsePrNumber(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        const string marker = "/pull/";
        int index = output.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return null;
        }

        ReadOnlySpan<char> span = output[(index + marker.Length)..].AsSpan().Trim();
        int digitsLength = 0;
        while (digitsLength < span.Length && char.IsDigit(span[digitsLength]))
        {
            digitsLength++;
        }

        if (digitsLength == 0)
        {
            return null;
        }

        return int.TryParse(span[..digitsLength], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
            ? number
            : null;
    }

    private IReadOnlyList<string> BuildCreateArgs(string headBranch, PrOpenMetadata metadata)
    {
        var args = new List<string>
        {
            "pr", "create",
            "--repo", _repo,
            "--base", "main",
            "--head", headBranch,
            "--fill",
        };

        AddMany(args, "--label", metadata.Labels);
        AddMany(args, "--reviewer", metadata.Reviewers);

        if (!string.IsNullOrWhiteSpace(metadata.Milestone))
        {
            args.Add("--milestone");
            args.Add(metadata.Milestone);
        }

        return args;
    }

    private static void AddMany(List<string> args, string optionName, IReadOnlyList<string> values)
    {
        foreach (string value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            args.Add(optionName);
            args.Add(value);
        }
    }

    private static bool LooksLikeAlreadyExists(string stdout, string stderr)
    {
        return ContainsAlreadyExists(stderr) || ContainsAlreadyExists(stdout);

        static bool ContainsAlreadyExists(string text)
            => text.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                && text.Contains("pull request", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int?> LookupPrNumberAsync(string headBranch, CancellationToken ct)
    {
        var args = new List<string>
        {
            "pr", "list",
            "--repo", _repo,
            "--state", "all",
            "--search", $"head:{headBranch}",
            "--limit", "10",
            "--json", "number,headRefName,state",
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode != 0)
        {
            return null;
        }

        PrListDto[]? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize(gh.Stdout, GhJsonContext.Default.PrListDtoArray);
        }
        catch (JsonException)
        {
            return null;
        }

        if (pulls is null)
        {
            return null;
        }

        foreach (PrListDto pull in pulls)
        {
            if (!string.Equals(pull.HeadRefName, headBranch, StringComparison.Ordinal))
            {
                continue;
            }

            if (pull.State?.ToUpperInvariant() is "OPEN" or "MERGED")
            {
                return pull.Number;
            }
        }

        return null;
    }
}

/// <summary>
/// Fail-closed PR opener used when GitHub App identity is not configured: outbound authored acts are skipped,
/// never silently sent as the ambient <c>gh</c> user.
/// </summary>
internal sealed class DisabledPrOpenSource : IPrOpenSource
{
    private readonly string _reason;
    private bool _reported;

    public DisabledPrOpenSource(string reason)
    {
        _reason = reason;
    }

    public Task<PrOpenOutcome> OpenAsync(string orderBase, string headBranch, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (!_reported)
        {
            Console.Error.WriteLine($"octoshift: outbound PR open disabled; skipping authored act: {_reason}");
            _reported = true;
        }

        return Task.FromResult(new PrOpenOutcome(PrOpenOutcomeKind.Unavailable));
    }
}
