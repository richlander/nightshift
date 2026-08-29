# Turnstile

**A coordination store: kv, lease, txn, watch.**
*One at a time, and it keeps count.*

*Engineering spec, v0.2 — build against this.*

---

## 0. What this is

A **flat, revision-ordered key/value store with compare-and-swap, leases, and watch.** Modeled on the etcd v3 data model, backed by SQLite in the log-structured style of [kine](https://github.com/k3s-io/kine). Single writer of record. No consensus.

The category is Chubby, ZooKeeper, etcd, Consul. The gap it fills: **coordination primitives without the cluster.** Nobody stands up etcd for two machines and a laptop. That's the product.

### Turnstile knows nothing about Nightshift

Nightshift is the first consumer. It is not the only possible one, and the kernel must not know it exists. No orders, no slices, no DAGs, no agents, no PRs, no git, no GitHub. Every key name in the Nightshift design is a **convention used by tools**. Turnstile sees byte strings.

The name is not decoration. A turnstile admits **one at a time**, it **counts**, and **it has no opinion about who you are** — authorization is somebody else's job. That is the kernel: mutual exclusion, a monotonic log, and zero credentials.

**The discipline test, for every future feature:**

> *Would this make sense in a Turnstile that had never heard of Nightshift?*
> **No → it's a controller.**

That survives a Tuesday afternoon better than any list of rules.

### Non-negotiables

| | |
|---|---|
| **No consensus.** | One writer of record. No Raft, no replication, no leader election. |
| **No business logic.** | If it knows what a DAG is, the design has failed. |
| **No egress.** | Turnstile never opens an outbound connection. Assert it in a test. |
| **No credentials.** | No tokens, no PATs, no SSH keys, no `gh`. Nothing to compromise. |
| **No process spawning.** | It does not exec anything. |
| **Turnstile is watched; it never calls out.** | No plugins, no hooks, no callbacks. Tools poll or stream. |

---

## 1. The four layers

| Layer | Concern | Question it answers |
|---|---|---|
| **kv** | data | what is |
| **lease** | ownership, temporal | who holds it, for how long |
| **txn** | consistency | who wins |
| **watch** | notification | who finds out |

The layers **stack; they do not call each other.** That is the test that the decomposition is right:

- **kv** doesn't know what a lease *is* — it has a `lease` column, nothing more.
- **lease** doesn't know about txn — it deletes keys; it doesn't care how they got there.
- **txn** doesn't know about watch — it writes rows; the log does the rest.
- **watch** doesn't know about anything — it tails the log.

The payoff: **watch is derived, not implemented.** Lease expiry emits events *for free*, because expiry is a delete, a delete is a row, and watch is rows. **Nobody wires the sweeper to the notification system.** There is nothing to wire.

> **Txn is how a writer acquires. Watch is how a reader learns. Lease is what connects them — because a lease expiring is a write nobody made, and it is the event that matters most.**

**Watch shapes the other three.** kv/lease/txn could be built on any table. Watch is what *forces* the log-structured design — the append-only revision table exists so "everything after N" is `WHERE id > N`, resumable and gapless. Build the store first and bolt watch on later, and you will rebuild the store.

**Design for watch on day one, even before anything reads it.**

---

## 2. Data model

### Flat keyspace, lexicographic order

Keys are byte strings. **There is no hierarchy.** `/order/1234/op/0005/claim` is one opaque key that happens to sort near its siblings. Slashes are convention; the only thing they buy is *sorting*.

A prefix query is sugar for a range scan:

```
prefix "/order/1234/"  ⇒  Range(start = "/order/1234/", end = "/order/12340")
```

Consequences, all intentional:

- **Nothing cascades.** Deleting a parent does not delete children. A tool writes `Range` → `DeleteRange` in a `Txn` if it wants one. (Usually it doesn't — **lease does this better.**)
- **No referential integrity.** You can write `/order/9999/...` for a nonexistent order. Validation is a controller's job.
- **Prefix scan is the only query.** No secondary indexes, no `WHERE`. **Any question must be expressible as a prefix** — so a secondary index is *a second keyspace, written by a controller*.

### ⚠ Lexicographic order is not numeric order

```
/order/1234/op/10   sorts BEFORE   /order/1234/op/5     because '1' < '5'
```

**Zero-pad every numeric key component** (`op/0005`, `op/0010`), or use fixed-width hex. Turnstile does not enforce this — it's a convention tools must honor, and it must be shouted in the tool-author docs. Retrofitting once tools depend on the layout is miserable.

### Revisions

A **global, strictly monotonic revision** on every mutation. Never repeats, never goes backward, increments by exactly one.

Per key:

- **`create_revision`** — revision at which the key was created. **`0` means the key does not exist.**
- **`mod_revision`** — revision of the key's most recent mutation.

> **`mod_revision` is the fencing token.** Globally monotonic, kernel-issued, no counter to maintain. A tool presents the `mod_revision` it saw; a stale one is rejected. Zombies cannot land.

### Value constraints

- **Keys:** begin with `/`. No whitespace, control chars, or `..`. Max 512 bytes.
- **Values:** opaque bytes; JSON by convention. **Max 64 KiB.** A large value means the design is wrong — put a pointer in the store and the payload in git.

**One key = one thing that one writer mutates.** A fat blob rewritten by many writers reintroduces read-modify-write contention, which is the bug being deleted.

---

## 3. Key classes

Not a kernel feature — a **design vocabulary** — but it determines what the API must enforce.

| Class | Example | Create | Update | Delete | Needs CAS? |
|---|---|---|---|---|---|
| **immutable** | `slice/a/spec` | if-absent + `?immutable` | **impossible** | impossible | n/a |
| **contended** | `slice/a/claim`, `/cap/merge` | **if-absent only** | **If-Match only** | **If-Match only** | **yes** |
| **owned** | `slice/a/state`, `/ready/*` | free | **blind is correct** | free | **no** |
| **ephemeral** | `/agent/dev-b` | free + lease | free | **by lease** | no |

**Only one class needs mandatory CAS.**

> **Immutable and ephemeral are mutually exclusive.** An immutable key cannot be attached to a lease.
> Immutability means the key is *never* deleted; a lease exists precisely to *delete* what it holds when the
> holder dies. The two contracts contradict — an immutable leased key would outlive its lease as an orphan —
> so the combination is rejected at write time (`400`), on every path that could create it (see §6). This
> constrains *new* writes; it is not a migration. A row written before the rule that is both immutable and
> leased keeps its immutability and so may outlive lease removal — see the lease-deletion note in §6.

A controller reconciling its own derived state is the *sole* authority. There is no one to race. CAS there is pure ceremony. **Where there's no race, CAS is noise. Where there is one, it's mandatory. Where there shouldn't be a writer at all, the key is immutable.**

So the question is never "is this key transactional." It's **who is allowed to write this, and is there anyone to race with.**

---

## 4. Storage

Log-structured, single append-only table. This is kine's model; copy it closely.

```sql
CREATE TABLE kv (
  id           INTEGER PRIMARY KEY AUTOINCREMENT,  -- THE REVISION
  key          TEXT    NOT NULL,
  created      INTEGER NOT NULL,                   -- 1 if this row created the key
  deleted      INTEGER NOT NULL,                   -- 1 if tombstone
  immutable    INTEGER NOT NULL DEFAULT 0,
  create_rev   INTEGER NOT NULL,
  prev_rev     INTEGER NOT NULL,                   -- previous revision of this key, 0 if none
  lease        INTEGER NOT NULL DEFAULT 0,         -- 0 = none
  value        BLOB,
  old_value    BLOB
);

CREATE INDEX kv_key_id ON kv(key, id DESC);
CREATE INDEX kv_id     ON kv(id);

CREATE TABLE lease (
  id           INTEGER PRIMARY KEY,                -- RANDOM 128-bit, NOT sequential
  ttl_secs     INTEGER NOT NULL,
  expires_at   INTEGER NOT NULL                    -- unix seconds, SERVER clock
);

CREATE TABLE meta (k TEXT PRIMARY KEY, v TEXT);    -- committed_revision, compact_revision
```

`meta` holds two durable singletons keyed by name: `committed_revision`, the highest committed revision,
advanced **in the same transaction** as the `kv` rows it counts (so a reader — status, range, or the watch
one-shot sync — can never report below a committed row it can already see, and a rolled-back write neither
advances nor exposes it); and `compact_revision` (below). `committed_revision` is the external source of
truth for the current revision — the single-writer actor keeps a private in-memory copy only as its
allocation base. On open it is reconciled with the log in the schema transaction: backfilled from
`MAX(kv.id)` when absent (a database written before the key existed), repaired **upward** to `MAX(kv.id)` if a
stored value has fallen below it (so it never sits under a visible row), and left untouched when at or above
it — a value above the surviving max is legitimate after compaction. A malformed or negative value fails
loudly rather than resetting a corrupt counter.

**Three properties fall out, and they are the reason for this shape:**

| | |
|---|---|
| **Watch = `WHERE id > N ORDER BY id`** | Resumable. No missed events, no duplicate ambiguity, no delivery semantics to reason about. |
| **Current state = max(`id`) per key where `deleted = 0`** | A materialized view over the log. |
| **The event log is free** | It *is* the storage. There is no second system. |

And the property the tools above depend on: **level-triggered reconciliation is the default.** A crashed controller asks *"what is the state now?"*, not *"what did I miss?"* This must be true, or a single dropped event wedges a shift permanently and nobody can tell why.

### Concurrency

- **SQLite WAL.**
- **All writes serialized through one connection** (single-writer actor). Do not scatter `BEGIN IMMEDIATE` discipline across the codebase — funnel writes through one place.
- Reads may be concurrent.
- **Watch fan-out must not hold an open read transaction** — it pins the WAL and blocks checkpointing. Notify subscribers in-process from the write actor, with the log as the source of truth.

### Compaction — not optional

The log grows without bound. k3s users have grown 8 GB SQLite files, then found compaction itself couldn't finish.

- **`compact(rev)`**: delete rows with `id < rev` *for keys that have a newer row*. **Always retain the latest row per live key.**
- Store `compact_revision` in `meta`. Run hourly, retain 24h. Expose `POST /compact`.
- `auto_vacuum=INCREMENTAL`, or `VACUUM` on a schedule.

---

## 5. API

**HTTP/JSON over a Unix domain socket.** Optionally TCP bound to the Tailscale interface with a bearer token.

**Explicitly NOT gRPC.** etcd and kine use it for protocol fidelity; we should not. The goal is *tools in any language, written by humans or agents in an afternoon.* gRPC means codegen, a toolchain, a barrier. HTTP/JSON means:

```bash
curl --unix-socket /run/turnstile.sock -N 'localhost/watch?prefix=/esc/'
```

**A watch is a `curl -N`.** A shell-native, backgroundable long-running process — exactly the mechanism verified to work with Claude Code's `run_in_background` and Copilot's background bash. The agent-facing watch is an HTTP stream, and it works *because the protocol is boring*.

### Conditional is the default

**Narrow the verbs; don't annotate the keys.** ZooKeeper's `create()` fails if the node exists — there is no unconditional form. Kine already discovered that Kubernetes only ever uses etcd's general `Txn` for Create/Update/Delete, and pattern-matches accordingly. **The general transaction was a superset nobody needed.**

And HTTP already standardized the answer: **`428 Precondition Required`** (RFC 6585) exists precisely for *"the server requires this request to be conditional"*, motivated by the lost-update problem.

```
POST   /kv/{key}                       CREATE. 409 if exists. No unconditional form.
PUT    /kv/{key}   If-Match: <rev>     UPDATE. 428 if header absent. 412 if stale.
DELETE /kv/{key}   If-Match: <rev>     DELETE. 428 if absent, 412 if stale.
PUT    /kv/{key}?unconditional         blind write — explicit, ugly, visible in code review.
```

**This matters because your tools will be written by agents**, and a model reaches for a blind `PUT` every time — it's simpler and it's what the training data does. A silent lost-update race in a controller is exactly the bug that surfaces at 3am and takes a week to find.

A lint that fires loudly at development time, against a class of bug your authors systematically produce, is worth a lot. It cannot prove correctness — the store can force you to *supply* a precondition, but it cannot know which one is *right*. **It's a lint, not a proof.** Take it anyway.

### Endpoints

```
GET    /kv/{key}                          → value, create_revision, mod_revision, lease
GET    /kv?prefix=P[&limit=N][&keys_only] → range scan, lexicographic order
POST   /kv/{key}         [?lease=L] [?immutable]     create (lease and immutable are mutually exclusive → 400)
PUT    /kv/{key}         If-Match | ?unconditional   update
DELETE /kv/{key}         If-Match
POST   /txn                               general CAS — multi-key
POST   /lease            {"ttl": 2700}    → {"id": "<128-bit random>"}
PUT    /lease/{id}                        keepalive → {"ttl_remaining": 2700}
DELETE /lease/{id}                        revoke (deletes all attached keys)
GET    /lease/{id}                        ttl_remaining, attached keys
GET    /watch?prefix=P&from=R             SSE stream
POST   /compact          {"revision": R}
GET    /status                            current_revision, compact_revision, db size, uptime
```

### `/txn` — and the distinction that matters

There are **two different things** both called Txn:

| | **Conditional single-key write** | **Multi-key atomic write** |
|---|---|---|
| **Purpose** | I win the race, or I don't | These change together, or not at all |
| **Frequency** | **hot path** — every claim, every renew | rare |
| **Callers** | **agents** | **controllers** |
| **Uses** | claim, renew, capability acquire, fenced write | claim + reverse index; ready-frontier update |

> **The agent uses Txn to win. The controller uses Txn to not lie.**
>
> An agent races other agents for *one* thing — its whole world is one CAS.
> A controller is the sole writer of a derived view, so it never races — but its view must be **coherent**, so it writes atomically.

**Get single-key conditional write exactly right.** That is the hot path, that is where the 64-thread race test lives, and everything above depends on it. General `/txn` is called by two audited controllers.

*If you shipped only conditional single-key writes, you'd have a working system with a small correctness gap in the plan controller. That's a legitimate v0.*

```jsonc
POST /txn
{
  "compare": [
    { "key": "/order/1234/op/0005/slice/a/claim",
      "target": "create_revision",     // create_revision | mod_revision | value | lease
      "op": "==", "value": 0 }
  ],
  "success": [
    { "op": "put", "key": "/order/1234/op/0005/slice/a/claim",
      "value": {"agent":"dev-b","host":"merritt","attempt":1},
      "lease": "0x9f3c..." }
  ],
  "failure": [ { "op": "get", "key": "/order/1234/op/0005/slice/a/claim" } ]
}

→ { "succeeded": true, "revision": 4211, "responses": [...] }
```

All `compare` clauses ANDed. The chosen branch executes atomically in one SQLite transaction. **`create_revision == 0` means "does not exist" — that is the entire claim protocol.**

### `/watch` — SSE

```
GET /watch?prefix=/ready/&from=4200

event: put
data: {"key":"/ready/1234/0005/a","value":{},"create_revision":4201,"mod_revision":4201,"immutable":false}

event: delete
data: {"key":"/order/1234/op/0005/slice/a/claim","prev_value":{...},"mod_revision":4202}

event: sync
data: {"revision":4202}

: heartbeat
```

1. Stream all events with `id > from` matching the prefix, **in revision order**.
2. Emit `sync` with the current revision once the backlog drains. **The client now knows it is caught up.** This is what makes level-triggered reconciliation implementable.
3. Stream live events.
4. **Heartbeat every 30s** so proxies don't time out and a watcher can tell "quiet" from "dead."

**Watching from a compacted revision returns `410 Gone`** with the current `compact_revision`. The client MUST re-`Range` and resume. This is etcd's behavior and it is **load-bearing** — it *forces* controllers to be level-triggered instead of silently drifting. It is not an error case; it is the mechanism.

**Watch liveness is daemon-only (#202).** The daemon owns the change signal every watcher parks on, so a watch is a capability of the daemon transport, not of the file. The direct-SQLite path (`LocalStore`, library mode) has only a process-local signal — it can never be woken by another process's commit to the same file — so it does **not** return a watch that could park forever; it fails the call up front with a typed `TurnstileWatchUnavailableException`. This narrows the contract rather than adding shared notification: finite reads, writes, txns and leases stay fully available daemonless (and safe alongside other *direct* writers, #199), but a live watch — a contended `lock`/`elect`, a `queue pop --wait`, or `turnstile watch` — requires a running `turnstile serve`, which surfaces as the unavailable exit code and a first-line error rather than a silent hang.

**Rejecting the direct watch is necessary but not sufficient; a database-ownership lock completes it (#202).** Refusing to hand out a direct watch stops a caller parking on a dead signal, but it does nothing about the *other* half: a direct `LocalStore` write to a file a daemon is watching still commits and advances the global revision while pulsing only its own process-local signal — the daemon's watcher, parked on a different signal, never wakes and silently misses the event. So liveness also requires that a direct writer and a daemon can never hold the same file at once. Turnstile enforces this with a cross-process advisory lock (`ModeLock`, a Unix `flock` on a stable `<db>-modelock` sidecar, released automatically on process exit):

- A `LocalStore` takes a **shared** lock for its lifetime, so any number of direct readers/writers still coexist over one file with no daemon (#199 preserved).
- A daemon takes an **exclusive** lock for its full lifetime, so it is the sole writer-of-record while it runs — every commit flows through the one `KvStore` whose signal its watchers park on, and its watch is genuinely live.
- The two are mutually exclusive. Opening a `LocalStore` against a daemon-owned file fails immediately with a typed `TurnstileDatabaseInUseException` (a `TurnstileUnavailableException`) telling the caller to use the socket; starting a daemon while any `LocalStore` is open fails the same way rather than serving with a watch it cannot keep live. Both surface as the unavailable exit code and a first-line `turnstile:` error, no stack trace. The lock is acquired *before* the SQLite file or the socket is touched, and disposed after the store/daemon is fully done, so a startup failure or a crash releases it cleanly. Malicious host-local tampering with the sidecar is out of scope — this is coordination correctness, not security containment.
- The lock is keyed on the database's **canonical** filesystem identity, not its spelling. Both entry points resolve the path once through `realpath(3)` (`DatabasePath.Canonicalize`) — following every intermediate *and* final symlink — and hand the same canonical string to both the `ModeLock` and `KvStore.Open`. A lexical path (`Path.GetFullPath`) would not do: it never follows a symlink, so an ordinary alias of one file — a symlinked database file, or a database under a symlinked directory — would take a `<db>-modelock` sidecar beside a *different* name for the same inode, letting a direct writer commit behind a daemon's live watch. **Supported alias contract:** any symlink aliasing (final component, intermediate directory, or both) of the same file resolves to the same lock identity; a not-yet-created database resolves its existing parent directory and appends the filename, so parent-directory aliases converge before creation; a *dangling* final-component symlink (its target absent) is refused with a visible error rather than given an identity that could differ from the file SQLite would open. Out of scope, unchanged: hardlink aliasing (invisible to `realpath`) and an adversary swapping a path component after resolution — that is host-local tampering, not coordination correctness.

**The canonical controller loop** — put this in the tool-author docs:

```
loop:
  state, R = GET /kv?prefix=P          # returns current_revision
  reconcile(state)
  watch(prefix=P, from=R):
     on event -> reconcile(...)
     on 410   -> goto loop             # recompacted; re-list
     on close -> goto loop
```

### Auth

- **Unix socket:** filesystem permissions. Mode `0600`. That's v1.
- **TCP (cross-machine):** bind to the Tailscale interface only. **Bearer token.** No TLS — Tailscale provides transport encryption; adding TLS is complexity for no gain on a tailnet.
- The token grants **full access.** No per-key ACLs. If you need them later, that's a proxy tool, not kernel surface.

---

## 6. Lease — the unification

**This is the most important idea in Turnstile.**

A lease has a TTL. Keys may be **attached** to it. On expiry or revoke, **all attached keys are deleted** — which writes tombstones, which **emit `delete` events on the watch.** No *validly written* key is exempt: immutable+lease is rejected at write time (§3), so a current database has no immutable leased key to strand. One caveat for a legacy database: a row written before that rule that is both immutable and leased is intentionally **skipped** by deletion (an immutable key is never deleted) and so survives its lease. That is a pre-existing row, not something a new write can produce, and it is left as-is rather than migrated.

Therefore:

> **Holder death = lease expiry = key deletion = a watch event = a controller reacts.**
>
> **One mechanism. No special case.**

A hung process, a crash, an OOM, a machine reboot — **all identical to the kernel.** Keepalive stops; the attached keys vanish; a watcher sees the delete and reacts.

**There is no dead-process detector anywhere in the system. There is a lease.**

### Lease groups lifetime. Txn groups mutation.

Different axes, and conflating them is the mistake:

- *"a, b, c must **die** together"* → **one lease.**
- *"a, b, c must **change** together"* → **Txn.**

And a smell worth naming: **if a, b, c must always be mutated together by the same writer, they should be one key.** The only reason to split keys is to avoid contention between *different* writers. Same writer, always atomic, no race → you split for nothing and bought a transaction you didn't need.

**Attach anything that should die with the holder to its lease** — the claim, the registry entry, the reverse index. They vanish together. **Leases are the garbage collector.**

### Implementation notes

- **Expiry is evaluated at the server**, on the server's clock, always. Clock skew between laptop and desktop must never decide who owns an operation.
- **Deadlines are stored in whole server seconds, truncated — and fixed before the write lands.** `expires_at` is computed as `floor(now) + ttl` at the moment the request is prepared, where `floor(now)` drops the fraction of the current second already elapsed. Truncation alone puts the deadline in **`(ttl − 1, ttl]`** ahead of *that* instant. But the deadline is frozen *before* the insert is handed to the single serialized writer, so the writer-queue wait, the write itself, and the response transit all elapse against a clock that is already counting down: under contention the lifetime a caller can actually use once its response returns is shorter than `(ttl − 1, ttl]`, and a sufficiently delayed grant can return already expired. This is the current storage contract; a round-up or higher-resolution deadline would be a future change, if a sub-second-sensitive caller ever needs one. Neither number a client sees is a promise of that much wall-clock life left: the requested `ttl` (wire `ttl`, `LeaseInfo.TtlSecs`) is the value asked for, and `ttl_remaining` is `max(0, expires_at − floor(now))`, a whole-second difference that over-reports by up to the elapsed sub-second remainder. A successful keepalive resets the deadline to `floor(now) + ttl` and returns the requested `ttl`.
- **Renew against `ttl − 1`, not `ttl`.** A holder that sets its keepalive cadence from the nominal TTL — renew every `ttl/2`, the obvious choice — is timing against up to a full second more lease than it holds. At `ttl = 2` the deadline starts at most 2s out (less once the queue and response delay from the previous bullet are counted), so after waiting the nominal `ttl/2 = 1s` the remaining margin is at most about a second: absent other delay it lies in `(0, 1]` and can be nearly zero, and under writer contention it has no positive lower bound — the renewal can arrive after the lease has already expired. Short TTLs give no dependable renewal window: keep TTLs comfortably above your renewal interval (tens of seconds or more), and treat very short TTLs as unreliable.
- **The sweeper runs eagerly** (~1s tick), not lazily on read. Lazy expiry is *correct* but produces no event — and the event is the entire point.
- **Keepalive racing expiry must be deterministic.** The server decides. A keepalive arriving after the sweeper has run **fails** with `410 Gone`. The client must treat this as *"I have lost my claim"* and **stop** — never silently re-acquire.

---

## 7. Lifecycle

`turnstile` is **two programs wearing one name** — the tmux/docker/git pattern.

```
turnstile serve      the daemon. long-lived. weeks.
turnstile <else>     a thin client. one socket write. exits.
```

The first invocation auto-starts the daemon. After that, nobody thinks about it.

### Everything expensive is hot, in the daemon, permanently

| | Where | Cost |
|---|---|---|
| SQLite connection + WAL | daemon | opened once |
| Prepared statements | daemon | compiled once |
| Write-serialization actor | daemon | always warm |
| Lease sweeper | daemon | ~1s tick |
| Watch subscriber table | daemon | in memory |

The client is **deliberately cold**, because it has nothing worth keeping warm. Socket write, `printf`, exit.

### The client's cost is `exec()`, and it does not matter

- Native AOT startup: **~1–5 ms**
- Socket round trip: **~0.1–0.5 ms**
- A full call: **~2–6 ms, dominated by process startup**

> **Every call sits inside an agent turn that costs 1–10 *seconds*. The exec overhead is three orders of magnitude below the model latency it's embedded in.** Optimizing it is not premature — it's unmeasurable.

### Call volume is trivial

Each client issues only a handful of calls per unit of work — an acquire, a few conditional writes and renews, a release. Even a busy fleet is **low hundreds of calls per active cycle, low thousands over a work day.** SQLite does that in seconds of wall clock and idles the rest of the time.

Plus **one long-lived SSE stream per interactive client** and **one per controller.** Those are connections, not calls — cheap to hold, and they're what makes polling unnecessary.

### The design consequence

> **Because the daemon holds all state and the CLI holds none, restarting the CLI cannot leave a local cache out of sync.**

Callers supply keys and lease handles explicitly on each invocation. Identity and lifecycle are caller policy; Turnstile does not infer them from `cwd`, a worktree, or any other local state.

**A stateful client would be a client that could get out of sync. A stateless one can't.**

### Where cost actually shows up

Not the CLI. Two places:

1. **Watch fan-out.** Do not hold an open SQLite read transaction per subscriber — it pins the WAL, blocks checkpointing, and presents as a mystery slowdown.
2. **Compaction.** Not a hot-path cost but a **cliff.** Schedule it, monitor it, don't discover it at 3am.

---

## 8. Correctness invariants

The things the tests exist to prove.

1. **Revisions are strictly monotonic, gapless, never reused** — across restarts and crashes.
2. **Conditional writes are linearizable.** N concurrent claimants of one key ⇒ exactly one succeeds.
3. **No lost events.** A watcher from revision `R` sees *every* matching mutation with `id > R`, exactly once, in revision order.
4. **No phantom events.** A watcher never sees a rolled-back mutation.
5. **Lease expiry emits exactly one delete event per attached key.** Every *validly attached* key is deletable, because immutable+lease is rejected at write time (invariant 11); a legacy immutable+leased row is the one exception and is intentionally skipped rather than deleted (§6).
6. **Compaction never destroys the latest live row for any key.**
7. **A watch from a compacted revision fails loudly (`410`)** rather than silently skipping history.
8. **Crash recovery is clean.** `kill -9` mid-write leaves no torn state; the revision counter resumes correctly.
9. **Immutable keys cannot be mutated by anyone, ever.**
10. **Turnstile opens no outbound sockets.**
11. **Immutable and lease are mutually exclusive for new writes.** Creating or attaching a key that is both immutable and leased — via create, txn put, or making a leased key immutable — is rejected (`400`) before any state change, so no *newly written* immutable key can outlive its lease. This is not retroactive: a row written before the rule that is both immutable and leased keeps its immutability and may survive lease removal (§6), and is not migrated.

---

## 9. Tests — where to focus

Priority order. **The first four are where the bugs will actually be.**

### P0 — concurrency and the log

- **Claim race.** 64 threads race to create the same key. Exactly one `201`. Repeat 10k times. **This is the whole product; if it's wrong, nothing else matters.**
- **Revision monotonicity under concurrent writers.** No gaps, no reuse, strict +1. Assert after every mutation.
- **Watch completeness.** 100k mutations across 1k keys while a watcher streams from rev 0. Event count and order must match the log exactly. **Then rerun with the watcher killed and resumed at random points — no gaps at any resume point.**
- **Crash recovery.** `kill -9` under write load, in a loop. Restart. Consistent DB, correct revision resume, no duplicates.

### P1 — leases

- **Expiry emits deletes.** 100 keys on one lease. Let it expire. Watcher sees exactly 100 delete events, once each.
- **Keepalive/expiry race.** KeepAlive within ±10 ms of expiry. Outcome deterministic; the client must be able to tell which happened. Fuzz the timing.
- **Revoke is atomic.** A concurrent reader sees all attached keys or none — never a partial set.
- **Leases survive kernel restart** if not yet expired (TTLs are absolute and persisted).

### P2 — compaction and boundaries

- Compact while a watcher streams from a to-be-compacted revision → **`410`, never a silent gap.**
- Compact never removes the live row for a key with no newer revision.
- Compact under concurrent write load.
- **Lexicographic ordering.** Assert correct order for zero-padded keys — **and assert the *wrong* order for unpadded ones**, so the trap is documented in the suite.

### P3 — protocol

- Malformed keys rejected (no leading `/`, control chars, `..`, over-length).
- Oversized values rejected.
- `PUT` without `If-Match` → **428**. With a stale one → **412**.
- Immutable key mutation → **409**, always.
- Immutable + lease (create, txn put, or making a leased key immutable) → **400**, on every path, before any state change.
- SSE survives a slow consumer without unbounded server-side buffering.

### If you have the appetite

**Deterministic simulation.** Drive Turnstile from a seeded scheduler interleaving writers, watchers, the lease sweeper, and compaction; assert §8 after every step. The lost-event-under-a-compaction/expiry-interleave class of bug is nearly impossible to find otherwise and **will** bite you at 3am.

---

## 10. POC demos

Each proves one primitive, is independently useful, and is small. Together they prove the thesis: **coordination is a store plus recipes.**

**Write at least two in a language that is not C#. That is the demo.**

### `turnstile-lock` (~50 lines)

```bash
turnstile-lock /lock/merge-patrol --ttl 30m -- ./patrol.sh
```

Acquires, runs, releases. **If the process dies, the lease expires and the lock frees.**

**Proves:** conditional create + lease + keepalive + auto-release.
**Useful alone:** this *by itself* replaces *"tell one agent to be the merge watcher and wait 30 minutes so two don't do it at once."* Ship it standalone.

### `turnstile-queue` (~120 lines) — **the one that sells it**

```bash
turnstile-queue add  /q/build '{"task":"port benchmarks"}'
turnstile-queue take /q/build --ttl 5m
turnstile-queue done /q/build/0003
```

**Proves:** prefix range + conditional claim + lease reclaim — a pull-based work queue in miniature.

**The demo:** start 8 takers. `kill -9` one mid-work. **Watch its item return to the queue automatically and get picked up.** No supervisor, no health check, no retry logic anywhere in the code.

**That is the entire supervision story, in a 120-line program.**

### `turnstile-elect` (~60 lines)

```bash
turnstile-elect /cap/merge --ttl 15s -- ./merge-controller
```

One instance runs. If it dies, another takes over within 15s.
**Proves:** singleton lease + watch for takeover — leader election and capability registration, verbatim.

### `turnstile watch` (~20 lines — or zero, it's `curl`)

```bash
turnstile watch /order/ | jq -c '.'
curl --unix-socket /run/turnstile.sock -N 'localhost/watch?prefix=/order/&from=0'
```

**Proves:** SSE, cursor resume, shell-native streaming. Background it from an agent's bash tool, go idle, get woken on completion. **Verify end-to-end against real Claude Code and Copilot sessions** — it should be proven against the kernel, not a mock.

---

## 11. The family

```
turnstile         coordination store: kv, lease, txn, watch
  turnstile-lock    distributed mutex
  turnstile-queue   work queue
  turnstile-elect   leader election
```

**The threat model is the deployment.** Turnstile holds no credentials and makes no outbound calls, so a bare `turnstile` deployment has *nothing* to compromise. Each controller you build on top is an explicit decision, and any component that can act on the outside world — the one holding a write token — is a small, auditable surface you add deliberately.

That is a structural fact, not a clever argument.

---

## 12. Deferred

- Per-key ACLs → a proxy tool, not kernel surface
- Multi-writer / replication → one writer of record; cross-machine is one Turnstile over Tailscale
- gRPC → no
- Any notion of orchestration → controllers

---

## 13. Reading list

1. **kine — `pkg/logstructured/sqllog`.** Your storage layer, already written, in a language you can read. **Start here; it saves a week.**
2. **Chubby (Burrows, OSDI '06).** The *rationale* — why a lock service beats a consensus library for application developers. §2.4 is where fencing tokens ("sequencers") come from.
3. **etcd Learning docs — data model + Txn.** The API contract being narrowed.
4. **ZooKeeper recipes.** Locks, leader election, barriers, queues. Every recipe above is in there, named and debugged twenty years ago.

---

> **You set out to build a coordination daemon and discovered you are building a very small etcd. That is good news: the hard parts are two decades old and solved, and everything genuinely novel lives in the controllers above.**