namespace Turnstile.Storage;

using System.Globalization;
using Microsoft.Data.Sqlite;

/// <summary>The log-structured schema (spec §4). kine's model: a single append-only revision table.</summary>
internal static class Schema
{
    /// <summary>The <c>meta</c> key holding the durable committed revision — the external source of truth,
    /// advanced in the same transaction as the <c>kv</c> rows it counts so a reader never sees one without the
    /// other.</summary>
    public const string CommittedRevisionKey = "committed_revision";

    public const string Ddl = """
        CREATE TABLE IF NOT EXISTS kv (
          id           INTEGER PRIMARY KEY,          -- THE REVISION (assigned by the write actor)
          key          TEXT    NOT NULL,
          created      INTEGER NOT NULL,             -- 1 if this row created the key
          deleted      INTEGER NOT NULL,             -- 1 if tombstone
          immutable    INTEGER NOT NULL DEFAULT 0,
          create_rev   INTEGER NOT NULL,
          prev_rev     INTEGER NOT NULL,             -- previous revision of this key, 0 if none
          lease        TEXT,                         -- NULL = none; 128-bit unguessable hex otherwise
          value        BLOB,
          old_value    BLOB
        );

        CREATE INDEX IF NOT EXISTS kv_key_id ON kv(key, id DESC);
        CREATE INDEX IF NOT EXISTS kv_id     ON kv(id);

        CREATE TABLE IF NOT EXISTS lease (
          id           TEXT PRIMARY KEY,             -- 128-bit unguessable hex, NOT sequential
          ttl_secs     INTEGER NOT NULL,
          expires_at   INTEGER NOT NULL              -- unix seconds, SERVER clock
        );

        CREATE TABLE IF NOT EXISTS meta (k TEXT PRIMARY KEY, v TEXT);
        """;

    public static void Ensure(SqliteConnection conn)
    {
        using SqliteTransaction tx = conn.BeginTransaction();

        using (SqliteCommand ddl = conn.CreateCommand())
        {
            ddl.Transaction = tx;
            ddl.CommandText = Ddl;
            ddl.ExecuteNonQuery();
        }

        ReconcileCommittedRevision(conn, tx);

        tx.Commit();
    }

    /// <summary>
    /// Reconciles the durable <c>committed_revision</c> singleton with the log, atomically with the DDL:
    /// <list type="bullet">
    /// <item>Absent (a database written before the key existed): backfill it from <c>MAX(kv.id)</c>.</item>
    /// <item>Present but below <c>MAX(kv.id)</c> (a stale counter): repair it upward to <c>MAX(kv.id)</c>, or
    /// the next write would reuse a visible id and the reported revision would sit below a visible row.</item>
    /// <item>Present at or above <c>MAX(kv.id)</c>: leave it. A value above the surviving max is legitimate
    /// after compaction or history retention removes rows.</item>
    /// <item>Malformed / negative / out-of-range: fail visibly rather than silently reset a corrupt counter.</item>
    /// </list>
    /// </summary>
    private static void ReconcileCommittedRevision(SqliteConnection conn, SqliteTransaction tx)
    {
        long maxId = MaxKvId(conn, tx);
        string? existing = ReadCommittedRevisionText(conn, tx);

        if (existing is null)
        {
            SetCommittedRevision(conn, tx, maxId, insert: true);
            return;
        }

        if (!long.TryParse(existing, NumberStyles.None, CultureInfo.InvariantCulture, out long committed))
        {
            throw new InvalidOperationException(
                $"turnstile: the committed-revision meta value '{existing}' is not a valid non-negative integer.");
        }

        if (committed < maxId)
        {
            SetCommittedRevision(conn, tx, maxId, insert: false);
        }
    }

    private static long MaxKvId(SqliteConnection conn, SqliteTransaction tx)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT COALESCE(MAX(id), 0) FROM kv;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static string? ReadCommittedRevisionText(SqliteConnection conn, SqliteTransaction tx)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT v FROM meta WHERE k = $k;";
        cmd.Parameters.AddWithValue("$k", CommittedRevisionKey);
        object? raw = cmd.ExecuteScalar();
        return raw is null or DBNull ? null : (string)raw;
    }

    private static void SetCommittedRevision(SqliteConnection conn, SqliteTransaction tx, long value, bool insert)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = insert
            ? "INSERT INTO meta (k, v) VALUES ($k, $v);"
            : "UPDATE meta SET v = $v WHERE k = $k;";
        cmd.Parameters.AddWithValue("$k", CommittedRevisionKey);
        cmd.Parameters.AddWithValue("$v", value.ToString(CultureInfo.InvariantCulture));
        cmd.ExecuteNonQuery();
    }
}
