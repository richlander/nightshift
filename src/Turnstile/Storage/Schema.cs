namespace Turnstile.Storage;

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

        // Initialize the durable committed-revision singleton idempotently and atomically with the DDL: an
        // existing database adopts its MAX(kv.id); a fresh one starts at 0; a database that already carries
        // the key is left exactly as it is (OR IGNORE keeps the existing value — never a reset). This is the
        // migration for databases that predate the key — a one-time backfill, not a rewrite.
        using (SqliteCommand init = conn.CreateCommand())
        {
            init.Transaction = tx;
            init.CommandText = """
                INSERT OR IGNORE INTO meta (k, v)
                SELECT $k, CAST(COALESCE(MAX(id), 0) AS TEXT) FROM kv;
                """;
            init.Parameters.AddWithValue("$k", CommittedRevisionKey);
            init.ExecuteNonQuery();
        }

        tx.Commit();
    }
}
