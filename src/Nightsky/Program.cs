using System.CommandLine;
using Nightsky;
using Nightsky.Commands;
using Nightsky.Output;

RootCommand root = CreateRootCommand();
ParseResult result = root.Parse(args);
if (result.Errors.Count > 0)
{
    foreach (var error in result.Errors)
    {
        Console.Error.WriteLine(error.Message);
    }

    return ExitCode.Usage;
}

return await result.InvokeAsync();

static RootCommand CreateRootCommand()
{
    var command = new RootCommand("nightsky read-only shift board");

    var socket = new Option<string?>("--socket")
    {
        Description = "Path to the Turnstile socket (overrides NIGHTSHIFT_SOCKET and TURNSTILE_SOCKET).",
        Recursive = true,
    };
    var all = new Option<bool>("--all", "-a") { Description = "Show all orders including landed (default hides landed)." };
    var watch = new Option<bool>("--watch", "-w") { Description = "Watch and redraw on live changes." };
    Option<OutputFormat> output = OutputFormatter.CreateOutputOption();
    var scope = new Argument<string?>("scope")
    {
        Description = "Optional scope (<plan> or <plan>/<order>).",
        Arity = ArgumentArity.ZeroOrOne,
    };

    command.Options.Add(socket);
    command.Options.Add(all);
    command.Options.Add(watch);
    command.Options.Add(output);
    command.Arguments.Add(scope);
    command.SetAction(async (parseResult, cancellationToken)
        => await BoardCommand.RunAsync(
            Paths.ResolveSocket(parseResult.GetValue(socket)),
            parseResult.GetValue(scope),
            parseResult.GetValue(watch),
            parseResult.GetValue(all),
            parseResult.GetValue(output)));

    return command;
}
