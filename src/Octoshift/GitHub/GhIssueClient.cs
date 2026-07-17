namespace Octoshift.GitHub;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>The live <see cref="IIssueClient"/>, implemented with the <c>gh</c> CLI.</summary>
internal sealed class GhIssueClient : IIssueClient
{
    private readonly string _repo;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> _runGhAsync;

    public GhIssueClient(string repo)
        : this(repo, RunGhAsync)
    {
    }

    internal GhIssueClient(string repo, Func<IReadOnlyList<string>, CancellationToken, Task<GhResult>> runGhAsync)
    {
        _repo = repo;
        _runGhAsync = runGhAsync;
    }

    public async Task<IssueState> GetIssueStateAsync(int issueNumber, CancellationToken ct)
    {
        var args = new List<string>
        {
            "issue", "view",
            issueNumber.ToString(CultureInfo.InvariantCulture),
            "--repo", _repo,
            "--json", "state",
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode != 0)
        {
            string detail = gh.Stderr.Trim();
            Console.Error.WriteLine($"octoshift: gh issue view #{issueNumber} failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
            return IssueState.Unknown;
        }

        IssueStateDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(gh.Stdout, GhIssueJsonContext.Default.IssueStateDto);
        }
        catch (JsonException)
        {
            return IssueState.Unknown;
        }

        return dto?.State?.ToUpperInvariant() switch
        {
            "OPEN" => IssueState.Open,
            "CLOSED" => IssueState.Closed,
            _ => IssueState.Unknown,
        };
    }

    public async Task<IssueCloseOutcome> CloseIssueAsync(int issueNumber, string comment, CancellationToken ct)
    {
        var args = new List<string>
        {
            "issue", "close",
            issueNumber.ToString(CultureInfo.InvariantCulture),
            "--repo", _repo,
            "--comment", comment,
        };

        GhResult gh = await _runGhAsync(args, ct);
        if (gh.ExitCode == 0)
        {
            return IssueCloseOutcome.Closed;
        }

        string detail = gh.Stderr.Trim();
        if (detail.Contains("already closed", StringComparison.OrdinalIgnoreCase))
        {
            return IssueCloseOutcome.AlreadyClosed;
        }

        Console.Error.WriteLine($"octoshift: gh issue close #{issueNumber} failed (exit {gh.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        return IssueCloseOutcome.Failed;
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

internal sealed record IssueStateDto
{
    [JsonPropertyName("state")]
    public string? State { get; init; }
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(IssueStateDto))]
internal partial class GhIssueJsonContext : JsonSerializerContext
{
}
