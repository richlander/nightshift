namespace Octoshift.Commands;

using System.Diagnostics;

/// <summary>
/// Resolves the <c>owner/name</c> repo scope octoshift watches, like <c>gh</c> does: an explicit
/// <c>--repo owner/name</c> wins, otherwise it is inferred from the current worktree's <c>origin</c> remote.
/// </summary>
internal static class RepoScope
{
    /// <summary>
    /// The outcome of resolving a repeatable <c>--repo</c> scope: the ordered, de-duplicated repos to
    /// search, and — when a supplied flag was malformed — the usage error that must fail the whole
    /// invocation. A malformed flag is never silently dropped: dropping it would narrow the scope the
    /// operator asked for and could turn a real collision into a false unique, or a real hit into a
    /// not-found, so the command refuses rather than proceeds.
    /// </summary>
    public readonly record struct Resolution(IReadOnlyList<string> Repos, string? Error);

    /// <summary>
    /// Resolves the ordered, de-duplicated set of repo scopes to search. Explicit <c>--repo owner/name</c>
    /// flags — repeatable — win and are searched in the order given; when none are supplied the scope is
    /// inferred from the current worktree's <c>origin</c> remote, preserving the single-repo default. If any
    /// explicit flag is not a well-formed <c>owner/name</c> the whole resolution fails with a usage error —
    /// the caller must not infer or proceed. An empty <see cref="Resolution.Repos"/> with no error means
    /// nothing could be inferred, which the caller reports as its own usage error.
    /// </summary>
    public static Resolution Resolve(IReadOnlyList<string> repoFlags)
    {
        ArgumentNullException.ThrowIfNull(repoFlags);

        if (repoFlags.Count == 0)
        {
            return new Resolution(Resolve((string?)null) is { } inferred ? [inferred] : [], null);
        }

        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string flag in repoFlags)
        {
            if (NormalizeSlug(flag?.Trim()) is not { } slug)
            {
                return new Resolution([], $"'{flag}' is not a valid owner/name repository; pass --repo owner/name.");
            }

            if (seen.Add(slug))
            {
                ordered.Add(slug);
            }
        }

        return new Resolution(ordered, null);
    }

    /// <summary>
    /// Validates one <c>--repo</c> value at the parser, mirroring the host validator: returns a usage-error
    /// message when it is not a well-formed <c>owner/name</c>, or null when it is. Keeps a malformed scope
    /// from reaching an API path where it could widen, narrow, or corrupt the set of repos searched.
    /// </summary>
    public static string? Validate(string value)
        => NormalizeSlug(value?.Trim()) is null
            ? $"--repo value '{value}' is not a valid owner/name repository"
            : null;

    /// <summary>
    /// Resolves the scope from an optional <paramref name="repoFlag"/> (<c>owner/name</c>), falling back to
    /// the <c>origin</c> remote URL. Returns null when neither yields a well-formed <c>owner/name</c>.
    /// </summary>
    public static string? Resolve(string? repoFlag)
    {
        if (!string.IsNullOrWhiteSpace(repoFlag))
        {
            return NormalizeSlug(repoFlag.Trim());
        }

        return NormalizeSlug(ParseRemote(RunGit("remote get-url origin")));
    }

    /// <summary>Extracts <c>owner/name</c> from an <c>origin</c> URL (SSH, HTTPS, or scp-like git@ form).</summary>
    internal static string? ParseRemote(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        string trimmed = url.Trim();

        // scp-like: git@github.com:owner/repo(.git)
        int at = trimmed.IndexOf('@', StringComparison.Ordinal);
        int colon = trimmed.IndexOf(':', StringComparison.Ordinal);
        if (at >= 0 && colon > at && !trimmed.Contains("://", StringComparison.Ordinal))
        {
            return NormalizeSlug(trimmed[(colon + 1)..]);
        }

        // URL forms: https://host/owner/repo(.git), ssh://git@host/owner/repo(.git)
        int scheme = trimmed.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            string afterScheme = trimmed[(scheme + 3)..];
            int slash = afterScheme.IndexOf('/', StringComparison.Ordinal);
            if (slash >= 0)
            {
                return NormalizeSlug(afterScheme[(slash + 1)..]);
            }
        }

        return null;
    }

    private static string? NormalizeSlug(string? candidate)
    {
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return null;
        }

        string slug = candidate.Trim().Trim('/');
        if (slug.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            slug = slug[..^4];
        }

        string[] parts = slug.Split('/');
        return parts.Length == 2 && IsSafeSegment(parts[0]) && IsSafeSegment(parts[1]) ? $"{parts[0]}/{parts[1]}" : null;
    }

    /// <summary>
    /// Whether one <c>owner</c> or <c>name</c> segment is safe to place in a <c>repos/{owner}/{name}</c>
    /// REST path. GitHub owner and repo identifiers are drawn from <c>[A-Za-z0-9._-]</c>; anything else — a
    /// space, a control character, a path or query metacharacter — is rejected rather than accepted as a
    /// one-slash string, so a malformed scope cannot corrupt the path or read as a different repo. The
    /// bare relative names <c>.</c> and <c>..</c> are rejected for the same reason.
    /// </summary>
    private static bool IsSafeSegment(string segment)
    {
        if (segment.Length == 0 || segment is "." or "..")
        {
            return false;
        }

        foreach (char c in segment)
        {
            bool ok = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '_' or '-';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private static string? RunGit(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using Process? proc = Process.Start(psi);
            if (proc is null)
            {
                return null;
            }

            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit();
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
