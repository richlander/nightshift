namespace Octoshift.Commands;

using System.Diagnostics;

/// <summary>
/// Resolves the <c>owner/name</c> repo scope octoshift watches, like <c>gh</c> does: an explicit
/// <c>--repo owner/name</c> wins, otherwise it is inferred from the current worktree's <c>origin</c> remote.
/// </summary>
internal static class RepoScope
{
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
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? $"{parts[0]}/{parts[1]}" : null;
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
