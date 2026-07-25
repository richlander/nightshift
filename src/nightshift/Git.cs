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

    /// <summary>Resolves <paramref name="rev"/> to a commit SHA, or null if it cannot be resolved.</summary>
    public static string? RevParse(string rev) => Run($"rev-parse --verify {rev}^{{commit}}");

    /// <summary>Returns true when <paramref name="ancestor"/> is an ancestor of <paramref name="descendant"/>.</summary>
    public static bool IsAncestor(string ancestor, string descendant) => RunOk($"merge-base --is-ancestor {ancestor} {descendant}");

    /// <summary>Fast-forwards the current branch to <paramref name="target"/> only.</summary>
    public static bool MergeFastForwardOnly(string target) => RunOk($"merge --ff-only --no-stat {target}");

    /// <summary>Resets the current branch and working tree to <paramref name="target"/>.</summary>
    public static bool ResetHardTo(string target) => RunOk($"reset --hard {target}");

    /// <summary>Sets <paramref name="refName"/> to <paramref name="sha"/>.</summary>
    public static bool UpdateRef(string refName, string sha) => RunOk($"update-ref {refName} {sha}");

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

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            string output = stdoutTask.GetAwaiter().GetResult().Trim();
            _ = stderrTask.GetAwaiter().GetResult();
            return proc.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git not installed or not runnable.
            return null;
        }
    }

    private static bool RunOk(string args)
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
                return false;
            }

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            _ = stdoutTask.GetAwaiter().GetResult();
            _ = stderrTask.GetAwaiter().GetResult();
            return proc.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git not installed or not runnable.
            return false;
        }
    }
}
