# Models

Three TLA+ specifications, kept apart because they answer different questions.

| Spec | Question |
| --- | --- |
| `Waiting.tla` | Over time, who owns a PR and what may be acted on? |
| `TmuxWindows.tla` | At one instant, which window is this even, given that agents write to each other's? |
| `Turnstile.tla` | What may a lease holder conclude, when a sweeper is deleting on a clock nobody controls? |

Mixing them would blur both: "who owns this PR" and "which window is this" have
different state, different actions and different failure modes. `Turnstile.tla` is
further apart still — it models the coordination store under `src/Turnstile`, not the
GitHub gate.

## TmuxWindows

Several agents write one shared namespace with no access control, and the tool reads it. A
tmux command without an explicit target lands on whichever window is *current*, which is
somebody else's — observed live twice, as four windows carrying a fifth's `@agent_state`,
and as a window named for a PR its own state said it was not working on.

The tool used to be a writer here too: `octoshift waiting --rename` corrected window-name
suffixes to match what a sweep observed. That was removed (nightshift issues #170–#172). A
persistent window name is read at a glance and believed, but its suffix encoded GitHub
state, the active pane and wall-clock activity — none of which a per-window tmux guard can
atomically revalidate at mutation time against a name that outlives the fact it asserted. So
the tool no longer writes this namespace at all; it reports the same findings in the row,
where they are re-derived each sweep and cannot go stale. The model follows: the name
channel is now written only by agents (`NameWindow` is an ordinary agent naming its own
window), and the feedback-loop property that once checked the tool's own rename is gone with
the rename.

The model does not ask whether agents make that mistake; they demonstrably do. It asks
which identity channels survive it. The answer is sharper than expected:

- **`@agent_state` and the window name are both writable by anyone**, and an untargeted
  publish sets them *together* — so a clobbered window's two durable channels agree with
  each other about a PR it never touched. There is nothing internal to notice.
- **Pane text is the only channel another agent cannot write**, because a process writes
  to its own terminal and nowhere else. That makes it useless for durability — it scrolls
  away, and this UI has no scrollback — and uniquely sound for detecting a clobber.

TLC confirms it: `IdentityIsSound` over the implemented rule is violated in three steps,
and the corroborated rule holds. The implementation now flags state its window's own
output contradicts.

One refinement came from the fleet rather than the checker. Requiring the pane to
*mention* the claimed PR flagged a window whose state was perfectly good and whose pane
was simply empty — normal here, since a report that has scrolled past the top leaves only
chrome. Silence is not disagreement, so the rule fires only when the pane talks about PRs
and never about this one.

## Turnstile

Clients build mutual exclusion out of leases whose expiry they do not control. A lock is
a key attached to a lease; the holder stays alive by renewing, and a sweeper deletes the
keys of any lease whose deadline has passed. Two processes therefore reason about the
same lease at the same instant, and the client's entire safety argument is one sentence
from `HardeningTests`:

> a successful keepalive means the key was never reaped out from under it; a failed one
> means it is gone and must not be re-acquired.

Four things came out of checking it, and the first is a defect in shipped code.

**A lease does not last as long as it says.** `KvStore.Now()` truncates to whole
seconds, but the instant a lease is granted at does not, so `Now() + ttlSecs` is short
by however much of the current second has already elapsed. A one-second lease granted
partway through a second is live for less than a second — in the limit, for
microseconds. `Grain` is the number of clock sub-ticks per stored second; at `Grain = 1`
the two collapse and `LeaseHonoursItsTtl` holds trivially, which is why the base
configuration does not catch it. `TurnstileSubSecond.cfg` sets `Grain = 2` and the
invariant fails against `Mutation = "None"` — the real design — in three steps: tick to
mid-second, grant, and the lease is half the length it was asked for.

This is not hypothetical. It is the root cause of the flaky suite fixed in #177, where
five tests created one-second leases and did setup work under them. Those were repaired
on the test side; the store behaviour is unchanged, and the consequence for a *client*
is the part that has not been addressed — a renewal cadence derived from the TTL must
assume `Ttl - 1` seconds, not `Ttl`, or the holder can lose its lease early. Tracked in
#189.

**The clock is not monotonic, and the design survives that anyway.** Expiry is evaluated
against `DateTimeOffset.UtcNow`, which is wall time: NTP or an operator can move it
backwards. That is environmental rather than a defect, so it is a constant
(`AllowClockStepBack`) rather than a mutation, and the base configuration turns it *on*.
TLC finds no violation. The reason is worth naming, because it is not the lease logic —
`SweepExpiredAsync` selects the expired leases and deletes their keys inside one write
transaction, so "is it expired" and "delete it" are evaluated against the same instant.
No clock movement can get between them.

**Which makes the atomicity, not the lease logic, the load-bearing part.** The
`SplitSweep` mutation separates the selection from the deletion and changes nothing
else. Under a monotone clock it is still correct — `TurnstileSplitSweepMonotonic.cfg`
runs clean, because a lease that is expired can never become live again, so a stale
selection stays true. Allow the clock to step back and the same mutation violates
`NeverReapLiveLease` in six steps: scan while expired, clock steps back, the lease is
live once more, delete anyway. A refactor that moved the sweep's clock read outside the
write actor would be invisible in tests and on a well-behaved machine.

**`LeaseInfo.ExpiresAt` means two different things.** `KvStore` returns the deadline it
stored, on the server clock. `RemoteStore` fabricates one from the *client* clock:

```csharp
long expiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + dto.Ttl;
```

Its comment concedes the value is "informational only — keepalive returns authoritative
remaining TTL", but nothing in the type says which of the two a caller is holding. The
`ClientComputedDeadline` mutation asks what a caller that trusts it concludes when its
clock trails the server's, and the answer is that it believes it holds a lock past the
point the server has already reaped: `BeliefMatchesStoredDeadline` fails, and with that
invariant removed so it cannot mask the consequence, `NeverReapBelievedLease` fails too.
This is a hazard in the type's shape rather than a bug in either store.

### Checked properties

| Design property | Model property |
| --- | --- |
| Every revision handed out is a row in the log | `LogIsGapless` |
| A key never outlives the lease holding it | `LiveKeysHaveALeaseRow` |
| A sweep never reaps a lease that is live | `NeverReapLiveLease` |
| Writes naming an expired lease are refused | `NoWriteUnderExpiredLease` |
| A parked watcher that is behind always has a reason to wake | `NoLostWakeup` |
| The deadline a holder is handed is the one that will be enforced | `BeliefMatchesStoredDeadline` |
| Keys are never reaped inside the window their holder was promised | `NeverReapBelievedLease` |
| A lease granted for Ttl seconds stays live for Ttl seconds | `LeaseHonoursItsTtl` |
| A key stops being live only by way of a log row | `RemovalIsLoggedStep` |
| The log only grows | `RevisionNeverDecreasesStep` |
| A cursor advances, and never past what is committed | `CursorAdvancesSoundlyStep` |

`RemovalIsLoggedStep` is a step property rather than an invariant on purpose. Lazy
expiry would give every *reader* the same answer as eager expiry, so no predicate over a
single state can separate them — the difference exists only as an event that did not
happen. `KvStore` makes the same point in prose: "expiry produces delete events — lazy
expiry would be correct but silent."

### Runs

```sh
cd docs/model
java -XX:+UseParallelGC -cp tla2tools.jar tlc2.TLC -workers 1 -cleanup \
  -config Turnstile.cfg Turnstile.tla
```

Two keys, two leases, `Ttl = 1`, `Skew = 1`, clock bound 3, revision bound 4. With TLC
2.19 the base configuration generates 390,517 states, 72,163 distinct, depth 18, zero
violations, in a few seconds.

| Configuration | Clock may step back | Result |
| --- | --- | --- |
| `Turnstile.cfg` | yes | No error — 558,208 states, 106,978 distinct, depth 18 |
| `TurnstileSplitSweepMonotonic.cfg` | no | No error — 315,718 states, 88,849 distinct, depth 18 |

The base configuration runs at `Grain = 1`, which assumes every TTL is full length. That
assumption is false on the real clock, and removing it is what
`TurnstileSubSecond.cfg` does.

The second is a contrast, not a gate: it is the same defect as `TurnstileSplitSweep.cfg`
with the clock forced to behave, and it passes. It is recorded to show that the mutation
needs the clock to bite, which is the whole point of that section above.

### Counterexample mutations

Each opt-in configuration enables one defect and must fail with the named property. A
clean exit means the gate has gone vacuous.

| Configuration | Deliberate defect | Expected violation |
| --- | --- | --- |
| `TurnstileSplitSweep.cfg` | Sweep selects and deletes in two steps | `NeverReapLiveLease` |
| `TurnstileLazyExpiry.cfg` | Expiry removes keys without writing rows | `RemovalIsLoggedStep` |
| `TurnstileDrainThenCapture.cfg` | Watcher drains before capturing its wake threshold | `NoLostWakeup` |
| `TurnstileExpiryBoundaryOff.cfg` | Write guard admits a lease on the tick the sweeper deletes it | `NoWriteUnderExpiredLease` |
| `TurnstileFailedWriteConsumesRevision.cfg` | A rejected write consumes a revision | `LogIsGapless` |
| `TurnstileClientComputedDeadline.cfg` | Holder's deadline computed on a client clock | `BeliefMatchesStoredDeadline` |
| `TurnstileClientComputedDeadlineReap.cfg` | The same, with the cause invariant removed | `NeverReapBelievedLease` |

`TurnstileSubSecond.cfg` is listed separately because it is not a mutation: it runs
`Mutation = "None"` and fails, which makes it a finding rather than a gate.

| Configuration | What it removes | Violation |
| --- | --- | --- |
| `TurnstileSubSecond.cfg` | The assumption that a TTL is full length | `LeaseHonoursItsTtl` |

All were run with `-workers 1` and produced their named violation. State counts for
mutations are not recorded: the first violating state is not deterministic across runs.

One mutation was written, failed to fire, and was kept — the investigation into *why*
`SplitSweep` passed is what produced the clock finding above. A mutation that does not
fire is either a vacuous property or a fact about the design that was not understood
yet, and it is worth finding out which.

## Waiting

TLA+ specification of the memory and ownership rules in `octoshift waiting`.

## Running it

Needs a JVM and `tla2tools.jar`:

```sh
brew install openjdk
curl -sLO https://github.com/tlaplus/tlaplus/releases/latest/download/tla2tools.jar

java -cp tla2tools.jar tla2sany.SANY Waiting.tla                     # parse
java -cp tla2tools.jar tlc2.TLC -config Waiting.cfg -workers auto Waiting.tla
```

Current bounds — 3 windows, 2 hosts, 2 PRs, 8 steps — run in a few seconds: with TLC 2.19
(12 workers) SANY parses cleanly and TLC reports 8,484,242 states generated, 1,539,916
distinct, depth 9, zero violations (~10s). Raise `MaxTime` for a
deeper search; hosts multiply the state space quickly, since every sweep branches over
the subsets of hosts it might have collected and every host restarts on its own epoch —
and opening the first window on an empty host is a server start that advances that host's
epoch too. A sweep also branches over the hosts it *attempted* against the subset that
*answered*, so a host requested but failed is distinct from one never asked about: that is
what keeps a never-before-known target that fails from reading as a complete view. The
persistent fleet membership (`knownHosts`) grows with the *attempted* hosts, not the
collected ones, so a host that fails on its very first attempt is still remembered — a
later sweep that omits it reads as narrowed rather than complete. A ghost `everAttempted`
tracks that independently, so `CompletenessCoversEveryAttemptedHost` refutes a mutation
that reverts the membership to collected-growth (the round-9 first-time-failed-host bug).
Membership also *shrinks* through the explicit `Retire` action and *grows back* through the
explicit `Add` action — operator acts, never ordinary collection — which respectively remove
a host and clear the registration state kept under it, and re-declare a host (the only way to
bring the local machine back once retired). `NoOwnerFromRetiredHost` is the safety retirement
earns: a retired host's stale claim can never remain actionable. `Add` adds transitions
without adding reachable distinct states (an add-then-sweep reaches only what a sweep already
does), and it re-stamps a collected window's registration fleet exactly as `Retire` does, so
`OwnerStableAcrossSweepStep` still holds — dropping that re-stamp is a refuted mutation.


## What is modelled, and what is not

**Modelled:** what happens to the state vector over time. Windows opening, closing and
switching PRs; the tmux server restarting; sweeps recording registrations; and the
ownership and confidence-of-ownership derived from those memories.

**Not modelled:** parsing `@agent_state`, tmux formats, REST semantics, ssh transport,
confidence grading, the verdict decision table. These turn bytes into the state vector.
A specification covering them would have to *assume* them correct, so it would model
the machine faithfully and miss the two worst defects found so far — a forged
collection frame and a record split by line wrapping — both parser bugs.

The split is deliberate: the checker owns memory and ownership, tests own everything
that produces the state vector. `InvariantTests` in the test project enumerates the
verdict and confidence product exhaustively for the same reason.

## Correspondence with the implementation

A model checked exhaustively proves things about the model. It says nothing about the
code unless the correspondence is demonstrated — an unchecked correspondence is how a
specification ends up describing a system nobody built.

`ModelCorrespondenceTests` in the test project mirrors each definition against the real
implementation, named for what it mirrors:

| TLA+ | Test |
| --- | --- |
| `SoleClaimantIsAlwaysOwner` | `SoleClaimantIsAlwaysOwner` |
| `AtMostOneOwner` | `AtMostOneOwner` |
| `NeverActOnUnwitnessedOrder` | `NeverActOnUnwitnessedOrder` |
| `NoCrossEpochMemory` | `NoCrossEpochMemory` |
| `RegistrationStableStep` | `RegistrationStableStep` |
| `OwnerStableAcrossSweepStep` | `OwnerStableAcrossSweepStep` |
| `Observed` | `ObservedRequiresAWitnessedRegistration` |
| `NoOwnerFromRetiredHost` | `RetiringAHostRemovesItAndClearsItsClaims` |
| `Add` | `AddingAHostDeclaresItAndReDeclaresLocalAfterRetirement` |

The model is the authority on ordering and memory; those tests are the evidence the C#
agrees with it.

## Validating the spec itself

A specification that passes proves nothing until you have seen it fail. Each invariant
here has a mutation that breaks it:

| Mutation | Breaks |
| --- | --- |
| drop the epoch check in `Registered` (the real pane-id bug) | `NoCrossEpochMemory` |
| let two unwitnessed claimants be ordered | `NeverActOnUnwitnessedOrder` |
| make a sole claimant unownable | `SoleClaimantIsAlwaysOwner` |
| let any registered claimant own, not only the first | `AtMostOneOwner` |
| re-register every window on every sweep | `RegistrationStableStep` |
| sort unregistered windows first instead of last | `OwnerStableAcrossSweepStep` |
| drop the `viewComplete` guard from `OwnsClaim` | `NoOwnerWhileViewIncomplete` |
| let a partial sweep rewrite registrations | `NoPhantomDepartureStep` |
| retire a host but leave it in `lastCollected` with `viewComplete` still true | `NoOwnerFromRetiredHost` |
| let a registration count against a fleet it was not made against | `OwnerStableAcrossSweepStep` |
| `Add` a host without re-stamping a collected window's registration fleet | `OwnerStableAcrossSweepStep` |

Every invariant and property in the config has an entry, which is the bar for calling
the run clean. A mutation must also be attributed to the *intended* property: an early
attempt at the last row was caught by `RegistrationStableStep` instead, which says
nothing about whether `OwnerStableAcrossSweepStep` checks anything. The mutation listed
leaves `regTime` untouched, so only the property under test can fire.

That exercise earned its keep twice. TLC refuted `RegistrationStableStep` on the first
run — the property, not the design, had forgotten that a window switching PRs re-registers.
It earned it a third time when the partial-view rule arrived. `SoleClaimantIsAlwaysOwner`
asserted that a sole claimant is *always* actionable, which stopped being true the moment
a sweep could fail to reach a host — you cannot know a claim is sole if you did not look
everywhere. TLC found the conflict between the two rules in two steps. The anti-degenerate
property is now conditioned on a complete view, which keeps its job (ruling out a tool
that never acts) without overruling the safety rule.

And mutation found that `NeverActOnUnwitnessedOrder` was originally a **tautology**:
phrased as `OwnsClaim(w) => Observed(...)`, it restated a definition, since `OwnsClaim`
already requires `Observed`. TLC cannot report a vacuous invariant as a failure — it
passed happily with the guard it was meant to protect deleted.
