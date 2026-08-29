namespace Turnstile.Storage;

using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;

/// <summary>
/// The kv layer: a flat, revision-ordered key/value store with conditional single-key writes.
/// Reads run on short-lived pooled connections; every write funnels through the single-writer actor.
///
/// <para><b>Not a supported public surface.</b> This type is an implementation detail shared by the two
/// entry points that <em>do</em> take a <see cref="ModeLock"/> — <see cref="LocalStore"/> (shared) and the
/// daemon (exclusive). It is deliberately <c>internal</c>: <see cref="Open"/> performs no ownership locking,
/// so a caller reaching it directly would bypass the database-ownership contract (#202). External code uses
/// <see cref="LocalStore"/>/<c>ITurnstile</c> or the daemon socket; trusted product internals and tests reach
/// this through <c>InternalsVisibleTo</c>.</para>
/// </summary>
internal sealed class KvStore : IDisposable
{
    private readonly string _readConnectionString;
    private readonly WriteActor _writer;
    private readonly ChangeSignal _changed;

    /// <summary>
    /// Test seam (issue #197): invoked once inside <see cref="WatchAsync"/> after a catch-up batch's boundary
    /// snapshot has been taken but before the one-shot sync is yielded. A test sets it to commit a racing
    /// change in exactly the window the old split-snapshot order left open, proving the sync boundary excludes
    /// that change and a reconnect from it does not skip. Null (the default) in production.
    /// </summary>
    internal Func<Task>? OnCaughtUpBeforeSyncForTests { get; set; }

    /// <summary>Test-only: whether this store's writer (thread + connection) has been disposed. Lets a failed
    /// open be proven to have torn its writer down rather than leaking it (#202).</summary>
    internal bool IsWriterDisposedForTests => _writer.IsDisposed;

    private KvStore(string readConnectionString, WriteActor writer, ChangeSignal changed)
    {
        _readConnectionString = readConnectionString;
        _writer = writer;
        _changed = changed;
    }

    /// <summary>The highest committed revision, read from the durable <c>meta</c> counter that is advanced in
    /// the same transaction as the rows it counts — so it can never lag a committed row a reader can already
    /// see (status, range, and the watch one-shot sync all read through this).</summary>
    public long CurrentRevision
    {
        get
        {
            using var conn = new SqliteConnection(_readConnectionString);
            conn.Open();
            return CommittedRevision.Read(conn);
        }
    }

    /// <summary>Opens (creating if needed) the store at <paramref name="dbPath"/> in WAL mode. Internal: it
    /// performs no ownership locking, so every caller must first hold a <see cref="ModeLock"/> — a shared one
    /// for direct <see cref="LocalStore"/> use, an exclusive one for the daemon. Bypassing that (a raw open
    /// against a daemon-owned file) is exactly the contract violation #202 closes.</summary>
    internal static KvStore Open(string dbPath)
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

        var changed = new ChangeSignal();
        var writer = new WriteActor(writeConn, changed.Pulse);

