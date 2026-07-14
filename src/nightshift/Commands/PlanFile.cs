namespace Nightshift.Commands;

/// <summary>Shared loading of a plan file (<c>orders.json</c>): resolves the commit SHA and parses it.</summary>
internal static class PlanFile
{
    public static async Task<(Plan Plan, string Sha)> LoadAsync(string path, string[] args, CancellationToken ct)
    {
        string sha = Options.Value(args, "--sha") ?? GitHead(Path.GetDirectoryName(Path.GetFullPath(path))!);
        Plan plan = Plan.Parse(await File.ReadAllTextAsync(path, ct), sha);
        return (plan, sha);
    }

    public static string ShortSha(string sha) => sha.Length > 0 ? sha[..Math.Min(sha.Length, 12)] : "(no sha)";

    public static string? FirstPositional(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].StartsWith('-'))
            {
                i++;
                continue;
            }

            return args[i];
        }

        return null;
    }

    private static string GitHead(string dir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "rev-parse HEAD")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit();
                if (proc.ExitCode == 0)
                {
                    return output;
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // git unavailable — proceed without a SHA (pass --sha instead).
        }

        return string.Empty;
    }
}
