namespace Turnstile.Storage;

using Microsoft.Data.Sqlite;

/// <summary>The log-structured schema (spec §4). kine's model: a single append-only revision table.</summary>
internal static class Schema
{
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
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = Ddl;
        cmd.ExecuteNonQuery();
    }
}
