namespace Octoshift;

using System.CommandLine;
using Octoshift.Commands;

/// <summary>Entry dispatch for the <c>octoshift</c> GitHub-membrane CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: octoshift <reconcile> ...";

    /// <summary>
    /// The global <c>--socket</c> override, inherited by every verb and passed through to the
    /// <c>nightshift</c> subprocesses so octoshift and the coordinator target the same Turnstile.
    /// </summary>
    private static readonly Option<string?> SocketOption = new("--socket")
    {
        Description = "Path to the Turnstile socket; passed through to nightshift subprocesses.",
        Recursive = true,
    };

    private static readonly HashSet<string> KnownVerbs = ["reconcile"];

    /// <summary>Parses and invokes the command line, preserving the exit-code contract.</summary>
    public static async Task<int> RunAsync(string[] args)
    {
        if (ShouldUseLegacyUsage(args))
        {
            Console.Error.WriteLine(Usage);
            return ExitCode.Usage;
        }

        RootCommand rootCommand = CreateRootCommand();
        ParseResult result = rootCommand.Parse(args);
        if (result.Errors.Count > 0)
        {
            foreach (var error in result.Errors)
            {
                Console.Error.WriteLine(error.Message);
            }

            return ExitCode.Usage;
        }

        return await result.InvokeAsync();
    }

    /// <summary>Builds the System.CommandLine command tree for octoshift.</summary>
    internal static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("octoshift GitHub membrane");
        rootCommand.Options.Add(SocketOption);
        rootCommand.Subcommands.Add(CreateReconcileCommand());
        return rootCommand;
    }

    private static bool ShouldUseLegacyUsage(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        return args[0].StartsWith("-", StringComparison.Ordinal) || !KnownVerbs.Contains(args[0]);
    }

    private static Command CreateReconcileCommand()
    {
        var command = new Command("reconcile", "Land merged nightshift PRs (inbound merge->land membrane).");

        var once = new Option<bool>("--once") { Description = "Do a single sweep (land everything merged-but-unlanded) and exit." };
        var repo = new Option<string?>("--repo") { Description = "Repository scope owner/name; inferred from the git remote when omitted." };
        var minInterval = new Option<int?>("--min-interval") { Description = "Absolute floor on the poll interval in seconds (default 60)." };
        var maxInterval = new Option<int?>("--max-interval") { Description = "Absolute ceiling on the poll interval in seconds (default 600)." };
        var cadenceWindow = new Option<int?>("--cadence-window") { Description = "How many recent merges the cadence EWMA averages (default 10)." };
        var cadenceDecay = new Option<double?>("--cadence-decay") { Description = "Cadence EWMA decay in (0,1]; higher weights recent gaps more (default 0.3)." };
        var backoff = new Option<double?>("--backoff") { Description = "Multiplicative backoff factor for idle polls (default 2)." };

        command.Options.Add(once);
        command.Options.Add(repo);
        command.Options.Add(minInterval);
        command.Options.Add(maxInterval);
        command.Options.Add(cadenceWindow);
        command.Options.Add(cadenceDecay);
        command.Options.Add(backoff);

        command.SetAction(async (parseResult, cancellationToken) => await ReconcileCommand.RunAsync(
            parseResult.GetValue(repo),
            parseResult.GetValue(SocketOption),
            parseResult.GetValue(once),
            parseResult.GetValue(minInterval),
            parseResult.GetValue(maxInterval),
            parseResult.GetValue(cadenceWindow),
            parseResult.GetValue(cadenceDecay),
            parseResult.GetValue(backoff)));

        return command;
    }
}
