namespace Turnstile.Storage;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>
/// The single strict reader of the durable <c>meta.committed_revision</c> counter, shared by the write actor
/// (which reads it under <c>BEGIN IMMEDIATE</c> as each transaction's allocation base) and by the read paths
/// (status, range, watch sync). It fails visibly on a missing or malformed value rather than guessing — a
/// corrupt counter is a bug to surface, not to paper over by re-deriving a number that could rewind the log.
/// One reader, one parse: there is no second, permissive path to drift from.
/// </summary>
internal static class CommittedRevision
{
    /// <summary>
    /// Reads the durable committed revision on <paramref name="conn"/>. When called inside an open
    /// transaction it reads that transaction's snapshot; the write actor calls it under <c>BEGIN IMMEDIATE</c>
    /// so the value is the latest committed revision across every connection and process.
    /// </summary>
    public static long Read(SqliteConnection conn)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT v FROM meta WHERE k = $k;";
        cmd.Parameters.AddWithValue("$k", Schema.CommittedRevisionKey);
        object? raw = cmd.ExecuteScalar();
        if (raw is null or DBNull)
        {
            throw new InvalidOperationException(
                $"turnstile: the '{Schema.CommittedRevisionKey}' meta row is missing; refusing to guess the revision.");
        }

        string text = (string)raw;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long revision))
        {
            throw new InvalidOperationException(
                $"turnstile: the committed-revision meta value '{text}' is not a valid non-negative integer.");
        }

        return revision;
    }
}
