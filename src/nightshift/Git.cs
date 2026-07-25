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

    /// <summary>
    /// True when <paramref name="commitish"/> resolves to a commit in the local object database. All
    /// worktrees of a repository share one object database, so the instant a stacked parent's worker
    /// commits its contract in another worktree, that ref (a branch or a pinned SHA) is reachable here —
    /// no fetch, push, or merge. This is the readiness signal the coordinator uses to release a stacked
    /// child ahead of the parent's merge (see <c>docs/design/stacked-orders.md</c> §3).
    /// </summary>
    /// <remarks>
    /// The base ref is passed as a single contiguous argument after <c>--end-of-options</c>, so a value with
    /// spaces or a leading dash can never be reinterpreted as git options — a malformed ref simply fails to
    /// resolve and returns <c>false</c> rather than probing an unintended object.
    /// </remarks>
    public static bool IsReachable(string commitish)
        => !string.IsNullOrWhiteSpace(commitish)
            && RunArgs("rev-parse", "--verify", "--quiet", "--end-of-options", $"{commitish}^{{commit}}") is not null;

    private static string? Run(string args)
    {
        var psi = new ProcessStartInfo("git", args);
        return Run(psi);
    }

    private static string? RunArgs(params string[] args)
    {
        var psi = new ProcessStartInfo("git");
        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        return Run(psi);
    }

    private static string? Run(ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        try
        {
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
