namespace Nightshift;

using Nightshift.Commands;

/// <summary>Entry dispatch for the <c>nightshift</c> agent/operator CLI.</summary>
public static class Cli
{
    private const string Usage = "usage: nightshift <next|check|release> ...";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string verb = args[0];
        string[] rest = args[1..];

        switch (verb)
        {
            case "next":
                return await NextCommand.RunAsync(rest);
            case "check":
                return await CheckCommand.RunAsync(rest);
            case "release":
                return await ReleaseCommand.RunAsync(rest);
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
}
