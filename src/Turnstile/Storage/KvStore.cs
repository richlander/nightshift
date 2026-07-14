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
            if (lease is not null && !LeaseIsLive(conn, lease))
            {
                throw new TurnstileValidationException("lease not found or expired");
            }

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

    // ---- txn: single-key compare-and-swap ------------------------------------------------------
    // The hot path. An agent races other agents for one thing, and its whole world is one CAS:
    // compare create_revision == 0 (does not exist), and on success put the claim under its lease.
    // All compares are ANDed; the chosen branch runs atomically in one write transaction.

    /// <summary>
    /// Evaluates the compare clauses (ANDed) and atomically runs the success or failure branch.
    /// Put is an upsert; the compares are the only guard. Immutable keys reject put/delete.
    /// </summary>
    public Task<TxnResult> TxnAsync(
        IReadOnlyList<TxnCompare> compare,
        IReadOnlyList<TxnOp> success,
        IReadOnlyList<TxnOp> failure)
    {
        foreach (TxnCompare c in compare)
        {
            if (Keys.ValidateKey(c.Key) is string ck)
            {
                throw new TurnstileValidationException(ck);
            }
        }

        foreach (TxnOp op in success.Concat(failure))
        {
            if (Keys.ValidateKey(op.Key) is string ok)
            {
                throw new TurnstileValidationException(ok);
            }

            if (op.Kind is TxnOpKind.Put && Keys.ValidateValue(op.Value ?? []) is string ov)
            {
                throw new TurnstileValidationException(ov);
            }
        }

        return _writer.ExecuteAsync((conn, next) =>
        {
            bool succeeded = compare.All(c => EvalCompare(conn, c));
            IReadOnlyList<TxnOp> branch = succeeded ? success : failure;

            long maxRev = 0;
            var responses = new List<TxnOpResult>(branch.Count);
            foreach (TxnOp op in branch)
            {
                switch (op.Kind)
                {
                    case TxnOpKind.Put:
                        maxRev = ApplyPut(conn, next, op);
                        break;

                    case TxnOpKind.Delete:
                        if (ApplyDelete(conn, next, op) is long del)
                        {
                            maxRev = del;
                        }

                        break;

                    case TxnOpKind.Get:
                        LatestRow? latest = ReadLatest(conn, op.Key);
                        KeyState? state = latest is LatestRow row && !row.Deleted ? ToState(op.Key, row) : null;
                        responses.Add(new TxnOpResult(TxnOpKind.Get, op.Key, state));
                        break;
                }
            }

            long revision = maxRev > 0 ? maxRev : CurrentRev(conn);
            return new TxnResult(succeeded, revision, responses);
        });
    }

    private bool EvalCompare(SqliteConnection conn, TxnCompare c)
    {
        LatestRow? latest = ReadLatest(conn, c.Key);
        LatestRow? live = latest is LatestRow row && !row.Deleted ? row : null;
        switch (c.Target)
        {
            case TxnTarget.CreateRevision:
                return CompareLong(live?.CreateRev ?? 0, c.Op, c.Revision);

            case TxnTarget.ModRevision:
                return CompareLong(live?.Id ?? 0, c.Op, c.Revision);

            case TxnTarget.Value:
                bool valueEqual = BytesEqual(live?.Value, c.Value);
                return c.Op switch
                {
                    TxnCompareOp.Equal => valueEqual,
                    TxnCompareOp.NotEqual => !valueEqual,
                    _ => throw new TurnstileValidationException("value comparison supports only == and !="),
                };

            case TxnTarget.Lease:
                bool leaseEqual = string.Equals(live?.Lease, c.Lease, StringComparison.Ordinal);
                return c.Op switch
                {
                    TxnCompareOp.Equal => leaseEqual,
                    TxnCompareOp.NotEqual => !leaseEqual,
                    _ => throw new TurnstileValidationException("lease comparison supports only == and !="),
                };

            default:
                throw new TurnstileValidationException("unknown compare target");
        }
    }

    // Upsert: create if absent, overwrite if present. Immutable live keys are refused.
    private long ApplyPut(SqliteConnection conn, Func<long> next, TxnOp op)
    {
        if (op.Lease is not null && !LeaseIsLive(conn, op.Lease))
        {
            throw new TurnstileValidationException("lease not found or expired");
        }

        LatestRow? latest = ReadLatest(conn, op.Key);
        long id = next();
        if (latest is LatestRow live && !live.Deleted)
        {
            if (live.Immutable)
            {
                throw new TurnstileValidationException($"cannot modify immutable key {op.Key}");
            }

            InsertRow(conn, id, op.Key, created: false, deleted: false, immutable: op.Immutable || live.Immutable,
                live.CreateRev, prevRev: live.Id, op.Lease ?? live.Lease, op.Value ?? [], oldValue: live.Value);
            return id;
        }

        InsertRow(conn, id, op.Key, created: true, deleted: false, op.Immutable,
            createRev: id, prevRev: latest?.Id ?? 0, op.Lease, op.Value ?? [], oldValue: null);
        return id;
    }

    // Deleting an absent key is a no-op (returns null, allocating no revision).
    private static long? ApplyDelete(SqliteConnection conn, Func<long> next, TxnOp op)
    {
        LatestRow? latest = ReadLatest(conn, op.Key);
        if (latest is not LatestRow live || live.Deleted)
        {
            return null;
        }

        if (live.Immutable)
        {
            throw new TurnstileValidationException($"cannot delete immutable key {op.Key}");
        }

        long id = next();
        InsertRow(conn, id, op.Key, created: false, deleted: true, live.Immutable, live.CreateRev,
            prevRev: live.Id, lease: null, value: null, oldValue: live.Value);
        return id;
    }

    private static bool CompareLong(long actual, TxnCompareOp op, long expected) => op switch
    {
        TxnCompareOp.Equal => actual == expected,
        TxnCompareOp.NotEqual => actual != expected,
        TxnCompareOp.Less => actual < expected,
        TxnCompareOp.Greater => actual > expected,
        _ => false,
    };

    private static bool BytesEqual(byte[]? a, byte[]? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.AsSpan().SequenceEqual(b);
    }

    private static long CurrentRev(SqliteConnection conn) => ScalarLong(conn, "SELECT COALESCE(MAX(id), 0) FROM kv;");

    // ---- lease layer ---------------------------------------------------------------------------
    // A lease groups lifetime: on expiry or revoke, every attached key is deleted (a tombstone,
    // which is a delete event on the watch). Agent death = lease expiry = key deletion = event.

    /// <summary>Grants a new lease with the given TTL (seconds).</summary>
    public Task<LeaseInfo> CreateLeaseAsync(long ttlSecs)
    {
        if (ttlSecs <= 0)
        {
            throw new TurnstileValidationException("ttl must be a positive number of seconds");
        }

        string id = NewLeaseId();
        long expiresAt = Now() + ttlSecs;
        return _writer.ExecuteAsync<LeaseInfo>((conn, next) =>
        {
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO lease (id, ttl_secs, expires_at) VALUES ($id, $ttl, $exp);";
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$ttl", ttlSecs);
            cmd.Parameters.AddWithValue("$exp", expiresAt);
            cmd.ExecuteNonQuery();
            return new LeaseInfo(id, ttlSecs, expiresAt);
        });
    }

    /// <summary>
    /// Renews a lease. Returns the remaining TTL in seconds, or null if the lease is already gone —
    /// a keepalive that loses the race with the sweeper fails, and the client must stop, never re-acquire.
    /// </summary>
    public Task<long?> KeepAliveAsync(string id)
        => _writer.ExecuteAsync<long?>((conn, next) =>
        {
            if (ReadLease(conn, id) is not (long ttl, long exp) || exp <= Now())
            {
                return null;
            }

            long expiresAt = Now() + ttl;
            using SqliteCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE lease SET expires_at = $exp WHERE id = $id;";
            cmd.Parameters.AddWithValue("$exp", expiresAt);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.ExecuteNonQuery();
            return ttl;
        });

    /// <summary>Revokes a lease, deleting all attached keys. Returns false if the lease did not exist.</summary>
    public Task<bool> RevokeLeaseAsync(string id)
        => _writer.ExecuteAsync((conn, next) =>
        {
            if (ReadLease(conn, id) is null)
            {
                return false;
            }

            foreach (string key in AttachedKeys(conn, id))
            {
                TombstoneAttachedKey(conn, next, key);
            }

            DeleteLeaseRow(conn, id);
            return true;
        });

    /// <summary>Reads a lease's state and attached keys, or null if it does not exist.</summary>
    public LeaseView? GetLease(string id)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        if (ReadLease(conn, id) is not (long ttl, long exp))
        {
            return null;
        }

        long remaining = Math.Max(0, exp - Now());
        return new LeaseView(id, ttl, remaining, AttachedKeys(conn, id));
    }

    /// <summary>
    /// Deletes attached keys for every lease whose deadline has passed (server clock). Runs eagerly on
    /// the sweeper tick so expiry produces delete events — lazy expiry would be correct but silent.
    /// Returns the number of keys deleted.
    /// </summary>
    public Task<int> SweepExpiredAsync()
        => _writer.ExecuteAsync((conn, next) =>
        {
            long now = Now();
            var expired = new List<string>();
            using (SqliteCommand find = conn.CreateCommand())
            {
                find.CommandText = "SELECT id FROM lease WHERE expires_at <= $now;";
                find.Parameters.AddWithValue("$now", now);
                using SqliteDataReader reader = find.ExecuteReader();
                while (reader.Read())
                {
                    expired.Add(reader.GetString(0));
                }
            }

            int deleted = 0;
            foreach (string id in expired)
            {
                foreach (string key in AttachedKeys(conn, id))
                {
                    TombstoneAttachedKey(conn, next, key);
                    deleted++;
                }

                DeleteLeaseRow(conn, id);
            }

            return deleted;
        });

    private bool LeaseIsLive(SqliteConnection conn, string id)
        => ReadLease(conn, id) is (long _, long exp) && exp > Now();

    private static (long Ttl, long Exp)? ReadLease(SqliteConnection conn, string id)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ttl_secs, expires_at FROM lease WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        using SqliteDataReader reader = cmd.ExecuteReader();
        return reader.Read() ? (reader.GetInt64(0), reader.GetInt64(1)) : null;
    }

    private static List<string> AttachedKeys(SqliteConnection conn, string id)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT k.key FROM kv k
            JOIN (SELECT key, MAX(id) AS mid FROM kv GROUP BY key) m ON k.key = m.key AND k.id = m.mid
            WHERE k.deleted = 0 AND k.lease = $id
            ORDER BY k.key;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        var keys = new List<string>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }

    private static void TombstoneAttachedKey(SqliteConnection conn, Func<long> next, string key)
    {
        LatestRow? latest = ReadLatest(conn, key);
        // Immutable keys are never deleted, preserving the immutability invariant even under a lease.
        if (latest is not LatestRow live || live.Deleted || live.Immutable)
        {
            return;
        }

        long id = next();
        InsertRow(conn, id, key, created: false, deleted: true, live.Immutable, live.CreateRev, prevRev: live.Id, lease: null, value: null, oldValue: live.Value);
    }

    private static void DeleteLeaseRow(SqliteConnection conn, string id)
    {
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM lease WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private static string NewLeaseId() => Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

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
