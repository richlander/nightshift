namespace Octoshift;

using System.CommandLine;
using Octoshift.Commands;

/// <summary>Entry dispatch for the <c>octoshift</c> GitHub-membrane CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: octoshift <reconcile|wait|watch> ...";

    /// <summary>
    /// The global <c>--socket</c> override, inherited by every verb and passed through to the
    /// <c>nightshift</c> subprocesses so octoshift and the coordinator target the same Turnstile.
    /// </summary>
    private static readonly Option<string?> SocketOption = new("--socket")
    {
        Description = "Path to the Turnstile socket; passed through to nightshift subprocesses.",
        Recursive = true,
    };

    private static readonly HashSet<string> KnownVerbs = ["reconcile", "wait", "watch"];

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
        rootCommand.Subcommands.Add(CreateWaitCommand());
        rootCommand.Subcommands.Add(CreateWatchCommand());
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
        Option<string?> repo = CreateRepoOption();
        PollOptions poll = CreatePollOptions();

        command.Options.Add(once);
        command.Options.Add(repo);
        poll.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) => await ReconcileCommand.RunAsync(
            parseResult.GetValue(repo),
            parseResult.GetValue(SocketOption),
            parseResult.GetValue(once),
            parseResult.GetValue(poll.MinInterval),
            parseResult.GetValue(poll.MaxInterval),
            parseResult.GetValue(poll.CadenceWindow),
            parseResult.GetValue(poll.CadenceDecay),
            parseResult.GetValue(poll.Backoff)));

        return command;
    }

    private static Command CreateWaitCommand()
    {
        var command = new Command("wait", "Block until a PR in scope resolves (merged, closed, or conflicting).");
        var scope = new Argument<string>("scope") { Description = "Plan or order scope (e.g. /plan/3 or /plan/3/order/op1)." };
        var all = new Option<bool>("--all") { Description = "Wait for the whole observed set in scope to resolve." };
        Option<string?> repo = CreateRepoOption();
        PollOptions poll = CreatePollOptions();

        command.Arguments.Add(scope);
        command.Options.Add(all);
        command.Options.Add(repo);
        poll.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) => await ObserveCommand.RunWaitAsync(
            parseResult.GetValue(scope)!,
            parseResult.GetValue(repo),
            parseResult.GetValue(all),
            parseResult.GetValue(poll.MinInterval),
            parseResult.GetValue(poll.MaxInterval),
            parseResult.GetValue(poll.CadenceWindow),
            parseResult.GetValue(poll.CadenceDecay),
            parseResult.GetValue(poll.Backoff)));

        return command;
    }

    private static Command CreateWatchCommand()
    {
        var command = new Command("watch", "Stream PR state transitions in scope until interrupted.");
        var scope = new Argument<string>("scope") { Description = "Plan or order scope (e.g. /plan/3 or /plan/3/order/op1)." };
        Option<string?> repo = CreateRepoOption();
        PollOptions poll = CreatePollOptions();

        command.Arguments.Add(scope);
        command.Options.Add(repo);
        poll.AddTo(command);

        command.SetAction(async (parseResult, cancellationToken) => await ObserveCommand.RunWatchAsync(
            parseResult.GetValue(scope)!,
            parseResult.GetValue(repo),
            parseResult.GetValue(poll.MinInterval),
            parseResult.GetValue(poll.MaxInterval),
            parseResult.GetValue(poll.CadenceWindow),
            parseResult.GetValue(poll.CadenceDecay),
            parseResult.GetValue(poll.Backoff)));

        return command;
    }

    private static Option<string?> CreateRepoOption()
        => new("--repo") { Description = "Repository scope owner/name; inferred from the git remote when omitted." };

    private static PollOptions CreatePollOptions()
    {
        return new PollOptions(
            new Option<int?>("--min-interval") { Description = "Absolute floor on the poll interval in seconds (default 60)." },
            new Option<int?>("--max-interval") { Description = "Absolute ceiling on the poll interval in seconds (default 600)." },
            new Option<int?>("--cadence-window") { Description = "How many recent merges the cadence EWMA averages (default 10)." },
            new Option<double?>("--cadence-decay") { Description = "Cadence EWMA decay in (0,1]; higher weights recent gaps more (default 0.3)." },
            new Option<double?>("--backoff") { Description = "Multiplicative backoff factor for idle polls (default 2)." });
    }

    private readonly record struct PollOptions(
        Option<int?> MinInterval,
        Option<int?> MaxInterval,
        Option<int?> CadenceWindow,
        Option<double?> CadenceDecay,
        Option<double?> Backoff)
    {
        public void AddTo(Command command)
        {
            command.Options.Add(MinInterval);
            command.Options.Add(MaxInterval);
            command.Options.Add(CadenceWindow);
            command.Options.Add(CadenceDecay);
            command.Options.Add(Backoff);
        }
    }
}
