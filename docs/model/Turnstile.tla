---- MODULE Turnstile ----
(* Model of the lease/expiry/watch core of Turnstile, the coordination store in
   src/Turnstile.

   The bug class: clients build mutual exclusion out of leases they do not control the
   expiry of. A lock is a key attached to a lease; the holder stays alive by renewing,
   and a sweeper deletes the keys of any lease whose deadline has passed. Two
   independent processes therefore reason about the same lease at the same instant --
   the client renewing it and the sweeper reaping it -- and the client's whole safety
   argument rests on one claim, stated in HardeningTests:

     a successful keepalive means the key was never reaped out from under it;
     a failed one means it is gone and must not be re-acquired.

   Nothing in the lease table enforces that. What enforces it is that every write --
   put, keepalive, revoke and sweep alike -- funnels through the single WriteActor, so
   "check the deadline" and "delete the keys" cannot be separated by another writer.
   The property is a consequence of the serialization point, not of the lease logic,
   which is exactly the kind of load-bearing structure that is easy to refactor away
   without noticing. `NeverReapLiveLease` is that claim; `SplitSweep` is what happens
   when the actor stops being the serialization point.

   The second question is different in kind. Expiry is swept eagerly rather than
   evaluated lazily on read, and KvStore says why: "expiry produces delete events --
   lazy expiry would be correct but silent." Lazy expiry gives every reader the same
   answer, so no invariant over the store's visible state can tell the two apart. The
   difference is only observable to a watcher, as the absence of an event. That makes
   it a property of transitions rather than of states, and `RemovalIsLoggedStep` is
   stated over the step for that reason.

   The third is the watch wake-up. WatchAsync captures the change signal *before*
   draining the backlog, with the comment "so a commit that races the drain still wakes
   them". Reversing those two lines loses a wake-up: a commit landing between the drain
   and the capture is neither drained nor counted as a reason to wake, so the watcher
   parks holding a cursor it knows to be stale. `NoLostWakeup` states that a parked
   watcher which is behind always has a pending reason to wake, which is checkable
   without any liveness machinery. This is the wake-*signal* ordering only, and the
   shipped code gets it right: `WaitForChangeAsync()` is captured before the drain.

   The watch also emits a one-shot "caught up" revision -- `WatchSyncMessage`. Since
   nightshift #197 that boundary is the committed revision read in the *same* snapshot as
   the events the drain returned, so a batch shorter than a page has delivered every
   matching event with `id <= Boundary` and the sync at `Boundary` cannot advertise a
   revision whose event was not delivered. The model's single atomic `rev` -- read by
   both the drain (which advances `cursor`) and the capture -- is the faithful image of
   that one snapshot: there is no two-snapshot gap to represent because the code no longer
   has one. `NoLostWakeup` and the atomic drain together correspond to the shipped watch.
   (The `/kv` range read -- items plus their revision, made coherent by the same #197 fix
   -- is not modelled here at all; the model speaks only to the watch.)

   Scope: keys, leases and revisions are abstract. Deliberately NOT modeled -- SQLite
   semantics, WAL behaviour, the txn compare/branch language, prefix ranges, key and
   value validation, the socket protocol, or the client helpers (lock, elect, queue)
   layered on top. Those turn bytes into the state vector; this models what happens to
   the state vector over time. The client obligation in the second half of the
   HardeningTests claim -- that a failed keepalive must not be followed by
   re-acquisition -- is a rule about client code, not about the store, and stays with
   the tests that exercise it.

   Also out of scope, so the model must not be read as covering them:
     - revision overflow (#198): the Int64 *value* of the ceiling is not modelled --
       `rev` is bounded by MaxRevision only to keep the state space finite. But the
       *shape* the bound enforces now corresponds to the implementation: once the ceiling
       is reached no revision-allocating action is enabled, and a multi-row action that
       would cross it (`rev + n <= MaxRevision`) does not fire at all rather than partially
       applying. That is fail-closed exhaustion -- the real allocator throws and rolls the
       whole transaction back with no row, no counter move, and no pulse (enforced by
       `RevisionOverflowTests`), never an integer wrap. The model images the fail-closed
       boundary, not the specific 2^63 limit.
     - cross-instance watch *notification*. Revision *allocation* across independently
       opened instances is now globally serialized -- each writer reads the durable
       committed revision under BEGIN IMMEDIATE as its base, never a cached value -- so
       the model's single global `rev` faithfully abstracts it (#199). What the model
       still does not cover is notification: each instance's change signal is in-memory,
       so a watcher on one instance is not woken by another instance's commit. That is a
       distinct gap, not addressed by #199.
     - the wire: watch events now carry `immutable` in serialization (#196), and the
       matching txn GET omission (#195) is likewise fixed in the implementation. Both are
       protocol concerns the abstract state vector never carried, so the model modelled the
       wire neither before nor after the fixes; it makes no claim about serialization
       fidelity either way.
   These are honest gaps, not claims -- a passing run of this model says nothing about
   any of them.

   The fourth question is the clock's resolution. KvStore.Now() truncates to whole
   seconds, but the instant a lease is granted at does not, so a deadline of
   `Now() + ttlSecs` is short by however much of the current second has already elapsed.
   Grain is the number of clock sub-ticks per stored second: at Grain = 1 the two
   collapse and every TTL is full length, which is the assumption `LeaseHonoursItsTtl`
   fails without. Ttl >= 1 is assumed so that a live lease's deadline is always
   distinguishable from the absent-lease sentinel.

   Status: parsed cleanly with SANY and model-checked with TLC 2.19 against
   Turnstile.cfg -- no errors, 558,208 states generated, 106,978 distinct, depth 18,
   zero violations (a few seconds). One configuration is a finding rather than a gate:
   TurnstileSubSecond.cfg raises Grain to 2 and violates LeaseHonoursItsTtl against
   Mutation = "None", because a lease granted partway through a second is short by the
   part already elapsed -- the root cause of the flaky suite fixed in #177, tracked
   for the store side in #189. The base configuration deliberately enables
   AllowClockStepBack: the server clock is DateTimeOffset.UtcNow, which is wall time and
   can move backwards, and the design is meant to tolerate that. It does, because the
   sweep's selection and its deletion are one transaction on the write actor.

   Validated by mutation, each in its own configuration -- see docs/model/README.md for
   the matrix. Splitting the sweep into a scan and an apply violates NeverReapLiveLease
   once the clock may step back (scan while expired, clock steps back, the lease is live
   again, delete anyway) and is clean while it may not, which is what identifies the
   atomicity rather than the lease logic as the load-bearing part; removing keys without
   writing rows violates RemovalIsLoggedStep; draining before capturing the wake
   threshold violates NoLostWakeup; admitting a lease on the boundary tick the sweeper
   deletes it violates NoWriteUnderExpiredLease; letting a rejected write consume a
   revision violates LogIsGapless; and computing the holder's deadline on a client clock
   -- what RemoteStore does -- makes the handed-back deadline a different number from the
   stored one, violating BeliefMatchesStoredDeadline. That counterexample proves the
   numeric mismatch only (a constant clock offset), not early reaping. It is NOT a general
   "no harm" result: RemoteStore computes client-Now + ttl only after the server has
   already stored expiry and returned, so response delay makes the value later than the
   enforced deadline even at zero skew. This atomic CreateLease model has no request/
   response delivery step, so it neither certifies nor refutes that consequence -- it only
   shows the value is informational, never the enforced server deadline.

   Re-run TLC after any change here to regenerate the counts and confirm zero
   violations. *)

EXTENDS Integers, FiniteSets

CONSTANTS
    Keys,               \* abstract keys; a lock is one of these attached to a lease
    Leases,             \* abstract lease ids
    Ttl,                \* lease lifetime, in whole seconds -- the unit the lease table stores
    Grain,              \* clock sub-ticks per whole second; 1 assumes every TTL is full length
    Skew,               \* the client clock's offset from the server (positive = client leads)
    AllowClockStepBack, \* whether the server clock may run backwards
    MaxTime,            \* bound the clock so the state space is finite
    MaxRevision,        \* bound the log so the state space is finite
    Mutation            \* "None" for the real design; anything else enables one defect

Mutations == {"None", "SplitSweep", "LazyExpiry", "DrainThenCapture",
              "ExpiryBoundaryOff", "FailedWriteConsumesRevision",
              "ClientComputedDeadline"}

ASSUME /\ Keys \subseteq (Nat \ {0})
       /\ Leases \subseteq (Nat \ {0})
       /\ Ttl \in (Nat \ {0})
       /\ Grain \in (Nat \ {0})
       /\ Skew \in Nat
       /\ AllowClockStepBack \in BOOLEAN
       /\ MaxTime \in Nat
       /\ MaxRevision \in Nat
       /\ Mutation \in Mutations

\* 0 is the sentinel for "no lease row" and "no lease attached". Ttl >= 1 keeps every
\* real deadline >= 1, so the sentinel can never collide with one.
Nothing == 0

VARIABLES
    now,                \* server clock, in sub-ticks (see Second below)
    expiry,             \* lease id -> deadline in whole seconds on the SERVER clock, Nothing when absent
    grantedAt,          \* lease id -> the sub-tick at which that deadline was handed out
    belief,             \* lease id -> the deadline its holder was handed, 0 when it holds none
    live,               \* the set of keys currently live
    owner,              \* key -> the lease it is attached to, or Nothing
    rev,                \* the highest committed revision
    logRows,            \* how many rows the log actually holds
    lastDel,            \* keys tombstoned by the step just taken (one-step history)
    cursor,             \* the watcher's position in the log
    captured,           \* the revision the watcher captured as its wake threshold
    wstate,             \* where the watcher is in capture/drain/park
    scanned,            \* leases a split sweep has scanned but not yet applied
    reapedLive,         \* set once a sweep has reaped a lease that was live at the time
    wroteUnderExpired   \* set once a write was accepted naming an already-expired lease

vars == << now, expiry, grantedAt, belief, live, owner, rev, logRows, lastDel, cursor,
           captured, wstate, scanned, reapedLive, wroteUnderExpired >>

\* The clock advances in sub-ticks; the lease table stores whole seconds. KvStore.Now()
\* is `DateTimeOffset.UtcNow.ToUnixTimeSeconds()`, so every deadline it writes is
\* truncated to a second boundary while the instant it was computed at was not. Grain = 1
\* collapses the two and assumes every TTL is full length, which is the assumption
\* TurnstileSubSecond.cfg removes.
Second(t) == t \div Grain

\* The deadline handed back to the holder. KvStore returns the value it stored, on the
\* server clock. RemoteStore does not: it fabricates one from the *client* clock as
\* `DateTimeOffset.UtcNow.ToUnixTimeSeconds() + dto.Ttl`, and its own comment concedes
\* the value is "informational only". Nothing in the LeaseInfo type says which of the
\* two a caller is holding, so ClientComputedDeadline hands back a number computed on the
\* client clock. Skew is that clock's offset from the server (positive = the client leads),
\* so the fabricated deadline differs from the stored one by exactly Skew. That is the
\* constant-offset mismatch this counterexample models; the separate response-delay
\* overstatement (real even at zero skew, but not modeled here) is discussed at
\* BeliefMatchesStoredDeadline below.
HandedBack == IF Mutation = "ClientComputedDeadline"
              THEN Second(now) + Ttl + Skew
              ELSE Second(now) + Ttl

----------------------------------------------------------------------------------
\* Lease predicates. These two must agree: KvStore guards writes with `exp > Now()`
\* and the sweeper selects with `expires_at <= now`, so over existing leases they
\* partition exactly. ExpiryBoundaryOff breaks that agreement at the boundary tick.

Exists(l) == expiry[l] # Nothing

LeaseLive(l) ==
    /\ Exists(l)
    /\ IF Mutation = "ExpiryBoundaryOff"
       THEN expiry[l] >= Second(now)
       ELSE expiry[l] > Second(now)

Expired(l) == Exists(l) /\ expiry[l] <= Second(now)

KeysOf(l) == {k \in live : owner[k] = l}

----------------------------------------------------------------------------------
\* The watcher's loop order. The real one captures the change signal, drains, then
\* parks. DrainThenCapture swaps the first two, which is the lost wake-up.

WatchStart    == IF Mutation = "DrainThenCapture" THEN "drain" ELSE "capture"
NextOfCapture == IF Mutation = "DrainThenCapture" THEN "park" ELSE "drain"
NextOfDrain   == IF Mutation = "DrainThenCapture" THEN "capture" ELSE "park"

----------------------------------------------------------------------------------

TypeOK ==
    /\ now \in 0..(MaxTime * Grain)
    /\ expiry \in [Leases -> 0..(MaxTime + Ttl)]
    /\ grantedAt \in [Leases -> 0..(MaxTime * Grain)]
    /\ belief \in [Leases -> 0..(MaxTime + Ttl + Skew)]
    /\ live \subseteq Keys
    /\ owner \in [Keys -> Leases \cup {Nothing}]
    /\ rev \in 0..MaxRevision
    /\ logRows \in 0..MaxRevision
    /\ lastDel \subseteq Keys
    /\ cursor \in 0..MaxRevision
    /\ captured \in 0..MaxRevision
    /\ wstate \in {"capture", "drain", "park"}
    /\ scanned \subseteq Leases
    /\ reapedLive \in BOOLEAN
    /\ wroteUnderExpired \in BOOLEAN

Init ==
    /\ now = 0
    /\ expiry = [l \in Leases |-> Nothing]
    /\ grantedAt = [l \in Leases |-> 0]
    /\ belief = [l \in Leases |-> 0]
    /\ live = {}
    /\ owner = [k \in Keys |-> Nothing]
    /\ rev = 0
    /\ logRows = 0
    /\ lastDel = {}
    /\ cursor = 0
    /\ captured = 0
    /\ wstate = WatchStart
    /\ scanned = {}
    /\ reapedLive = FALSE
    /\ wroteUnderExpired = FALSE

----------------------------------------------------------------------------------
\* Actions.

Tick ==
    /\ now < MaxTime * Grain
    /\ now' = now + 1
    /\ lastDel' = {}
    /\ UNCHANGED << expiry, grantedAt, belief, live, owner, rev, logRows, cursor, captured,
                    wstate, scanned, reapedLive, wroteUnderExpired >>

\* The server clock is DateTimeOffset.UtcNow, which is wall time and not monotonic: NTP
\* correction or an operator can move it backwards. This is environmental rather than a
\* defect, so it is a constant rather than a mutation -- the design is supposed to
\* tolerate it, and the base configuration enables it to check that it does.
ClockStepBack ==
    /\ AllowClockStepBack
    /\ now > 0
    /\ now' = now - 1
    /\ lastDel' = {}
    /\ UNCHANGED << expiry, grantedAt, belief, live, owner, rev, logRows, cursor, captured,
                    wstate, scanned, reapedLive, wroteUnderExpired >>

CreateLease(l) ==
    /\ ~Exists(l)
    /\ expiry' = [expiry EXCEPT ![l] = Second(now) + Ttl]
    /\ grantedAt' = [grantedAt EXCEPT ![l] = now]
    /\ belief' = [belief EXCEPT ![l] = HandedBack]
    /\ lastDel' = {}
    /\ UNCHANGED << now, live, owner, rev, logRows, cursor, captured, wstate,
                    scanned, reapedLive, wroteUnderExpired >>

\* A put under a lease. The guard is the only thing standing between a client and a
\* key attached to a lease the sweeper is about to delete.
Put(k, l) ==
    /\ rev < MaxRevision
    /\ LeaseLive(l)
    /\ rev' = rev + 1
    /\ logRows' = logRows + 1
    /\ live' = live \cup {k}
    /\ owner' = [owner EXCEPT ![k] = l]
    /\ wroteUnderExpired' = (wroteUnderExpired \/ Expired(l))
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, cursor, captured, wstate, scanned,
                    reapedLive >>

\* A rejected write. In the real store this consumes no revision -- Exists never
\* touches the log, which is what makes the revision sequence gapless.
PutRejected(k, l) ==
    /\ Mutation = "FailedWriteConsumesRevision"
    /\ rev < MaxRevision
    /\ ~LeaseLive(l)
    /\ rev' = rev + 1
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, live, owner, logRows, cursor, captured,
                    wstate, scanned, reapedLive, wroteUnderExpired >>

KeepAlive(l) ==
    /\ LeaseLive(l)
    /\ expiry' = [expiry EXCEPT ![l] = Second(now) + Ttl]
    /\ grantedAt' = [grantedAt EXCEPT ![l] = now]
    /\ belief' = [belief EXCEPT ![l] = HandedBack]
    /\ lastDel' = {}
    /\ UNCHANGED << now, live, owner, rev, logRows, cursor, captured, wstate,
                    scanned, reapedLive, wroteUnderExpired >>

\* The sweep as written: selecting the expired leases and tombstoning their keys is one
\* transaction on the write actor, so nothing can renew the lease in between.
SweepAtomic(l) ==
    /\ Mutation # "SplitSweep"
    /\ Expired(l)
    /\ LET K == KeysOf(l)
           n == Cardinality(K)
           silent == Mutation = "LazyExpiry"
       IN /\ rev + n <= MaxRevision
          /\ rev' = IF silent THEN rev ELSE rev + n
          /\ logRows' = IF silent THEN logRows ELSE logRows + n
          /\ lastDel' = IF silent THEN {} ELSE K
          /\ live' = live \ K
          /\ owner' = [k \in Keys |-> IF k \in K THEN Nothing ELSE owner[k]]
          /\ reapedLive' = (reapedLive \/ LeaseLive(l))
    /\ expiry' = [expiry EXCEPT ![l] = Nothing]
    /\ belief' = [belief EXCEPT ![l] = 0]
    /\ UNCHANGED << now, grantedAt, cursor, captured, wstate, scanned, wroteUnderExpired >>

\* SplitSweep: the selection and the deletion become two steps, so a keepalive can land
\* between them and the deletion proceeds on a stale decision.
SweepScan(l) ==
    /\ Mutation = "SplitSweep"
    /\ Expired(l)
    /\ l \notin scanned
    /\ scanned' = scanned \cup {l}
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, live, owner, rev, logRows, cursor, captured,
                    wstate, reapedLive, wroteUnderExpired >>

SweepApply(l) ==
    /\ Mutation = "SplitSweep"
    /\ l \in scanned
    /\ LET K == KeysOf(l)
           n == Cardinality(K)
       IN /\ rev + n <= MaxRevision
          /\ rev' = rev + n
          /\ logRows' = logRows + n
          /\ lastDel' = K
          /\ live' = live \ K
          /\ owner' = [k \in Keys |-> IF k \in K THEN Nothing ELSE owner[k]]
          /\ reapedLive' = (reapedLive \/ LeaseLive(l))
    /\ expiry' = [expiry EXCEPT ![l] = Nothing]
    /\ belief' = [belief EXCEPT ![l] = 0]
    /\ scanned' = scanned \ {l}
    /\ UNCHANGED << now, grantedAt, cursor, captured, wstate, wroteUnderExpired >>

\* The watcher. Capture records the revision it will wake above; drain advances the
\* cursor to whatever is committed; park blocks until the log passes the threshold.
WatchCapture ==
    /\ wstate = "capture"
    /\ captured' = rev
    /\ wstate' = NextOfCapture
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, live, owner, rev, logRows, cursor, scanned,
                    reapedLive, wroteUnderExpired >>

WatchDrain ==
    /\ wstate = "drain"
    /\ cursor' = rev
    /\ wstate' = NextOfDrain
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, live, owner, rev, logRows, captured, scanned,
                    reapedLive, wroteUnderExpired >>

WatchPark ==
    /\ wstate = "park"
    /\ rev > captured
    /\ wstate' = WatchStart
    /\ lastDel' = {}
    /\ UNCHANGED << now, expiry, grantedAt, belief, live, owner, rev, logRows, cursor, captured,
                    scanned, reapedLive, wroteUnderExpired >>

Next ==
    \/ Tick
    \/ ClockStepBack
    \/ \E l \in Leases : CreateLease(l)
    \/ \E k \in Keys, l \in Leases : Put(k, l)
    \/ \E k \in Keys, l \in Leases : PutRejected(k, l)
    \/ \E l \in Leases : KeepAlive(l)
    \/ \E l \in Leases : SweepAtomic(l)
    \/ \E l \in Leases : SweepScan(l)
    \/ \E l \in Leases : SweepApply(l)
    \/ WatchCapture
    \/ WatchDrain
    \/ WatchPark

Spec == Init /\ [][Next]_vars

----------------------------------------------------------------------------------
\* Invariants.

\* Every revision the store handed out is a row in the log. A rejected write that
\* consumed a revision would leave a number nothing accounts for -- and a watcher
\* resuming across it would wait for an event that is never coming.
LogIsGapless == logRows = rev

\* A live key always names a lease whose row still exists. Sweeping deletes the keys
\* and the row in the same step, so a key can never outlive the lease holding it.
LiveKeysHaveALeaseRow == \A k \in live : owner[k] \in Leases /\ Exists(owner[k])

\* The store half of the HardeningTests claim: a sweep never reaps a lease that was
\* live when the reaping happened. If it did, a client whose keepalive had just
\* succeeded would lose its keys while believing it still held them.
NeverReapLiveLease == ~reapedLive

\* Fail closed: a write naming an expired lease is refused. The write guard and the
\* sweeper's selection must partition existing leases exactly, or a put lands on a
\* lease that is already being deleted.
NoWriteUnderExpiredLease == ~wroteUnderExpired

\* A parked watcher that is behind the log always has a pending reason to wake. The
\* wake threshold is captured before draining precisely so this holds.
NoLostWakeup == (wstate = "park" /\ rev > cursor) => rev > captured

\* The deadline a holder is handed is the deadline the store will actually enforce.
\* KvStore returns the value it stored; RemoteStore fabricates one from the client
\* clock, and this is the invariant that separates the two.
\*
\* What the ClientComputedDeadline counterexample proves is a numeric/type-shape mismatch
\* only: RemoteStore computes ExpiresAt as `client-Now + ttl` rather than echoing the
\* server's stored `expires_at`, so with a nonzero clock offset (Skew, positive = client
\* leads) the two are different numbers. It does NOT, by itself, prove early reaping -- a
\* constant offset cancels for a holder that both computes and later checks on its own
\* clock.
\*
\* But do not read that as "no reaping hazard". There is a real overstatement window this
\* model does not represent. `CreateLeaseAsync` stores expiry on the server and only then
\* returns; RemoteStore computes `client-Now + ttl` *after* awaiting that round trip, so
\* even at zero clock skew the fabricated deadline is later than the enforced one by the
\* request/queue/write/response delay -- the same delay the README's caller-visible
\* lifetime note describes. CreateLease here is a single atomic step with no request/
\* response delivery, so the model neither certifies nor refutes that consequence. It
\* establishes only that RemoteStore's value is a different number, and therefore
\* informational -- never safe to treat as the enforced server deadline.
BeliefMatchesStoredDeadline ==
    \A l \in Leases : Exists(l) => belief[l] = expiry[l]

\* A lease granted for Ttl seconds stays live for Ttl seconds. This is what a caller
\* asking for a TTL believes it is buying, and it is the assumption behind any renewal
\* cadence derived from the TTL.
\*
\* This is the *stored* semantics: it compares the deadline the store computed and wrote
\* -- `floor(now) + ttlSecs`, measured from the grant instant `grantedAt` -- against the
\* requested Ttl. It holds trivially at Grain = 1. It does not hold on the real clock:
\* Now() truncates to whole seconds, so a lease granted partway through a second is short
\* by exactly the fraction already elapsed, bounded by one second. See TurnstileSubSecond.cfg
\* -- this fails against the real design, not a mutation.
\*
\* The *caller-observed* lifetime is weaker still and is deliberately NOT modeled: the
\* enqueue on the write actor, the commit, and the response's trip back all elapse between
\* `grantedAt` and the moment the caller can first act on the lease, and none is bounded.
\* So even a full-length stored deadline gives the caller no positive lower bound on the
\* time it actually holds the lock; a renewal cadence must budget for Ttl - 1 seconds and
\* for that unmodeled delay besides (#189).
LeaseHonoursItsTtl ==
    \A l \in Leases : Exists(l) => (expiry[l] * Grain) - grantedAt[l] >= Ttl * Grain

----------------------------------------------------------------------------------
\* Step properties. These are about transitions, not states, because the defects they
\* catch leave no trace in any single state.

\* A key stops being live only by way of a row in the log. Lazy expiry would give every
\* reader the same answer as eager expiry, so no state invariant separates them; the
\* difference is only ever visible as an event that did not happen.
RemovalIsLoggedStep ==
    [][ \A k \in Keys : (k \in live /\ k \notin live') => k \in lastDel' ]_vars

\* The log only ever grows.
RevisionNeverDecreasesStep == [][ rev' >= rev ]_vars

\* The watcher's cursor only advances, and never past what has been committed.
CursorAdvancesSoundlyStep == [][ cursor' >= cursor /\ cursor' <= rev' ]_vars

====
