namespace Turnstile.Storage;

using Microsoft.Data.Sqlite;

/// <summary>
/// The kv layer: a flat, revision-ordered key/value store with conditional single-key writes.
/// Reads run on short-lived pooled connections; every write funnels through the single-writer actor.
/// </summary>
public sealed class KvStore : IDisposable
{
    private readonly string _readConnectionString;
    private readonly WriteActor _writer;

    private KvStore(string readConnectionString, WriteActor writer)
    {
        _readConnectionString = readConnectionString;
        _writer = writer;
    }

    /// <summary>The highest committed revision.</summary>
    public long CurrentRevision => _writer.Revision;

    /// <summary>Opens (creating if needed) the store at <paramref name="dbPath"/> in WAL mode.</summary>
    public static KvStore Open(string dbPath)
    {
        string writeConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ConnectionString;

        var writeConn = new SqliteConnection(writeConnectionString);
        writeConn.Open();
        Execute(writeConn, "PRAGMA journal_mode=WAL;");
        Execute(writeConn, "PRAGMA synchronous=NORMAL;");
        Execute(writeConn, "PRAGMA busy_timeout=5000;");
        Schema.Ensure(writeConn);

        long startRevision = ScalarLong(writeConn, "SELECT COALESCE(MAX(id), 0) FROM kv;");
        var writer = new WriteActor(writeConn, startRevision);

        string readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
        }.ConnectionString;

