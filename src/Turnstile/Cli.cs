namespace Turnstile;

using Turnstile.Server;

/// <summary>Entry dispatch: <c>turnstile serve</c> runs the daemon; everything else is a thin client.</summary>
public static class Cli
{
    private const string Usage = "usage: turnstile <serve|get|create|put|delete|watch|lease|status> ...";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        string verb = args[0];
        string[] rest = args[1..];

        return verb switch
        {
            "serve" => await ServeAsync(rest),
            _ => await Client.RunAsync(verb, rest),
        };
    }

    private static async Task<int> ServeAsync(string[] args)
    {
        string socket = OptionValue(args, "--socket") ?? Paths.DefaultSocket;
        string db = OptionValue(args, "--db") ?? Paths.DefaultDb;

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            return await Daemon.RunAsync(socket, db, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
    }

    /// <summary>Returns the value following <paramref name="name"/> in <paramref name="args"/>, or null.</summary>
    internal static string? OptionValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    internal static bool HasFlag(string[] args, string name) => Array.IndexOf(args, name) >= 0;
}
