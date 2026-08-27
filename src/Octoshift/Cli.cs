namespace Octoshift;

using System.CommandLine;
using Octoshift.Commands;
using Octoshift.Waiting;

/// <summary>Entry dispatch for the <c>octoshift</c> GitHub-membrane CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: octoshift <waiting|pr|fleet> ...";

    private static readonly HashSet<string> KnownVerbs = ["waiting", "pr", "fleet"];

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

    private static Command CreateWaitingCommand()
    {
        var command = new Command("waiting", "Report stopped agent panes and what is actually blocking each one.");

        var all = new Option<bool>("--all") { Description = "Include windows that are holding legitimately, and windows that identify nothing." };
        Option<string[]> host = CreateHostOption();
        var json = new Option<bool>("--json") { Description = "Emit the rows as JSON instead of a table." };
        Option<string[]> repo = CreateRepoOption();

        command.Options.Add(all);
        command.Options.Add(host);
        command.Options.Add(json);
        command.Options.Add(repo);

        command.SetAction(async (parseResult, cancellationToken) => await WaitingCommand.RunAsync(
            parseResult.GetValue(repo) ?? [],
            parseResult.GetValue(host) ?? [],
            parseResult.GetValue(all),
            parseResult.GetValue(json),
            cancellationToken));

        return command;
    }

    private static Command CreatePrCommand()
    {
        var command = new Command("pr", "Locate a PR across the fleet and report what is happening to it.");

        var number = new Argument<int>("number") { Description = "The pull request number." };
        var host = CreateHostOption();
        var json = new Option<bool>("--json") { Description = "Emit the answer as JSON." };
        Option<string[]> repo = CreateRepoOption();

        command.Arguments.Add(number);
        command.Options.Add(host);
        command.Options.Add(json);
        command.Options.Add(repo);

        command.SetAction(async (parseResult, cancellationToken) => await PrCommand.RunAsync(
            parseResult.GetValue(number),
            parseResult.GetValue(repo) ?? [],
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

    private static Option<string[]> CreateRepoOption()
    {
        var repo = new Option<string[]>("--repo")
        {
            Description = "Repository scope owner/name; repeatable to search several repos. Inferred from the git remote when omitted.",
            Arity = ArgumentArity.OneOrMore,
            AllowMultipleArgumentsPerToken = false,
        };

        // A malformed --repo cannot be silently dropped: it would narrow the scope the operator asked for
        // and could turn a real collision into a false unique, so it is a usage error at the parser, the
        // same as an option-shaped --host.
        repo.Validators.Add(result =>
        {
            foreach (var token in result.Tokens)
            {
                if (RepoScope.Validate(token.Value) is { } error)
                {
                    result.AddError(error);
                    break;
                }
            }
        });

        return repo;
    }
}
