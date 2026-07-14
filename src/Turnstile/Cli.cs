namespace Turnstile;

/// <summary>Entry dispatch: <c>turnstile serve</c> runs the daemon; everything else is a thin client.</summary>
public static class Cli
{
    public static Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: turnstile <serve|get|create|put|delete|watch|lease|status> ...");
            return Task.FromResult(2);
        }

        return Task.FromResult(0);
    }
}
