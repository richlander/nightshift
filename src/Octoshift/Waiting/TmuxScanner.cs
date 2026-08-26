namespace Octoshift.Waiting;

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
/// Identity and state come from window options, because that is where the agent publishes them; the pane
/// is captured only to classify activity, since whether a window is mid-turn or holding a prompt open is
/// the one thing an option cannot say. Both halves travel in a single collection script — one command for
/// the whole host, whatever the window count.
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
    /// <strong>Every value on the wire is hex-encoded</strong>, one <c>od</c> per field, and that is the
    /// load-bearing part. A window name, a session name and <c>@agent_state</c> are all arbitrary text an
    /// agent sets, so any framing built from raw values can be split by a value: a newline inside
    /// <c>@agent_state</c> tore one manifest row into two fragments, both fragments failed to parse, and
    /// the window disappeared from a sweep that then reported QUIET. Hex cannot contain a newline, a
    /// <c>|</c> or a space, so no value can reach the framing at all — the encoding, not the parser's
    /// vigilance, is what makes a row unsplittable. Captures are encoded for the same reason: pane text
    /// is arbitrary, hostile-capable content, and agents routinely print this tool's own output and
    /// source, so an unencoded capture can contain any marker the framing uses.
    ///
    /// Window metadata still comes only from the manifest, which is closed before any capture begins, so
    /// even a capture that somehow reached the framing could not introduce, rename or restate a window.
    /// The per-run nonce makes the framing unguessable; the manifest makes guessing it insufficient; the
    /// encoding makes reaching it impossible.
    ///
    /// The capture is explicitly non-fatal: without that the loop inherits the last capture's status, so
    /// one pane closing mid-sweep would condemn the host and discard every row already collected. But
    /// non-fatal is not the same as unremarkable — a pane that could not be read is <em>said</em> to be
    /// unreadable, with its own <c>lost</c> frame, because an empty capture is otherwise indistinguishable
    /// from a quiet prompt and would be classified idle, which is the state a verdict may be acted on in.
    ///
    /// The cost is a handful of <c>tmux</c> and <c>od</c> invocations per window instead of one
    /// <c>list-windows</c> for the host. They are all local to the host being swept — the sweep still
    /// costs exactly one connection — and a sweep runs on a human timescale, so paying a few hundred
    /// forks to make a fleet impossible to hide from is the right trade.
    /// </remarks>
    private static string BuildScript(string nonce) => ScriptTemplate.Replace("NONCE", nonce, StringComparison.Ordinal);

    /// <summary>
    /// The script with its framing token left as <c>NONCE</c>. Kept uninterpolated so the tmux format
    /// braces read exactly as they will run.
    /// </summary>
    /// <remarks>
    /// Every emitted token is hex, so rows accumulate space-separated in <c>$r</c> and pane ids in
    /// <c>$p</c>: no value can contain a space, which is what lets the manifest be built whole and
    /// printed only once it is complete. <c>set -f</c> keeps the two unquoted expansions that read them
    /// back to word splitting alone, with no pathname expansion behind it. A window that vanishes between
    /// the listing and its fields is dropped from both, so it is never named in the manifest and never
    /// expected in a capture frame.
    /// </remarks>
    private const string ScriptTemplate = """
        set -f
        e() { printf %s "$1" | od -v -An -tx1 | tr -d '[:space:]'; }
        w=$(tmux list-windows -a -F '#{pane_id}') || exit 3
        r=''
        p=''
        for i in $w; do
          case $i in %[0-9]*) ;; *) continue ;; esac
          t=$(tmux display-message -p -t "$i" '#{session_name}:#{window_index}' 2>/dev/null) || continue
          a=$(tmux display-message -p -t "$i" '#{session_attached}' 2>/dev/null) || continue
          y=$(tmux display-message -p -t "$i" '#{window_activity}' 2>/dev/null) || continue
          s=$(tmux display-message -p -t "$i" '#{@agent_state}' 2>/dev/null) || continue
          n=$(tmux display-message -p -t "$i" '#{window_name}' 2>/dev/null) || continue
          r="$r NONCE:w|$(e "$i")|$(e "$t")|$(e "$a")|$(e "$y")|$(e "$s")|$(e "$n")"
          p="$p $i"
        done
        printf 'NONCE:manifest\n'
        for x in $r; do printf '%s\n' "$x"; done
        printf 'NONCE:end\n'
        for i in $p; do
          if c=$(tmux capture-pane -p -t "$i" 2>/dev/null); then
            printf 'NONCE:pane %s\n%s\nNONCE:read %s\n' "$i" "$(e "$c")" "$i"
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
    /// The output is not this collection's complete, well-formed account of the host. A successful exit
    /// code is not by itself evidence that the collection ran: <c>--host=-V</c> asked ssh for its version,
    /// a transport can succeed while writing something else entirely, and a connection dropped mid-stream
    /// truncates it. Nor is a parseable prefix evidence: a manifest row that does not decode, a pane named
    /// twice, a pane that never got a capture frame, a frame left open, or a frame that arrives twice all
    /// mean the collection is not the host — and every one of them, left non-fatal, shrinks the reported
    /// fleet silently. Fail loudly instead: an invisible fleet reported as a quiet one is the failure this
    /// whole path exists to prevent. The one exception is an explicit, complete <c>lost</c> frame, which
    /// is the host <em>saying</em> that one pane could not be read.
    /// </exception>
    internal static IReadOnlyList<TmuxPane> ParseCollection(string stdout, string? host, string nonce)
    {
        string manifestOpen = nonce + ":manifest";
        string manifestClose = nonce + ":end";
        string rowMarker = nonce + ":w|";
        string paneHeader = nonce + ":pane ";
        string paneRead = nonce + ":read ";
        string paneLost = nonce + ":lost ";

        var order = new List<string>();
        var windows = new Dictionary<string, TmuxPane>(StringComparer.Ordinal);
        var captures = new Dictionary<string, string?>(StringComparer.Ordinal);

        bool manifestOpened = false;
        bool manifestClosed = false;

        // The open frame, and the capture it has produced so far. A frame is HEADER, one encoded body
        // line, then the matching close — so `body` distinguishes "waiting for the capture" from
        // "waiting for the marker that says the capture is complete".
        string? current = null;
        string? body = null;

        foreach (string line in stdout.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (!manifestClosed)
            {
                if (!manifestOpened)
                {
                    // Anything before the manifest opens is the transport's, not ours: a login banner, a
                    // motd, whatever an rc file printed. It carries no metadata, so it is skipped.
                    manifestOpened = line == manifestOpen;
                    continue;
                }

                if (line == manifestClose)
                {
                    manifestClosed = true;
                    continue;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                TmuxPane window = ParseRow(line, rowMarker, host);
                if (!windows.TryAdd(window.PaneId, window))
                {
                    // Two rows for one pane means the rows are not a faithful listing, and taking either
                    // one is a guess about which of them the host meant.
                    throw Unavailable(host, $"tmux collection listed pane {window.PaneId} twice");
                }

                order.Add(window.PaneId);
                continue;
            }

            if (current is null)
            {
                if (line.Length == 0)
                {
                    continue;
                }

                if (line.StartsWith(paneHeader, StringComparison.Ordinal))
                {
                    string paneId = line[paneHeader.Length..];
                    if (!windows.ContainsKey(paneId))
                    {
                        throw Unavailable(host, $"tmux collection captured pane {paneId}, which its manifest never listed");
                    }

                    if (captures.ContainsKey(paneId))
                    {
                        // Two frames for one pane is two accounts of one screen; the second would
                        // overwrite the first, so which one is the pane's is undecidable.
                        throw Unavailable(host, $"tmux collection framed pane {paneId} twice");
                    }

                    current = paneId;
                    body = null;
                    continue;
                }

                // A close with no header before it is a frame whose header did not arrive — the stream is
                // not the shape the script writes, so nothing after it can be trusted to be either.
                throw Unavailable(host, line.StartsWith(paneRead, StringComparison.Ordinal) || line.StartsWith(paneLost, StringComparison.Ordinal)
                    ? "tmux collection closed a capture that was never opened"
                    : "tmux collection carried content outside a capture frame");
            }

            if (body is null)
            {
                // `lost` closes immediately: there is no capture to carry.
                if (Closes(line, paneLost, current))
                {
                    captures[current] = null;
                    current = null;
                    continue;
                }

                if (!TryDecode(line, out string decoded))
                {
                    throw Unavailable(host, $"tmux collection framed pane {current} with something that is not an encoded capture");
                }

                body = decoded;
                continue;
            }

            // A capture may only be closed by the marker naming the pane it belongs to, so nothing can
            // declare a neighbour read — the one claim that would turn an unread pane back into an
            // actionable one.
            if (!Closes(line, paneRead, current))
            {
                throw Unavailable(host, $"tmux collection did not close the capture of pane {current}");
            }

            captures[current] = body;
            current = null;
            body = null;
        }

        if (!manifestClosed)
        {
            throw Unavailable(host, manifestOpened
                ? "tmux collection was truncated: the manifest never closed"
                : "tmux collection returned no manifest; the output is not this collection's");
        }

        if (current is not null)
        {
            throw Unavailable(host, $"tmux collection ended with the capture of pane {current} still open");
        }

        // Every listed pane must have been spoken for. A pane with no frame at all is the truncation case
        // that reads as a quiet window: the manifest said it exists and the collection never said what it
        // was doing.
        if (order.FirstOrDefault(id => !captures.ContainsKey(id)) is { } missing)
        {
            throw Unavailable(host, $"tmux collection never captured pane {missing}");
        }

        return [.. order.Select(id => Finish(windows[id], captures[id]))];
    }

    private static bool Closes(string line, string marker, string paneId)
        => line.Length == marker.Length + paneId.Length
            && line.StartsWith(marker, StringComparison.Ordinal)
            && line.EndsWith(paneId, StringComparison.Ordinal);

    /// <summary>
    /// Decodes one hex-encoded field. Returns false for anything that is not hex, which is how a
    /// truncated line or foreign output is told from a value.
    /// </summary>
    private static bool TryDecode(string hex, out string text)
    {
        text = string.Empty;
        if (hex.Length % 2 != 0)
        {
            return false;
        }

        foreach (char c in hex)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        if (hex.Length > 0)
        {
            text = Encoding.UTF8.GetString(Convert.FromHexString(hex));
        }

        return true;
    }

    /// <summary>
    /// Attaches a capture, or says plainly that there is none. A pane with no capture is <see
    /// cref="PaneActivity.Unreadable"/> rather than idle: idle is the state a published record is taken as
    /// a handover in, and a pane nobody could read has handed over nothing.
    /// </summary>
    private static TmuxPane Finish(TmuxPane pane, string? capture)
        => capture is null
            ? pane with { Capture = string.Empty, Activity = PaneActivity.Unreadable }
            : pane with { Capture = capture, Activity = ClassifyActivity(capture) };

    /// <summary>
    /// Parses one manifest row: the row marker, then six hex-encoded fields. A row that does not decode
    /// is a failure rather than a dropped line — dropping it loses a window, and a lost window is
    /// indistinguishable from a window that is not there.
    /// </summary>
    internal static TmuxPane ParseRow(string line, string rowMarker, string? host)
    {
        if (!line.StartsWith(rowMarker, StringComparison.Ordinal))
        {
            throw Unavailable(host, "tmux collection carried a manifest line this collection did not write");
        }

        string[] parts = line[rowMarker.Length..].Split('|');
        if (parts.Length != 6)
        {
            throw Unavailable(host, $"tmux collection returned a manifest row of {parts.Length} field(s), not 6");
        }

        var fields = new string[6];
        for (int i = 0; i < parts.Length; i++)
        {
            if (!TryDecode(parts[i], out fields[i]))
            {
                throw Unavailable(host, "tmux collection returned a manifest row that is not encoded");
            }
        }

        if (!IsPaneId(fields[0]))
        {
            throw Unavailable(host, $"tmux collection returned a manifest row naming '{fields[0]}', which is not a pane id");
        }

        return new TmuxPane
        {
            PaneId = fields[0],
            Target = fields[1],
            Host = host,
            SessionAttached = fields[2].Trim() != "0" && fields[2].Trim().Length > 0,
            LastActivity = ParseActivity(fields[3], host),
            AgentStateOption = fields[4].Trim() is { Length: > 0 } option ? option : null,
            WindowName = fields[5].Trim(),
        };
    }

    private static DateTimeOffset? ParseActivity(string value, string? host)
    {
        if (value == "0")
        {
            return null;
        }

        if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out long epoch)
            || epoch <= 0
            || epoch > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            throw Unavailable(host, $"tmux collection returned out-of-range window activity '{value}'");
        }

        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }

    /// <summary>A tmux pane id: <c>%</c> and digits, which is every id tmux mints and nothing else.</summary>
    private static bool IsPaneId(string value)
    {
        if (value.Length < 2 || value[0] != '%')
        {
            return false;
        }

        foreach (char c in value.AsSpan(1))
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
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