        string readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = true,
        }.ConnectionString;

        return new KvStore(readConnectionString, writer, changed);
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
        => RangeSnapshot(prefix, limit, keysOnly).Items;

    /// <summary>
    /// Scans live keys under a prefix and returns them together with the durable committed revision that
    /// describes <em>exactly that snapshot</em>, both read in one explicit SQLite read transaction. Any caller
    /// that publishes a revision alongside range items must use this, so the two can never come from different
    /// snapshots — the split that let a range advertise revision N without the state at N (issue #197). The
    /// transaction spans only materialization: the rows are fully read before it ends, and nothing is held
    /// across an await.
    /// </summary>
    internal RangeReadResult RangeSnapshot(string prefix, int limit = 0, bool keysOnly = false)
        => RangeSnapshot(prefix, limit, keysOnly, afterBoundaryRead: null);

    /// <summary>
    /// Test-seam overload (issue #197): <paramref name="afterBoundaryRead"/> runs once inside the read
    /// transaction, after the boundary SELECT has established the snapshot but before the range rows are read.
    /// A test commits through the writer there to prove the committed change never enters this snapshot — the
    /// returned revision and items both predate it — which fails if the explicit transaction is removed or the
    /// two reads stop sharing it. Null (the default) in production; the WAL read snapshot lets the writer
    /// commit without deadlock.
    /// </summary>
    internal RangeReadResult RangeSnapshot(string prefix, int limit, bool keysOnly, Func<Task>? afterBoundaryRead)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction(deferred: true);
        long revision = CommittedRevision.Read(conn);
        RunAfterBoundarySeam(afterBoundaryRead);
        IReadOnlyList<KeyState> items = ReadRangeRows(conn, prefix, limit, keysOnly);
        tx.Commit();
        return new RangeReadResult(revision, items);
    }

    private static void RunAfterBoundarySeam(Func<Task>? seam)
    {
        if (seam is { } run)
        {
            run().GetAwaiter().GetResult();
        }
    }

    private static IReadOnlyList<KeyState> ReadRangeRows(SqliteConnection conn, string prefix, int limit, bool keysOnly)
    {
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

    // ---- watch ---------------------------------------------------------------------------------
    // Watch is why the store is log-structured: "everything after N" is WHERE id > N — resumable and
    // gapless. Reads use short-lived connections so a watcher never pins the WAL.

    /// <summary>
    /// Reads change-log rows with <c>id &gt; fromExclusive</c> under a prefix, in revision order.
    /// Every row is an event (a delete row is a delete; anything else is a put).
    /// </summary>
    public IReadOnlyList<WatchEvent> ReadEvents(string prefix, long fromExclusive, int limit)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        return ReadEventRows(conn, prefix, fromExclusive, limit, boundary: null);
    }

    /// <summary>
    /// Reads one batch of change-log events together with the committed boundary that batch was taken against,
    /// both in one explicit read transaction. The events are bounded by that boundary, so a batch that returns
    /// fewer than <paramref name="limit"/> rows has delivered <em>every</em> matching event with
    /// <c>id &lt;= Boundary</c> — which is exactly the condition under which a watcher may advertise a sync at
    /// <c>Boundary</c> without skipping an event. Reading the boundary and the events on separate snapshots is
    /// what let a sync advertise a revision whose event was never delivered (issue #197).
    /// </summary>
    internal EventBatch ReadEventBatch(string prefix, long fromExclusive, int limit)
        => ReadEventBatch(prefix, fromExclusive, limit, afterBoundaryRead: null);

    /// <summary>
    /// Test-seam overload (issue #197): <paramref name="afterBoundaryRead"/> runs once inside the read
    /// transaction, after the boundary SELECT has established the snapshot but before the events are read, so a
    /// test can prove a commit there never enters this snapshot — the boundary and events both predate it.
    /// Null in production.
    /// </summary>
    internal EventBatch ReadEventBatch(string prefix, long fromExclusive, int limit, Func<Task>? afterBoundaryRead)
    {
        using var conn = new SqliteConnection(_readConnectionString);
        conn.Open();
        using SqliteTransaction tx = conn.BeginTransaction(deferred: true);
        long boundary = CommittedRevision.Read(conn);
        RunAfterBoundarySeam(afterBoundaryRead);
        IReadOnlyList<WatchEvent> events = ReadEventRows(conn, prefix, fromExclusive, limit, boundary);
        tx.Commit();
        return new EventBatch(boundary, events);
    }

    private static IReadOnlyList<WatchEvent> ReadEventRows(
        SqliteConnection conn, string prefix, long fromExclusive, int limit, long? boundary)
    {
        string? end = Keys.PrefixEnd(prefix);
        using SqliteCommand cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT id, key, deleted, create_rev, lease, value, old_value, immutable
            FROM kv
            WHERE id > $from{(boundary is null ? string.Empty : " AND id <= $boundary")} AND key >= $start{(end is null ? string.Empty : " AND key < $end")}
            ORDER BY id
            {(limit > 0 ? "LIMIT $limit" : string.Empty)};
            """;
        cmd.Parameters.AddWithValue("$from", fromExclusive);
        cmd.Parameters.AddWithValue("$start", prefix);
        if (boundary is not null)
        {
            cmd.Parameters.AddWithValue("$boundary", boundary.Value);
        }

        if (end is not null)
        {
            cmd.Parameters.AddWithValue("$end", end);
        }

        if (limit > 0)
        {
            cmd.Parameters.AddWithValue("$limit", limit);
        }

        var events = new List<WatchEvent>();
        using SqliteDataReader reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            bool deleted = reader.GetInt64(2) != 0;
            events.Add(new WatchEvent(
                Revision: reader.GetInt64(0),
                Key: reader.GetString(1),
                Deleted: deleted,
                CreateRevision: reader.GetInt64(3),
                Lease: reader.IsDBNull(4) ? null : reader.GetString(4),
                Value: reader.IsDBNull(5) ? null : (byte[])reader[5],
                PrevValue: reader.IsDBNull(6) ? null : (byte[])reader[6])
            {
                // Immutable is the new key state's immutability; a delete is a tombstone and reports false.
                Immutable = !deleted && reader.GetInt64(7) != 0,
            });
        }

        return events;
    }

    /// <summary>
    /// Completes when the log next advances. Callers should capture this <em>before</em> draining so a
    /// commit that races the drain still wakes them — a pulse is never lost, only coalesced.
    /// </summary>
    public Task WaitForChangeAsync() => _changed.WaitAsync();

    /// <summary>
    /// Streams the change log under <paramref name="prefix"/> starting after <paramref name="fromExclusive"/>:
    /// the backlog as <see cref="WatchEventMessage"/>s, then a one-shot <see cref="WatchSyncMessage"/> when
    /// caught up, then live events as they commit. Runs until <paramref name="ct"/> is cancelled.
    /// </summary>
    /// <remarks>
    /// Two orderings make this sound (issue #197). The wake signal is captured <em>before</em> each drain, so a
    /// commit racing the drain still completes the captured task and wakes the next loop. And the sync boundary
    /// is the committed revision read in the <em>same snapshot</em> as the batch that caught up: a batch shorter
    /// than the page size has delivered every matching event with <c>id &lt;= Boundary</c>, so a sync at
    /// <c>Boundary</c> never advertises a revision whose event was not yet emitted. A commit landing after that
    /// final snapshot has <c>id &gt; Boundary</c>; the sync does not cover it and the pre-captured signal
    /// delivers it on the next loop, so it is never skipped.
    /// </remarks>
    public async IAsyncEnumerable<WatchMessage> WatchAsync(
        string prefix,
        long fromExclusive,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        long cursor = fromExclusive;
        bool synced = false;

        while (!ct.IsCancellationRequested)
        {
            Task changed = WaitForChangeAsync();

            while (true)
            {
                EventBatch batch = ReadEventBatch(prefix, cursor, 256);
                foreach (WatchEvent e in batch.Events)
                {
                    yield return new WatchEventMessage(e);
                    cursor = e.Revision;
                }

                if (batch.Events.Count < 256)
                {
                    // Caught up to this snapshot's boundary: every matching event <= Boundary has been emitted,
                    // so a one-shot sync at Boundary cannot skip one. Events past it wake the next loop.
                    if (!synced)
                    {
                        if (OnCaughtUpBeforeSyncForTests is { } hook)
                        {
                            await hook().ConfigureAwait(false);
                        }

                        yield return new WatchSyncMessage(batch.Boundary);
                        synced = true;
                    }

                    break;
                }
            }

            await changed.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Creates a key if absent. Returns <see cref="WriteStatus.Exists"/> if it is already live.</summary>
    public Task<WriteResult> CreateAsync(string key, byte[] value, bool immutable = false, string? lease = null)
    {
        Validate(key, value);

        // Reject the invalid combination up front — before the lease lookup or the writer enqueue — since both
        // effective values are known here. (InsertRow keeps the same guard as a defensive backstop.)
        if (immutable && lease is not null)
        {
            throw new TurnstileValidationException($"immutable key {key} cannot be attached to a lease");
        }

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

            // An explicit immutable+lease put is invalid input, rejected up front alongside key/value the same
            // way for both branches — before the writer is enqueued, so no transaction is even started. (A put
            // that only becomes immutable+lease by inheriting a live key's lease can't be seen without reading
            // state, so ApplyPut rejects that case before allocating a revision.)
            if (op.Kind is TxnOpKind.Put && op.Immutable && op.Lease is not null)
            {
                throw new TurnstileValidationException($"immutable key {op.Key} cannot be attached to a lease");
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

            // A write txn reports its own max allocated revision; a read-only/no-op txn reports the durable
            // committed revision (which, after compaction, can legitimately exceed MAX(kv.id)) read on this
            // same writer transaction snapshot — never MAX(kv.id), which would lag it.
            long revision = maxRev > 0 ? maxRev : CommittedRevision.Read(conn);
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
        if (latest is LatestRow live && !live.Deleted)
        {
            if (live.Immutable)
            {
                throw new TurnstileValidationException($"cannot modify immutable key {op.Key}");
            }

            // Compute the effective row (a put may inherit the live key's lease, or turn it immutable) and
            // reject an immutable+leased result before allocating a revision or staging a row.
            bool immutable = op.Immutable || live.Immutable;
            string? lease = op.Lease ?? live.Lease;
            if (immutable && lease is not null)
            {
                throw new TurnstileValidationException($"immutable key {op.Key} cannot be attached to a lease");
            }

            long updateId = next();
            InsertRow(conn, updateId, op.Key, created: false, deleted: false, immutable,
                live.CreateRev, prevRev: live.Id, lease, op.Value ?? [], oldValue: live.Value);
            return updateId;
        }

        // Create branch: the explicit immutable+lease combination was already rejected up front in TxnAsync,
        // but keep the check here so ApplyPut is correct independent of its caller.
        if (op.Immutable && op.Lease is not null)
        {
            throw new TurnstileValidationException($"immutable key {op.Key} cannot be attached to a lease");
        }

        long id = next();
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
        // An immutable key is never deleted. With immutable+lease rejected at write time (see InsertRow), no
        // validly-created immutable key is ever attached to a lease, so this branch is unreachable for current
        // data; it stays as a defensive guard so that even a legacy row written before that rule can never be
        // silently deleted by lease expiry.
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
        // Invariant: an immutable key can never carry a lease. A lease ending deletes every key attached to
        // it (see TombstoneAttachedKey), but an immutable key is never deleted — so the combination would
        // orphan the immutable key when its lease row is removed, contradicting the lease contract. This is
        // the one point every write converges (direct create, txn put, and a txn that flips a leased mutable
        // key to immutable), so rejecting it here closes every entry path at once. A rejected write throws
        // before its INSERT and the enclosing transaction rolls back, so no state changes and no revision is
        // consumed.
        if (immutable && lease is not null)
        {
            throw new TurnstileValidationException($"immutable key {key} cannot be attached to a lease");
        }

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
}

/// <summary>
/// A one-shot, self-rearming wake signal. Each <see cref="Pulse"/> completes the current waiters and
/// swaps in a fresh source, so waits are edge-triggered but a pulse landing between capture and await
/// is never lost — it simply completes the already-captured task.
/// </summary>
internal sealed class ChangeSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task WaitAsync() => Volatile.Read(ref _tcs).Task;

    public void Pulse()
        => Interlocked.Exchange(ref _tcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
}
