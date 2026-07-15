namespace Nightshift;

using System.CommandLine;
using Nightshift.Commands;
using Nightshift.Output;

/// <summary>Entry dispatch for the <c>nightshift</c> agent/operator CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: nightshift <add|plan|land|join|standby|leave|next|show|recover|check|escalate|release|drain|stop|roster|where|watch> ...";

    private static readonly HashSet<string> KnownVerbs =
    [
        "add", "plan", "land", "join", "standby", "leave", "next", "show",
        "recover", "check", "escalate", "release", "drain", "stop", "roster", "where", "watch",
    ];

    /// <summary>Parses and invokes the command line, preserving Nightshift's exit-code contract.</summary>
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

    /// <summary>Builds the System.CommandLine command tree for every Nightshift verb.</summary>
    internal static RootCommand CreateRootCommand()
    {
        var rootCommand = new RootCommand("nightshift wire client");

        rootCommand.Subcommands.Add(CreateAddCommand());
        rootCommand.Subcommands.Add(CreatePlanCommand());
        rootCommand.Subcommands.Add(CreateLandCommand());
        rootCommand.Subcommands.Add(CreateNoArgsCommand("join", "Clock in on the roster.", JoinCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateNoArgsCommand("standby", "Stay on the roster but stop taking new work.", StandbyCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateNoArgsCommand("leave", "Clock out and release roster presence.", LeaveCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateNextCommand());
        rootCommand.Subcommands.Add(CreateShowCommand());
        rootCommand.Subcommands.Add(CreateNoArgsCommand("recover", "Re-attach to the order encoded by the current git branch.", RecoverCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateNoArgsCommand("check", "Renew the active claim lease and read directives.", CheckCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateEscalateCommand());
        rootCommand.Subcommands.Add(CreateReleaseCommand());
        rootCommand.Subcommands.Add(CreateToggleCommand("drain", "Stop handing out new work until resumed.", DrainCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateToggleCommand("stop", "Raise or clear the global halt flag.", StopCommand.RunAsync));
        rootCommand.Subcommands.Add(CreateRosterCommand());
        rootCommand.Subcommands.Add(CreateWhereCommand());
        rootCommand.Subcommands.Add(CreateWatchCommand());

        return rootCommand;
    }

    private static bool ShouldUseLegacyUsage(string[] args)
    {
        if (args.Length == 0)
        {
            return true;
        }

        // Any leading option (e.g. --help, -h, --version) or unknown token falls back to the
        // one-line usage/exit-2 contract; System.CommandLine's own help/version output stays
        // suppressed at the root so this migration adds no new success-path behavior.
        return args[0].StartsWith("-", StringComparison.Ordinal) || !KnownVerbs.Contains(args[0]);
    }

    private static Command CreateAddCommand()
    {
        var command = new Command("add", "Seed and reconcile a plan once.");
        var orders = new Argument<string?>("orders.json")
        {
            Description = "Plan file to seed.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var sha = new Option<string?>("--sha") { Description = "Commit SHA for the plan." };

        command.Arguments.Add(orders);
        command.Options.Add(sha);
        command.SetAction(async (parseResult, cancellationToken)
            => await AddCommand.RunAsync(parseResult.GetValue(orders), parseResult.GetValue(sha)));
        return command;
    }

    private static Command CreatePlanCommand()
    {
        var command = new Command("plan", "Seed a plan and keep ready work reconciled.");
        var orders = new Argument<string?>("orders.json")
        {
            Description = "Plan file to seed.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var plan = new Option<string?>("--plan") { Description = "Plan file to seed." };
        var sha = new Option<string?>("--sha") { Description = "Commit SHA for the plan." };

        command.Arguments.Add(orders);
        command.Options.Add(plan);
        command.Options.Add(sha);
        command.SetAction(async (parseResult, cancellationToken)
            => await PlanCommand.RunAsync(parseResult.GetValue(orders), parseResult.GetValue(plan), parseResult.GetValue(sha)));
        return command;
    }

    private static Command CreateLandCommand()
    {
        var command = new Command("land", "Mark an order as landed.");
        var orderBase = new Argument<string?>("order-base")
        {
            Description = "Order base path, e.g. /plan/1234/order/op4.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var reason = new Option<string?>("--reason") { Description = "Reason recorded with the landed state." };

        command.Arguments.Add(orderBase);
        command.Options.Add(reason);
        command.SetAction(async (parseResult, cancellationToken)
            => await LandCommand.RunAsync(parseResult.GetValue(orderBase), parseResult.GetValue(reason)));
        return command;
    }

    private static Command CreateNextCommand()
    {
        var command = new Command("next", "Claim the next available order of work.");
        var scope = new Argument<string?>("scope")
        {
            Description = "Ready-set scope to scan.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var timeout = new Option<int>("--timeout") { Description = "Seconds to wait for work." };
        timeout.DefaultValueFactory = _ => 60;

        command.Arguments.Add(scope);
        command.Options.Add(timeout);
        command.SetAction(async (parseResult, cancellationToken)
            => await NextCommand.RunAsync(parseResult.GetValue(scope), parseResult.GetValue(timeout)));
        return command;
    }

    private static Command CreateEscalateCommand()
    {
        var command = new Command("escalate", "Escalate the active order for judgment.");
        var reason = new Option<string?>("--reason") { Description = "What needs judgment." };

        command.Options.Add(reason);
        command.SetAction(async (parseResult, cancellationToken)
            => await EscalateCommand.RunAsync(parseResult.GetValue(reason)));
        return command;
    }

    private static Command CreateReleaseCommand()
    {
        var command = new Command("release", "Release the active order with an outcome.");
        var status = new Option<string?>("--status") { Description = "Outcome: done, blocked, declined, escalated, or refused." };
        var reason = new Option<string?>("--reason") { Description = "Optional reason for the outcome." };

        command.Options.Add(status);
        command.Options.Add(reason);
        command.SetAction(async (parseResult, cancellationToken)
            => await ReleaseCommand.RunAsync(parseResult.GetValue(status), parseResult.GetValue(reason)));
        return command;
    }

    private static Command CreateToggleCommand(string name, string description, Func<bool, Task<int>> runAsync)
    {
        var command = new Command(name, description);
        var resume = new Option<bool>("--resume") { Description = "Clear the control flag instead of setting it." };

        command.Options.Add(resume);
        command.SetAction(async (parseResult, cancellationToken)
            => await runAsync(parseResult.GetValue(resume)));
        return command;
    }

    private static Command CreateNoArgsCommand(string name, string description, Func<string[], Task<int>> runAsync)
    {
        var command = new Command(name, description);
        command.SetAction(async (parseResult, cancellationToken) => await runAsync([]));
        return command;
    }

    private static Command CreateWhereCommand()
    {
        var command = new Command("where", "List claimed or reported orders.");
        Option<OutputFormat> output = OutputFormatter.CreateOutputOption();

        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken)
            => await WhereCommand.RunAsync(parseResult.GetValue(output)));
        return command;
    }

    private static Command CreateRosterCommand()
    {
        var command = new Command("roster", "List workers on duty.");
        Option<OutputFormat> output = OutputFormatter.CreateOutputOption();

        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken)
            => await RosterCommand.RunAsync(parseResult.GetValue(output)));
        return command;
    }

    private static Command CreateShowCommand()
    {
        var command = new Command("show", "Reprint the current claim's WORK packet.");
        Option<OutputFormat> output = OutputFormatter.CreateOutputOption();

        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken)
            => await ShowCommand.RunAsync(parseResult.GetValue(output)));
        return command;
    }

    private static Command CreateWatchCommand()
    {
        var command = new Command("watch", "Follow the coordinator board live.");
        Option<OutputFormat> output = OutputFormatter.CreateWatchOutputOption();

        command.Options.Add(output);
        command.SetAction(async (parseResult, cancellationToken)
            => await WatchCommand.RunAsync(parseResult.GetValue(output)));
        return command;
    }
}
