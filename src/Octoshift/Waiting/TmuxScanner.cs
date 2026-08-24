namespace Octoshift.Waiting;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;

/// <summary>What a pane's footer says the agent in it is doing right now.</summary>
internal enum PaneActivity
{
    /// <summary>Nothing is running. This is the state a status record is meaningful in.</summary>
    Idle,

    /// <summary>The agent is mid-turn.</summary>
    Working,

    /// <summary>The agent is holding a prompt open and waiting for a keystroke.</summary>
    Blocked,
}

/// <summary>The result of running a local command.</summary>
internal readonly record struct CommandResult(int ExitCode, string Stdout, string Stderr);

/// <summary>One tmux window and the visible contents of its active pane.</summary>
internal sealed record TmuxPane
{
    public required string Target { get; init; }

    public required string WindowName { get; init; }

    /// <summary>Whether a client is attached to this window's session right now.</summary>
    public required bool SessionAttached { get; init; }

    /// <summary>When the window last produced output — an observed stop time, not a claimed one.</summary>
    public DateTimeOffset? LastActivity { get; init; }

    /// <summary>The window's <c>@agent_state</c> option: the agent's own account of where it is.</summary>
    public string? AgentStateOption { get; init; }

    public PaneActivity Activity { get; init; }

    public string Capture { get; init; } = string.Empty;
}

/// <summary>
/// Lists tmux windows and captures what each one is showing.
/// </summary>
/// <remarks>
/// One <c>list-windows</c> call carries identity and state, because the agent publishes both as window
/// options. Panes are still captured, but only to classify activity: whether a window is mid-turn or
/// holding a prompt open is the one thing an option cannot say.
/// </remarks>
internal sealed class TmuxScanner
{
    /// <summary>
    /// Window name comes last so that a <c>|</c> inside it cannot shift the fields before it.
    /// </summary>
    private const string ListFormat =
        "#{session_name}:#{window_index}|#{session_attached}|#{window_activity}|#{@agent_state}|#{window_name}";

    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>> _runAsync;

    public TmuxScanner(Func<IReadOnlyList<string>, CancellationToken, Task<CommandResult>>? runAsync = null)
        => _runAsync = runAsync ?? RunTmuxAsync;

    /// <summary>Lists every window across every session, each with its active pane's visible text.</summary>
    public async Task<IReadOnlyList<TmuxPane>> ScanAsync(CancellationToken ct)
    {
        CommandResult list = await _runAsync(["list-windows", "-a", "-F", ListFormat], ct);
        if (list.ExitCode != 0)
        {
            return [];
        }

        var panes = new List<TmuxPane>();
        foreach (TmuxPane window in ParseWindows(list.Stdout))
        {
            CommandResult capture = await _runAsync(["capture-pane", "-p", "-t", window.Target], ct);
            string text = capture.ExitCode == 0 ? capture.Stdout : string.Empty;
            panes.Add(window with { Capture = text, Activity = ClassifyActivity(text) });
        }

        return panes;
    }

    /// <summary>Parses <c>list-windows -F</c> output. Malformed rows are dropped, not guessed at.</summary>
    internal static IReadOnlyList<TmuxPane> ParseWindows(string stdout)
    {
        var windows = new List<TmuxPane>();
        foreach (string line in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string[] parts = line.Split('|', 5);
            if (parts.Length < 5 || parts[0].Length == 0)
            {
                continue;
            }

            windows.Add(new TmuxPane
            {
                Target = parts[0],
                SessionAttached = parts[1].Trim() != "0" && parts[1].Trim().Length > 0,
                LastActivity = long.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch) && epoch > 0
                    ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                    : null,
                AgentStateOption = parts[3].Trim() is { Length: > 0 } option ? option : null,
                WindowName = parts[4].Trim(),
            });
        }

        return windows;
    }

    /// <summary>
    /// Classifies a pane from its footer. A record only means "stopped" in an idle pane: the same block
    /// scrolled up while the agent works on is not a handover, and a pane holding a prompt open is
    /// waiting on a keystroke rather than on GitHub.
    /// </summary>
    internal static PaneActivity ClassifyActivity(string capture)
    {
        string footer = Footer(capture);

        if (footer.Contains("esc to cancel", StringComparison.OrdinalIgnoreCase)
            || footer.Contains("enter to confirm", StringComparison.OrdinalIgnoreCase))
        {
            return PaneActivity.Blocked;
        }

        return footer.Contains("esc to interrupt", StringComparison.OrdinalIgnoreCase)
            || footer.Contains("esc interrupt", StringComparison.OrdinalIgnoreCase)
                ? PaneActivity.Working
                : PaneActivity.Idle;
    }

    private static string Footer(string capture)
    {
        string[] lines = capture.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var tail = new StringBuilder();
        int taken = 0;
        for (int i = lines.Length - 1; i >= 0 && taken < 8; i--)
        {
            if (lines[i].Trim().Length == 0)
            {
                continue;
            }

            tail.Append(lines[i]).Append('\n');
            taken++;
        }

        return tail.ToString();
    }

    private static async Task<CommandResult> RunTmuxAsync(IReadOnlyList<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("tmux")
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
            return new CommandResult(127, string.Empty, ex.Message);
        }

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync(ct);
        return new CommandResult(proc.ExitCode, stdout.ToString(), stderr.ToString());
    }
}