        return new KvStore(readConnectionString, writer);
    }

    /// <summary>Returns the live state of a key, or null if it does not exist.</summary>
    public KeyState? Get(string key)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        LatestRow? latest = ReadLatest(conn, key);
        return latest is LatestRow row && !row.Deleted ? ToState(key, row) : null;
    }

    /// <summary>Scans live keys under a prefix in lexicographic order.</summary>
    public IReadOnlyList<KeyState> Range(string prefix, int limit = 0, bool keysOnly = false)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();

        using SqliteCommand cmd = conn.CreateCommand();
        string? end = Keys.PrefixEnd(prefix);
        string bound = end is null ? string.Empty : " AND k.key < $end";
        cmd.CommandText = $"""
            SELECT k.key, k.id, k.create_rev, k.lease, k.immutable, {(keysOnly ? "NULL" : "k.value")} AS value
            FROM kv k
            JOIN (SELECT key, MAX(id) AS mid FROM kv WHERE key >= $start{(end is null ? string.Empty : " AND key < $end")} GROUP BY key) m
              ON k.key = m.key AND k.id = m.mid
            WHERE k.deleted = 0 AND k.key >= $start{bound}
            ORDER BY k.key
            {(limit > 0 ? "LIMIT $limit" : string.Empty)};
            """;
        cmd.Parameters.AddWithValue("$start", prefix);
        if (end is not null)
        {
            cmd.Parameters.AddWithValue("$end", end);
        }

        if (limit > 0)
        {
            cmd.Parameters.AddWithValue("$limit", limit);
        }

        var results = new List<KeyState>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            string key = reader.GetString(0);
            long modRev = reader.GetInt64(1);
            long createRev = reader.GetInt64(2);
            string? lease = reader.IsDBNull(3) ? null : reader.GetString(3);
            bool immutable = reader.GetInt64(4) != 0;
            byte[]? value = reader.IsDBNull(5) ? null : (byte[])reader[5];
            results.Add(new KeyState(key, createRev, modRev, lease, immutable, value));
        }

        return results;
    }

    /// <summary>Creates a key if absent. Returns <see cref="WriteStatus.Exists"/> if it is already live.</summary>
    public Task<WriteResult> CreateAsync(string key, byte[] value, bool immutable = false, string? lease = null)
    {
        Validate(key, value);
        return _writer.ExecuteAsync((conn, next) =>
        {
            LatestRow? latest = ReadLatest(conn, key);
            if (latest is LatestRow live && !live.Deleted)
            {
                return new WriteResult(WriteStatus.Exists, live.Id, ToState(key, live));
            }

            long id = next();
            InsertRow(conn, id, key, created: true, deleted: false, immutable, createRev: id, prevRev: latest?.Id ?? 0, lease, value, oldValue: null);
            return new WriteResult(WriteStatus.Created, id, null);
        });
    }

    /// <summary>Updates a key. Requires an If-Match revision unless <paramref name="unconditional"/> is set.</summary>
    public Task<WriteResult> UpdateAsync(string key, byte[] value, long? ifMatch, bool unconditional = false)
    {
        Validate(key, value);
        return _writer.ExecuteAsync((conn, next) =>
        {
            LatestRow? latest = ReadLatest(conn, key);
            if (latest is not LatestRow live || live.Deleted)
            {
                return new WriteResult(WriteStatus.NotFound, 0, null);
            }

            if (live.Immutable)
            {
                return new WriteResult(WriteStatus.Immutable, live.Id, ToState(key, live));
            }

            WriteResult? precondition = CheckPrecondition(key, live, ifMatch, unconditional);
            if (precondition is not null)
            {
                return precondition;
            }

            long id = next();
            InsertRow(conn, id, key, created: false, deleted: false, live.Immutable, live.CreateRev, prevRev: live.Id, live.Lease, value, oldValue: live.Value);
            return new WriteResult(WriteStatus.Ok, id, null);
        });
    }

    /// <summary>Deletes a key by writing a tombstone. Requires an If-Match revision unless unconditional.</summary>
    public Task<WriteResult> DeleteAsync(string key, long? ifMatch, bool unconditional = false)
    {
        if (Keys.ValidateKey(key) is string err)
        {
            throw new TurnstileValidationException(err);
        }

        return _writer.ExecuteAsync((conn, next) =>
        {
            LatestRow? latest = ReadLatest(conn, key);
            if (latest is not LatestRow live || live.Deleted)
            {
                return new WriteResult(WriteStatus.NotFound, 0, null);
            }

            if (live.Immutable)
            {
                return new WriteResult(WriteStatus.Immutable, live.Id, ToState(key, live));
            }

            WriteResult? precondition = CheckPrecondition(key, live, ifMatch, unconditional);
            if (precondition is not null)
            {
                return precondition;
            }

            long id = next();
            InsertRow(conn, id, key, created: false, deleted: true, live.Immutable, live.CreateRev, prevRev: live.Id, lease: null, value: null, oldValue: live.Value);
            return new WriteResult(WriteStatus.Deleted, id, null);
        });
    }

    public void Dispose() => _writer.Dispose();

    private static WriteResult? CheckPrecondition(string key, LatestRow live, long? ifMatch, bool unconditional)
    {
        if (unconditional)
        {
            return null;
        }

        if (ifMatch is null)
        {
            return new WriteResult(WriteStatus.PreconditionRequired, live.Id, ToState(key, live));
        }

        if (ifMatch.Value != live.Id)
        {
            return new WriteResult(WriteStatus.PreconditionFailed, live.Id, ToState(key, live));
        }

        return null;
    }

    private static void Validate(string key, byte[] value)
    {
        if ((Keys.ValidateKey(key) ?? Keys.ValidateValue(value)) is string err)
        {
            throw new TurnstileValidationException(err);
        }
    }

    private static LatestRow? ReadLatest(SqliteConnection conn, string key)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, deleted, immutable, create_rev, lease, value FROM kv WHERE key = $key ORDER BY id DESC LIMIT 1;";
        cmd.Parameters.AddWithValue("$key", key);
        using SqliteDataReader reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new LatestRow(
            Id: reader.GetInt64(0),
            Deleted: reader.GetInt64(1) != 0,
            Immutable: reader.GetInt64(2) != 0,
            CreateRev: reader.GetInt64(3),
            Lease: reader.IsDBNull(4) ? null : reader.GetString(4),
            Value: reader.IsDBNull(5) ? null : (byte[])reader[5]);
    }

    private static void InsertRow(
        SqliteConnection conn, long id, string key, bool created, bool deleted, bool immutable,
        long createRev, long prevRev, string? lease, byte[]? value, byte[]? oldValue)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO kv (id, key, created, deleted, immutable, create_rev, prev_rev, lease, value, old_value)
            VALUES ($id, $key, $created, $deleted, $immutable, $create_rev, $prev_rev, $lease, $value, $old_value);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$created", created ? 1 : 0);
        cmd.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        cmd.Parameters.AddWithValue("$immutable", immutable ? 1 : 0);
        cmd.Parameters.AddWithValue("$create_rev", createRev);
        cmd.Parameters.AddWithValue("$prev_rev", prevRev);
        cmd.Parameters.AddWithValue("$lease", (object?)lease ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$value", (object?)value ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$old_value", (object?)oldValue ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static KeyState ToState(string key, LatestRow row)
        => new(key, row.CreateRev, row.Id, row.Lease, row.Immutable, row.Value);

    private static void Execute(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }
}
