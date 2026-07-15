namespace Nightshift;

using System.Diagnostics;

/// <summary>
/// Thin wrappers over the local <c>git</c> CLI. Nightshift is not GitHub-aware, but it does read the local
/// working copy: the worktree root anchors an agent's identity, and the current branch is the recovery key
/// (<c>recover</c> re-attaches to the order the branch name encodes).
/// </summary>
internal static class Git
{
    /// <summary>The worktree root, or the current directory if this is not a git worktree.</summary>
    public static string WorktreeRoot() => Run("rev-parse --show-toplevel") ?? Directory.GetCurrentDirectory();

    /// <summary>The checked-out branch, or null when detached or outside a repository.</summary>
    public static string? CurrentBranch()
    {
        string? branch = Run("branch --show-current");
        return string.IsNullOrEmpty(branch) ? null : branch;
    }

    private static string? Run(string args)
    {
        try
        {
            var psi = new ProcessStartInfo("git", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = Process.Start(psi);
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
            // git not installed or not runnable.
            return null;
        }
    }
}
