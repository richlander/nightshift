namespace Turnstile.Server;

using System.Net.Sockets;
using Turnstile.Storage;

/// <summary>
/// Chooses a transport for an <see cref="ITurnstile"/>: a live daemon (<see cref="RemoteStore"/>) if one
/// is listening, otherwise the file directly (<see cref="LocalStore"/>, library mode). Helpers and
/// controllers call this and never care which they got — the daemon is an opt-in for liveness, not a
/// prerequisite for the single-user helpers.
/// </summary>
public static class TurnstileConnection
{
    /// <summary>
    /// Returns a <see cref="RemoteStore"/> if a daemon answers on <paramref name="socketPath"/>, else a
    /// <see cref="LocalStore"/> opened on <paramref name="dbPath"/> (which sweeps expired leases on open).
    /// </summary>
    public static async Task<ITurnstile> ConnectAsync(string? socketPath = null, string? dbPath = null, CancellationToken ct = default)
    {
        socketPath ??= Paths.DefaultSocket;
        dbPath ??= Paths.DefaultDb;

        if (File.Exists(socketPath))
        {
            RemoteStore remote = RemoteStore.Connect(socketPath);
            try
            {
                // A socket file can be stale after a crash; a probe confirms someone is actually listening.
                await remote.GetRevisionAsync(ct);
                return remote;
            }
            catch (HttpRequestException ex) when (ex.InnerException is SocketException)
            {
                remote.Dispose();
            }
        }

        return await LocalStore.OpenAsync(dbPath);
    }
}
