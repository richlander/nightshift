namespace Nightshift.Commands;

using Nightshift.Turnstile;

/// <summary>
/// Shared toggle for the operator control flags (<c>drain</c>/<c>stop</c>): setting a durable key raises
/// the flag for the whole shift; <c>--resume</c> clears it. Any value counts as "raised" — the workers
/// only test presence — so the flag is written as <c>"1"</c>.
/// </summary>
internal static class Control
{
    public static async Task<int> ToggleAsync(
        TurnstileClient client, string[] args, string key, string onLabel, string offLabel, CancellationToken ct)
        => await ToggleAsync(client, Array.IndexOf(args, "--resume") >= 0, key, onLabel, offLabel, ct);

    public static async Task<int> ToggleAsync(
        TurnstileClient client, bool resume, string key, string onLabel, string offLabel, CancellationToken ct)
    {
        if (resume)
        {
            await client.DeleteAsync(key, ct);
            Console.WriteLine(offLabel);
        }
        else
        {
            await client.SetAsync(key, "1", ct);
            Console.WriteLine(onLabel);
        }

        return ExitCode.Ok;
    }
}
