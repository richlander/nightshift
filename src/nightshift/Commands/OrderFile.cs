namespace Nightshift.Commands;

/// <summary>Shared loading of a work-order file: resolves the commit SHA and parses the model.</summary>
internal static class OrderFile
{
    public static async Task<(WorkOrder Order, string Sha)> LoadAsync(string path, string[] args, CancellationToken ct)
    {
        string sha = Options.Value(args, "--sha") ?? GitHead(Path.GetDirectoryName(Path.GetFullPath(path))!);
        WorkOrder order = WorkOrder.Parse(await File.ReadAllTextAsync(path, ct), sha);
        return (order, sha);
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
