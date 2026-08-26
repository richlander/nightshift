namespace Octoshift.Waiting;

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
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
    /// The collection script, run once per host. It emits a <em>manifest</em> of every window, closes it,
    /// and only then emits the captures — each introduced by a header naming a pane id and nothing else,
    /// and closed by a marker saying whether the capture succeeded.
    /// </summary>
    /// <remarks>
    /// Batched because the obvious shape — list, then capture each — is one round trip per window, which
    /// is unnoticeable locally and ruinous over ssh: a host running twenty-two agent windows would cost
    /// twenty-three connections per sweep.
    ///
    /// Framed this way because pane text is arbitrary, hostile-capable content. Agents routinely print
    /// this tool's own output and source, so a capture can contain anything the framing uses. Requiring a
    /// marker to <em>parse</em> as a window row does not help: a pane emitting a well-formed row would
    /// split its own capture and inject a window that does not exist, carrying whatever state it liked —
    /// and a forged row naming a real head with corroborating fields would be graded high confidence and
    /// become eligible to act on. So window metadata comes only from the manifest, which is closed before
    /// any capture begins, and captures may only attach text to a pane the manifest already named. The
    /// per-run nonce makes the framing itself unguessable; the manifest makes guessing it insufficient.
    ///
    /// The capture is explicitly non-fatal: without that the loop inherits the last capture's status, so
    /// one pane closing mid-sweep would condemn the host and discard every row already collected. But
    /// non-fatal is not the same as unremarkable — a pane that could not be read is <em>said</em> to be
    /// unreadable, because an empty capture is otherwise indistinguishable from a quiet prompt and would
    /// be classified idle, which is the state a verdict may be acted on in.
    /// </remarks>
    private static string BuildScript(string nonce) => ScriptTemplate.Replace("NONCE", nonce, StringComparison.Ordinal);

    /// <summary>
    /// The script with its framing token left as <c>NONCE</c>. Kept uninterpolated so the tmux format
    /// braces and the shell's <c>${...}</c> read exactly as they will run.
    /// </summary>
    private const string ScriptTemplate = """
        w=$(tmux list-windows -a -F '#{pane_id}|#{session_name}:#{window_index}|#{session_attached}|#{window_activity}|#{@agent_state}|#{window_name}') || exit 3
        printf 'NONCE:manifest\n%s\nNONCE:end\n' "$w"
        printf '%s\n' "$w" | while IFS= read -r m; do
          [ -n "$m" ] || continue
          i=${m%%|*}
          if c=$(tmux capture-pane -p -t "$i" 2>/dev/null); then
            printf 'NONCE:pane %s\n%s\nNONCE:read %s\n' "$i" "$c" "$i"
          else
            printf 'NONCE:pane %s\nNONCE:lost %s\n' "$i" "$i"
          fi
        done
        """;

    private readonly string? _host;
    private readonly Func<string, CancellationToken, Task<CommandResult>> _runAsync;

    public TmuxScanner(string? host = null, Func<string, CancellationToken, Task<CommandResult>>? runAsync = null)
    {
        _host = host;
        _runAsync = runAsync ?? ShellRunner.For(host);
    }

    /// <summary>A fresh, unguessable framing token per collection.</summary>
    private static string NewNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>
    /// Collects every window on this scanner's host. Throws when tmux could not be reached: an
    /// unreachable host and an idle one must not report the same thing.
    /// </summary>
    public async Task<IReadOnlyList<TmuxPane>> ScanAsync(CancellationToken ct)
    {
        string nonce = NewNonce();
        CommandResult result = await _runAsync(BuildScript(nonce), ct);
        if (result.ExitCode != 0)
        {
            string detail = result.Stderr.Trim() is { Length: > 0 } stderr ? stderr : $"exited {result.ExitCode}";
            throw Unavailable(_host, detail);
        }

        return ParseCollection(result.Stdout, _host, nonce);
    }

    private static TmuxUnavailableException Unavailable(string? host, string detail)
        => new(host is null ? detail : $"{host}: {detail}");

    /// <summary>
    /// Reads the manifest, then attaches each capture to the pane it names. Window metadata comes only
    /// from the manifest, so no amount of pane content can introduce, rename or restate a window.
    /// </summary>
    /// <exception cref="TmuxUnavailableException">
    /// The output does not carry this collection's complete manifest. A successful exit code is not by
    /// itself evidence that the collection ran: <c>--host=-V</c> asked ssh for its version, a transport
    /// can succeed while writing something else entirely, and a connection dropped mid-manifest truncates
    /// it. All three would otherwise be indistinguishable from a host with no windows, which is how an
    /// invisible fleet gets reported as a quiet one.
    /// </exception>
    internal static IReadOnlyList<TmuxPane> ParseCollection(string stdout, string? host, string nonce)
    {
        string manifestOpen = nonce + ":manifest";
        string manifestClose = nonce + ":end";
        string paneHeader = nonce + ":pane ";
        string paneRead = nonce + ":read ";
        string paneLost = nonce + ":lost ";

        var order = new List<string>();
        var windows = new Dictionary<string, TmuxPane>(StringComparer.Ordinal);
        var captures = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);

        // A pane may be introduced once, and is only readable once its own closing marker says so. Both
        // are sets rather than flags on the capture so a pane whose capture came back empty — legitimately
        // or because it vanished — cannot be reintroduced by a later pane's text.
        var headed = new HashSet<string>(StringComparer.Ordinal);
        var read = new HashSet<string>(StringComparer.Ordinal);

        bool manifestOpened = false;
        bool manifestClosed = false;
        string? current = null;

        foreach (string line in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!manifestClosed)
            {
                if (!manifestOpened)
                {
                    manifestOpened = line == manifestOpen;
                    continue;
                }

                if (line == manifestClose)
                {
                    manifestClosed = true;
                    continue;
                }

                if (ParseWindow(line, host) is { } window && windows.TryAdd(window.PaneId, window))
                {
                    order.Add(window.PaneId);
                    captures[window.PaneId] = new StringBuilder();
                }

                continue;
            }

            // Past the manifest, a header may only select a pane that is already known, only once, and
            // only where the script would emit one: with no capture open. A repeat would let a pane's own
            // text reopen an earlier window, and a mid-capture header would let it open a later one.
            if (current is null
                && line.StartsWith(paneHeader, StringComparison.Ordinal)
                && line[paneHeader.Length..] is { Length: > 0 } paneId
                && windows.ContainsKey(paneId)
                && headed.Add(paneId))
            {
                current = paneId;
                continue;
            }

            // A closing marker may only name the pane it closes, so a capture cannot declare a neighbour
            // readable — the one claim that would turn an unread pane back into an actionable one.
            if (current is not null && Closes(line, paneRead, current))
            {
                read.Add(current);
                current = null;
                continue;
            }

            if (current is not null && Closes(line, paneLost, current))
            {
                current = null;
                continue;
            }

            if (current is not null)
            {
                captures[current].AppendLine(line);
            }
        }

        if (!manifestClosed)
        {
            throw Unavailable(host, manifestOpened
                ? "tmux collection was truncated: the manifest never closed"
                : "tmux collection returned no manifest; the output is not this collection's");
        }

        return [.. order.Select(id => Finish(windows[id], captures[id].ToString(), read.Contains(id)))];
    }

    private static bool Closes(string line, string marker, string paneId)
        => line.Length == marker.Length + paneId.Length
            && line.StartsWith(marker, StringComparison.Ordinal)
            && line.EndsWith(paneId, StringComparison.Ordinal);

    /// <summary>
    /// Attaches a capture, or says plainly that there is none. An unread pane is <see
    /// cref="PaneActivity.Unreadable"/> rather than idle: idle is the state a published record is taken as
    /// a handover in, and a pane nobody could read has handed over nothing.
    /// </summary>
    private static TmuxPane Finish(TmuxPane pane, string capture, bool read)
        => read
            ? pane with { Capture = capture, Activity = ClassifyActivity(capture) }
            : pane with { Capture = string.Empty, Activity = PaneActivity.Unreadable };

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
