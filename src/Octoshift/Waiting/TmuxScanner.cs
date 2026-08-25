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

    /// <summary>The pane could not be captured, so nothing about it is known.</summary>
    Unreadable,
}

/// <summary>Raised when tmux itself could not be reached, as distinct from finding no windows.</summary>
internal sealed class TmuxUnavailableException(string message) : Exception(message);

/// <summary>One tmux window and the visible contents of its active pane.</summary>
internal sealed record TmuxPane
{
    /// <summary>
    /// The tmux pane id (<c>%12</c>). Used for every follow-up call: it is unique, stable across renames
    /// and reindexing, and cannot be confused by a delimiter inside a session or window name.
    /// </summary>
    public required string PaneId { get; init; }

    /// <summary>Human-readable <c>session:window</c>, for display only.</summary>
    public required string Target { get; init; }

    /// <summary>The host this window lives on, or null for this machine.</summary>
    public string? Host { get; init; }

    /// <summary>How the window is named in a report: <c>fernie cp:3</c>, or just <c>cp:3</c> locally.</summary>
    public string Where => Host is null ? Target : $"{Host} {Target}";

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
    /// One shell script, run once per host, that emits every window's metadata followed by its capture.
    /// </summary>
    /// <remarks>
    /// Batched on purpose. The obvious shape — list the windows, then capture each — is one round trip
    /// per window, which is unnoticeable locally and ruinous over SSH: a host running twenty-two agent
    /// windows would cost twenty-three connections per sweep. Assigning the listing first also means a
    /// tmux that is not running fails the command instead of yielding an empty list through a pipeline.
    ///
    /// The capture is explicitly non-fatal. Without that, the loop inherits the last capture's status, so
    /// a single pane closing between enumeration and capture exits non-zero and condemns the whole host —
    /// discarding every row already collected. Host failure is reserved for transport and the listing.
    /// </remarks>
    private const string CollectScript = """
        w=$(tmux list-windows -a -F '#{pane_id}|#{session_name}:#{window_index}|#{session_attached}|#{window_activity}|#{@agent_state}|#{window_name}') || exit 3
        printf '%s\n' "$w" | while IFS= read -r m; do
          [ -n "$m" ] || continue
          printf '@@OCTOSHIFT@@%s\n' "$m"
          tmux capture-pane -p -t "${m%%|*}" 2>/dev/null || true
        done
        """;

    private const string Marker = "@@OCTOSHIFT@@";

    private readonly string? _host;
    private readonly Func<string, CancellationToken, Task<CommandResult>> _runAsync;

    public TmuxScanner(string? host = null, Func<string, CancellationToken, Task<CommandResult>>? runAsync = null)
    {
        _host = host;
        _runAsync = runAsync ?? ShellRunner.For(host);
    }

    /// <summary>
    /// Collects every window on this scanner's host. Throws when tmux could not be reached: an
    /// unreachable host and an idle one must not report the same thing.
    /// </summary>
    public async Task<IReadOnlyList<TmuxPane>> ScanAsync(CancellationToken ct)
    {
        CommandResult result = await _runAsync(CollectScript, ct);
        if (result.ExitCode != 0)
        {
            string detail = result.Stderr.Trim() is { Length: > 0 } stderr ? stderr : $"exited {result.ExitCode}";
            throw new TmuxUnavailableException(_host is null ? detail : $"{_host}: {detail}");
        }

        return ParseCollection(result.Stdout, _host);
    }

    /// <summary>Splits the batched stream into windows, each with the capture that followed its header.</summary>
    internal static IReadOnlyList<TmuxPane> ParseCollection(string stdout, string? host)
    {
        var panes = new List<TmuxPane>();
        TmuxPane? pending = null;
        var capture = new StringBuilder();

        foreach (string line in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            // A boundary is the marker AND a well-formed window row. Pane text can contain the marker —
            // agent output quotes this tool's own source — and treating that as a boundary would truncate
            // the window it appeared in and invent one that does not exist.
            TmuxPane? header = line.StartsWith(Marker, StringComparison.Ordinal)
                ? ParseWindow(line[Marker.Length..], host)
                : null;

            if (header is null)
            {
                if (pending is not null)
                {
                    capture.AppendLine(line);
                }

                continue;
            }

            if (pending is { } previous)
            {
                panes.Add(Finish(previous, capture.ToString()));
            }

            capture.Clear();
            pending = header;
        }

        if (pending is { } last)
        {
            panes.Add(Finish(last, capture.ToString()));
        }

        return panes;
    }

    private static TmuxPane Finish(TmuxPane pane, string capture)
        => pane with { Capture = capture, Activity = ClassifyActivity(capture) };

    /// <summary>Parses one metadata line. Malformed rows are dropped, not guessed at.</summary>
    internal static TmuxPane? ParseWindow(string line, string? host)
    {
        string[] parts = line.Split('|', 6);
        if (parts.Length < 6 || !parts[0].StartsWith('%'))
        {
            return null;
        }

        return new TmuxPane
        {
            PaneId = parts[0],
            Target = parts[1],
            Host = host,
            SessionAttached = parts[2].Trim() != "0" && parts[2].Trim().Length > 0,
            LastActivity = long.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch) && epoch > 0
                ? DateTimeOffset.FromUnixTimeSeconds(epoch)
                : null,
            AgentStateOption = parts[4].Trim() is { Length: > 0 } option ? option : null,
            WindowName = parts[5].Trim(),
        };
    }

    /// <summary>
    /// Classifies a pane from its footer. A published state only means "stopped" in an idle pane: the
    /// same state set while the agent works on is not a handover, and a pane holding a prompt open is
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
}
