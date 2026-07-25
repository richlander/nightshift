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
        => Execute(psi, out string output) && output.Length > 0 ? output : null;

    private static bool RunOk(string args) => RunOk(new ProcessStartInfo("git", args));

    private static bool RunOk(ProcessStartInfo psi) => Execute(psi, out _);

    /// <summary>
    /// Starts git and fully drains both stdout and stderr before waiting for exit, returning true on a zero
    /// exit code and handing back the trimmed stdout. Reading both pipes asynchronously first is the deadlock
    /// fix: synchronously reading one stream to end blocks once the other pipe's OS buffer fills (e.g. a large
    /// diffstat), so the child never exits and the read never returns. Every wrapper funnels through here, so
    /// the drain lives in exactly one place and every path — <see cref="Run(string)"/>, <see cref="RunArgs"/>,
    /// and <see cref="RunOk(string)"/> — gets the fix.
    /// </summary>
    private static bool Execute(ProcessStartInfo psi, out string output)
    {
        output = string.Empty;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
            {
                return false;
            }

            Task<string> stdoutTask = proc.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = proc.StandardError.ReadToEndAsync();
            proc.WaitForExit();
            output = stdoutTask.GetAwaiter().GetResult().Trim();
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
