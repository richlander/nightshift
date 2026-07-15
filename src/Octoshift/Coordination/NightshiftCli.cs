namespace Octoshift.Coordination;

using System.Diagnostics;
using System.Text;

/// <summary>
/// The live <see cref="INightshiftClient"/>: it shells out to the <c>nightshift</c> CLI. The resolved
/// socket (from octoshift's <c>--socket</c>) is passed to every child via <c>NIGHTSHIFT_SOCKET</c> so
/// octoshift and the coordinator target the same Turnstile. This type is I/O only; the land decision lives
/// in the pure <see cref="Octoshift.Commands.LandDecision"/>.
/// </summary>
internal sealed class NightshiftCli : INightshiftClient
{
    private const string Executable = "nightshift";
    private readonly string? _socket;

    public NightshiftCli(string? socket) => _socket = socket;

    public async Task<BoardState> GetBoardAsync(CancellationToken ct)
    {
        ProcessRun run = await RunAsync(["where", "--output", "json"], ct);
        return run.ExitCode == 0 ? BoardState.Parse(run.Stdout) : BoardState.Empty;
    }

    public async Task<bool> LandAsync(string orderBase, string reason, CancellationToken ct)
    {
        ProcessRun run = await RunAsync(["land", orderBase, "--reason", reason], ct);
        if (run.ExitCode != 0)
        {
            string detail = run.Stderr.Trim();
            Console.Error.WriteLine($"octoshift: nightshift land {orderBase} failed (exit {run.ExitCode}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        }

        return run.ExitCode == 0;
    }

    private async Task<ProcessRun> RunAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(Executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        if (!string.IsNullOrEmpty(_socket))
        {
            psi.Environment["NIGHTSHIFT_SOCKET"] = _socket;
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
        return new ProcessRun(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private readonly record struct ProcessRun(int ExitCode, string Stdout, string Stderr);
}
