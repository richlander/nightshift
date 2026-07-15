namespace Octoshift.GitHub;

using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The live <see cref="IMergedPrSource"/>: merged nightshift PRs sourced from GitHub via the <c>gh</c> CLI.
/// It uses <c>gh api -i</c> (headers included) with an <c>If-None-Match</c> conditional request so an idle
/// poll comes back 304 — no body, no rate cost — and surfaces GitHub's <c>X-Poll-Interval</c> and
/// <c>X-RateLimit-*</c> headers so the poller honors the provider's back-pressure. JSON is parsed with
/// System.Text.Json source generation (no reflection) to stay NativeAOT-safe. I/O only; all decisions live
/// in the pure <see cref="Octoshift.Commands.LandDecision"/> and <see cref="AdaptivePoller"/>.
/// </summary>
internal sealed class GhMergedPrSource : IMergedPrSource
{
    private const string BranchPrefix = "nightshift/";
    private readonly string _repo;
    private readonly int _perPage;

    public GhMergedPrSource(string repo, int perPage = 50)
    {
        _repo = repo;
        _perPage = perPage;
    }

    public async Task<MergedPrPage> FetchMergedAsync(DateTimeOffset? since, string? etag, CancellationToken ct)
    {
        string path = $"repos/{_repo}/pulls?state=closed&sort=updated&direction=desc&per_page={_perPage}";
        var args = new List<string> { "api", path, "-i" };
        if (!string.IsNullOrEmpty(etag))
        {
            args.Add("-H");
            args.Add($"If-None-Match: {etag}");
        }

        GhResult gh = await RunGhAsync(args, ct);
        (string headerBlock, string body) = SplitHeadersAndBody(gh.Stdout);
        int status = StatusCode(headerBlock, gh.Stderr);
        string? responseEtag = HeaderValue(headerBlock, "etag") ?? etag;
        int pollInterval = HeaderInt(headerBlock, "x-poll-interval");

        if (status == 304)
        {
            return MergedPrPage.NotModifiedWith(responseEtag) with { ProviderMinIntervalSeconds = pollInterval };
        }

        if (status is 403 or 429 || status >= 500 || RateBudgetDepleted(headerBlock))
        {
            return new MergedPrPage
            {
                MergedPrs = [],
                ETag = responseEtag,
                ProviderMinIntervalSeconds = pollInterval,
                RateLimited = true,
                RateLimitResetSeconds = SecondsUntilReset(headerBlock),
            };
        }

        return new MergedPrPage
        {
            MergedPrs = ParseMerged(body, since),
            ETag = responseEtag,
            ProviderMinIntervalSeconds = pollInterval,
        };
    }

    /// <summary>Filters the pulls payload to merged nightshift branches newer than the watermark, newest first.</summary>
    internal static IReadOnlyList<MergedPr> ParseMerged(string body, DateTimeOffset? since)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        PullDto[]? pulls;
        try
        {
            pulls = JsonSerializer.Deserialize(body, GhJsonContext.Default.PullDtoArray);
        }
        catch (JsonException)
        {
            return [];
        }

        if (pulls is null)
        {
            return [];
        }

        var merged = new List<MergedPr>();
        foreach (PullDto pull in pulls)
        {
            if (pull.MergedAt is not { } mergedAtRaw
                || pull.Head?.Ref is not { Length: > 0 } headRef
                || !headRef.StartsWith(BranchPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (!DateTimeOffset.TryParse(mergedAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset mergedAt))
            {
                continue;
            }

            if (since is { } watermark && mergedAt <= watermark)
            {
                continue;
            }

            merged.Add(new MergedPr(pull.Number, headRef, mergedAt));
        }

        merged.Sort((a, b) => b.MergedAt.CompareTo(a.MergedAt));
        return merged;
    }

    /// <summary>Splits a <c>gh api -i</c> response into its header block and JSON body at the first blank line.</summary>
    internal static (string Headers, string Body) SplitHeadersAndBody(string response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return (string.Empty, string.Empty);
        }

        string normalized = response.Replace("\r\n", "\n", StringComparison.Ordinal);
        int split = normalized.IndexOf("\n\n", StringComparison.Ordinal);
        return split < 0
            ? (normalized, string.Empty)
            : (normalized[..split], normalized[(split + 2)..]);
    }

    /// <summary>Reads the HTTP status code from the status line, falling back to a <c>(HTTP nnn)</c> note in stderr.</summary>
    internal static int StatusCode(string headerBlock, string stderr)
    {
        foreach (string line in headerBlock.Split('\n'))
        {
            if (line.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
                {
                    return code;
                }
            }
        }

        int marker = stderr.IndexOf("(HTTP ", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
        {
            string tail = stderr[(marker + 6)..];
            var digits = new string(tail.TakeWhile(char.IsDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code))
            {
                return code;
            }
        }

        return 0;
    }

    internal static string? HeaderValue(string headerBlock, string name)
    {
        foreach (string line in headerBlock.Split('\n'))
        {
            int colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0 && line[..colon].Trim().Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return line[(colon + 1)..].Trim();
            }
        }

        return null;
    }

    private static int HeaderInt(string headerBlock, string name)
        => int.TryParse(HeaderValue(headerBlock, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value > 0 ? value : 0;

    private static bool RateBudgetDepleted(string headerBlock)
    {
        string? remaining = HeaderValue(headerBlock, "x-ratelimit-remaining");
        return remaining is not null
            && int.TryParse(remaining, NumberStyles.Integer, CultureInfo.InvariantCulture, out int left)
            && left <= 0;
    }

    private static int SecondsUntilReset(string headerBlock)
    {
        string? reset = HeaderValue(headerBlock, "x-ratelimit-reset");
        if (reset is null || !long.TryParse(reset, NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch))
        {
            return 0;
        }

        double seconds = DateTimeOffset.FromUnixTimeSeconds(epoch).Subtract(DateTimeOffset.UtcNow).TotalSeconds;
        return seconds > 0 ? (int)Math.Ceiling(seconds) : 0;
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

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return new GhResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct GhResult(int ExitCode, string Stdout, string Stderr);
}

/// <summary>The slice of a GitHub pull object octoshift reads: number, merge instant, and head branch.</summary>
internal sealed record PullDto
{
    [JsonPropertyName("number")]
    public int Number { get; init; }

    [JsonPropertyName("merged_at")]
    public string? MergedAt { get; init; }

    [JsonPropertyName("head")]
    public HeadDto? Head { get; init; }
}

/// <summary>The head ref of a pull — the branch that maps back to an order.</summary>
internal sealed record HeadDto
{
    [JsonPropertyName("ref")]
    public string? Ref { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(PullDto[]))]
internal partial class GhJsonContext : JsonSerializerContext
{
}
