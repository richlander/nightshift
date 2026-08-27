namespace Octoshift;

using System.CommandLine;
using Octoshift.Commands;
using Octoshift.Waiting;

/// <summary>Entry dispatch for the <c>octoshift</c> GitHub-membrane CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: octoshift <reconcile|wait|watch|waiting|pr|fleet> ...";

    /// <summary>
    /// The global <c>--socket</c> override, inherited by every verb and passed through to the
    /// <c>nightshift</c> subprocesses so octoshift and the coordinator target the same Turnstile.
    /// </summary>
    private static readonly Option<string?> SocketOption = new("--socket")
    {
        Description = "Path to the Turnstile socket; passed through to nightshift subprocesses.",
        Recursive = true,
    };

    private static readonly HashSet<string> KnownVerbs = ["reconcile", "wait", "watch", "waiting", "pr", "fleet"];

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
        rootCommand.Subcommands.Add(CreateWaitingCommand());
        rootCommand.Subcommands.Add(CreatePrCommand());
        rootCommand.Subcommands.Add(CreateFleetCommand());
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

    private static Command CreateWaitingCommand()
    {
        var command = new Command("waiting", "Report stopped agent panes and what is actually blocking each one.");

        var all = new Option<bool>("--all") { Description = "Include windows that are holding legitimately, and windows that identify nothing." };
        Option<string[]> host = CreateHostOption();
        var json = new Option<bool>("--json") { Description = "Emit the rows as JSON instead of a table." };
        var rename = new Option<bool>("--rename") { Description = "Correct tmux window-name suffixes to match what the tool observes." };
        Option<string?> repo = CreateRepoOption();

        command.Options.Add(all);
        command.Options.Add(host);
        command.Options.Add(json);
        command.Options.Add(rename);
        command.Options.Add(repo);

        command.SetAction(async (parseResult, cancellationToken) => await WaitingCommand.RunAsync(
            parseResult.GetValue(repo),
            parseResult.GetValue(host) ?? [],
            parseResult.GetValue(all),
            parseResult.GetValue(json),
            parseResult.GetValue(rename),
            cancellationToken));

        return command;
    }

    private static Command CreatePrCommand()
    {
        var command = new Command("pr", "Locate a PR across the fleet and report what is happening to it.");

        var number = new Argument<int>("number") { Description = "The pull request number." };
        var host = CreateHostOption();
        var json = new Option<bool>("--json") { Description = "Emit the answer as JSON." };
        Option<string?> repo = CreateRepoOption();

        command.Arguments.Add(number);
        command.Options.Add(host);
        command.Options.Add(json);
        command.Options.Add(repo);

        command.SetAction(async (parseResult, cancellationToken) => await PrCommand.RunAsync(
            parseResult.GetValue(number),
            parseResult.GetValue(repo),
            parseResult.GetValue(host) ?? [],
            parseResult.GetValue(json),
            cancellationToken));

        return command;
    }

    private static Command CreateFleetCommand()
    {
        var command = new Command("fleet", "Show, add to, or retire from the declared fleet of targets that waiting and pr sweep.");

        // `octoshift fleet` with no subcommand lists the fleet — the reflex use — so the default action is
        // the list. `list` is also spellable explicitly for symmetry with `retire`.
        var listJson = new Option<bool>("--json") { Description = "Emit the fleet as JSON instead of a table." };
        command.Options.Add(listJson);
        command.SetAction(async (parseResult, cancellationToken) => await FleetCommand.RunListAsync(
            parseResult.GetValue(listJson),
            cancellationToken));

        var list = new Command("list", "List the declared fleet members.");
        var listSubJson = new Option<bool>("--json") { Description = "Emit the fleet as JSON instead of a table." };
        list.Options.Add(listSubJson);
        list.SetAction(async (parseResult, cancellationToken) => await FleetCommand.RunListAsync(
            parseResult.GetValue(listSubJson),
            cancellationToken));
        command.Subcommands.Add(list);

        var retire = new Command("retire", "Retire members from the declared fleet so sweeps stop expecting them.");
        Option<string[]> retireHost = CreateHostOption("Retire this host alias from the fleet; repeatable.");
        var retireLocal = new Option<bool>("--local") { Description = "Retire the local machine from the fleet." };
        var retireJson = new Option<bool>("--json") { Description = "Emit the result as JSON instead of a token line." };
        retire.Options.Add(retireHost);
        retire.Options.Add(retireLocal);
        retire.Options.Add(retireJson);
        retire.SetAction(async (parseResult, cancellationToken) => await FleetCommand.RunRetireAsync(
            parseResult.GetValue(retireHost) ?? [],
            parseResult.GetValue(retireLocal),
            parseResult.GetValue(retireJson),
            cancellationToken));
        command.Subcommands.Add(retire);

        var add = new Command("add", "Add members to the declared fleet — the way to (re-)declare a target, including the local machine after it has been retired.");
        Option<string[]> addHost = CreateHostOption("Add this host alias to the fleet; repeatable.");
        var addLocal = new Option<bool>("--local") { Description = "Add the local machine to the fleet." };
        var addJson = new Option<bool>("--json") { Description = "Emit the result as JSON instead of a token line." };
        add.Options.Add(addHost);
        add.Options.Add(addLocal);
        add.Options.Add(addJson);
        add.SetAction(async (parseResult, cancellationToken) => await FleetCommand.RunAddAsync(
            parseResult.GetValue(addHost) ?? [],
            parseResult.GetValue(addLocal),
            parseResult.GetValue(addJson),
            cancellationToken));
        command.Subcommands.Add(add);

        return command;
    }

    private static Option<string[]> CreateHostOption(
        string description = "Collect from this host over ssh; repeatable. Omit to read this machine's tmux.")
    {
        var host = new Option<string[]>("--host")
        {
            Description = description,
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };

        // Every value here becomes an ssh argument, so a value that is empty or option-shaped is rejected
        // at the parse rather than handed to ssh: `--host=-V` otherwise succeeds with no output and reads
        // as a quiet fleet, and bare `--host --json` otherwise swallows the flag as a hostname.
        host.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (HostTarget.Validate(token.Value) is { } error)
                {
                    result.AddError(error);
                    break;
                }
            }
        });

        return host;
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
