namespace Turnstile.Server;

using System.Net.Sockets;
using Turnstile.Storage;

/// <summary>
/// Chooses a transport for an <see cref="ITurnstile"/>: a live daemon (<see cref="RemoteStore"/>) if one
/// is listening, otherwise the file directly (<see cref="LocalStore"/>, library mode). Helpers and
/// controllers call this and never care which they got — the daemon is an opt-in for liveness, not a
/// prerequisite for the single-user helpers.
///
/// <para>Two failures do not survive the fallback. A <em>live watch</em>: a <see cref="LocalStore"/> rejects
/// <see cref="ITurnstile.WatchAsync"/> with a <see cref="TurnstileWatchUnavailableException"/> because its
/// change signal is process-local (#202). And the fallback <em>itself</em>: if a daemon exclusively owns the
/// database, opening it directly fails with a <see cref="TurnstileDatabaseInUseException"/> — a caller that
/// could not reach the socket must not open the file behind the daemon's live watch. Finite operations — get,
/// range, create, put, delete, txn, and leases — work daemonless unchanged whenever no daemon owns the file;
/// only a blocking watch (or a daemon-owned database) requires the daemon.</para>
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
