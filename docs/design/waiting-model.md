# The waiting model

*What `octoshift waiting` holds, how it changes, and what must never be true.*

The tool joins three things that change independently — what an agent says about
itself, what tmux shows, and what GitHub reports — and decides, for each window,
whether anything may be said to it. That decision has accumulated enough moving
parts to be worth writing down as a machine rather than as prose.

This document exists for two reasons. It is the reference for anyone changing the
decision path, and it is the specification a model checker would be written
against.

---

## 1. Where state lives

Three stores, with very different lifetimes. Most defects so far have come from
confusing one for another.

| Store | Lifetime | Authority over |
| --- | --- | --- |
| **tmux** (window options, names, pane text) | until the tmux server restarts | what an agent claims about itself |
| **GitHub** | durable | what is true of a PR |
| **`panes.json`** | across runs of the tool | what the tool has *witnessed* |

The third is the interesting one. It holds nothing that can be re-derived: when a
window first claimed a PR, when its output last changed, and which tmux server
its pane ids belonged to. Everything in it is a memory of an observation, and an
observation cannot be recovered after the fact — which is why it is persisted at
all, and why it must be discarded rather than repaired when its basis is gone.
Because a `waiting` and a `pr` can run at once and share this one file, each takes
a cross-process lock for the whole load-reconcile-save transaction: a run reads,
updates and writes under the lock so a concurrent run cannot interleave and lose
an update, and the write itself is atomic (temp file then rename). A lock that
cannot be taken, or a write that fails, is surfaced as unavailable — never a
success that leaves a stale witnessed order on disk.

---

## 2. The per-window state vector

A window's row is a product of these, all derived fresh each sweep except where
noted.

```
identity     none | issue(N) | pr(N)              from @agent_state, else window name
source       Declared | WindowName
activity     Idle | Working | Blocked | Unreadable
recommend    None | Continue | Wait | Merge | Approve | Stop | Unrecognised
waiting      None | Check(name) | Checks | Merge | Review
reviews      clean/required, or absent
blocked      set of issue or PR numbers
defects      set of self-contradictions found while parsing
silence      duration | unknown                   ← remembered across sweeps
claim        Sole | Owner | Follower              ← remembered across sweeps
basis        Uncontested | Observed | Inferred    ← remembered across sweeps
```

And, for the PR it names:

```
lifecycle    open | closed | merged(at)
mergeable    clean | dirty | unknown | behind | blocked | draft | unstable | absent
checks       known(set) | unknown
head         sha
```

## 3. The verdict

One of thirteen, each paired with an owner and a confidence:

| Verdict | Owner | Means |
| --- | --- | --- |
| `Ready` | operator | reviews meet the bar and the branch merges |
| `Unblocked` | operator | the agent's own declared wait has cleared |
| `NeedsOperator` | operator | a person must decide before anything moves |
| `Contradicted` | operator | declared done; GitHub disagrees |
| `Stale` | operator | the record describes a head GitHub has left |
| `Untrustworthy` | operator | declared done, but the record contradicts itself |
| `NotMergeable` | operator | declared done; GitHub does not affirm mergeability |
| `MergeUnverified` | either | GitHub has not computed mergeability |
| `Merged` / `Closed` | operator | the window's work is over |
| `Conflicting` | agent | mid-work, and the branch does not merge |
| `Holding` | nobody | in progress, or legitimately parked |
| `Unknown` | operator | GitHub could not be read |

**Ordering is part of the meaning.** The gates are evaluated in a fixed sequence,
and each one that fires ends the evaluation:

```
lifecycle (merged/closed)  →  head divergence  →  operator requests (stop/approve)
  →  self-contradiction  →  missing head  →  conflict  →  mergeability unknown
  →  declared wait  →  incomplete reviews  →  affirmative mergeability  →  Ready
```

Every gate fails closed. A claim that cannot be checked is not a claim that
passes.

## 4. What may be acted on

`MayAct` is the only output with consequences, and it is a conjunction of three
independent judgements:

```
MayAct  ⇔  verdict ∈ {Ready, Unblocked}      the state warrants speaking
        ∧  confidence = High                  the evidence supports it
        ∧  claim owns the PR                  this window is entitled to hear it
```

The three are deliberately separate. A window can be in a perfect state with
untrustworthy evidence; it can have flawless evidence and be the second agent on
a PR. Neither may be spoken to.

## 5. Transitions

The machine advances on a sweep. Between sweeps, the world moves on its own:

| Event | Effect |
| --- | --- |
| agent publishes state | identity, recommendation, reviews, waits change |
| agent emits output | body digest changes; silence resets |
| agent stops | activity → Idle; nothing else changes |
| window created / destroyed | claims re-ranked |
| PR merges, head moves, checks report | verdicts change under an unchanged record |
| **tmux server restarts** | that host's pane ids restart at `%0`; only its remembered claims are void, once it is next collected under its new epoch, and other hosts are untouched |
| host has no tmux server | a successful empty observation — the machine answered, it just holds no windows |
| host unreachable | that host contributes no rows; others are unaffected |

The last two are why the tool records an epoch **per host**. A restarted server
makes remembered pane ids name different windows, so the memory is not merely
stale — it is actively misleading, and confidence would launder it. Restarts are
per host because the fleet is many independent tmux servers: one machine rebooting
says nothing about the others, so only that host's epoch advances and only its
registrations are invalidated. And the restart itself changes nothing on disk: the
tool cannot rewrite what it has not collected, so a host's remembered claims are
voided the next time it is swept and its epoch is seen to have changed, not at the
moment of the restart. An empty observation records **no** epoch (no server was
running to have one), so the first window that later appears on that host begins
under a new, unknown server generation — a server *start*, indistinguishable from a
restart to the tool. Its claim is therefore a fresh, unwitnessed registration until a
continuous sweep records it, so an empty host cannot launder a first look into a
witnessed order.

## 6. Invariants

These are the properties the machine is supposed to have. They are stated
separately from the code because they are what a checker would check, and
because several of them were violated by code that passed its unit tests.

**Safety — nothing is spoken to on bad grounds.**

1. `MayAct ⇒ claim ≠ Follower` — never drive the second agent on a PR.
2. `MayAct ⇒ basis ≠ Inferred` — never drive an ownership nobody witnessed.
3. `MayAct ⇒ confidence = High`.
4. `MayAct ⇒ defects = ∅`.
5. `MayAct ⇒ head is present and matches GitHub`.
6. `Ready ⇒ reviews ≥ the two-clean bar ∧ mergeability affirmed`.
7. `mergeable ∈ {unknown, absent} ⇒ ¬Ready`.
8. At most one window per PR has `claim = Owner`.

**Temporal — memory behaves.**

9. If nothing observable changes between two sweeps, the owner of a PR does not
   change. *(An owner whose identity flips is worse than no owner.)*
10. After a host's epoch changes, no claim on that host has `basis = Observed`
    until the tool has witnessed those windows registering again.
11. A contested claim has `basis = Observed` only when every claimant's
    registration was *witnessed* — recorded while the tool was already watching
    its host under a complete view — and that witness is persisted with the
    registration, not recomputed each sweep. *(A claim first recorded under a
    narrow view stays untrusted across every later sweep, even once the whole
    fleet is collected; only a release and a witnessed re-registration can
    establish a trustworthy order. Recomputing trust from the current sweep's
    coverage is what let the third sweep of a full fleet promote a first look.)*
12. A window that stops claiming a PR — going quiet, publishing an issue, or a
    malformed record — clears its registration and its witness, so a later
    reclaim is a fresh registration that cannot inherit its old place in the
    queue. The same holds across a *gap*: a host absent from the previous sweep
    and collected now may have released and reclaimed unseen, so its window's
    registration and witness are reset rather than preserved. Continuity is
    membership in the previous sweep's collected set, not merely an unchanged
    epoch and claim.
13. A window's silence duration never decreases while its body digest is
    unchanged.
14. A claim's registration time — and its witness — are stable for as long as the
    window keeps claiming the same PR *across continuous observation*. *(The place
    in the queue and the trust in it travel together: a later sweep that finally
    sees the whole fleet cannot flip a continuing first look's witness from false
    to true, which is the temporal half of the fleet-expansion fix. Modelled as
    the `RegistrationStableStep` and `RegWitnessedStableStep` step properties,
    whose antecedents require the host to have been in the previous sweep
    (`HostOf(w) ∈ lastCollected`) — so a gap-return is free to reset both, but
    continuity freezes both.)*

**Liveness — the tool does not go quiet by accident.**

15. An unreachable host is reported, never absorbed into an empty result. A host
    with no running tmux server is the exception that proves the rule: it *did*
    answer, so it is a successful empty observation — recorded as a host seen to
    hold no windows, distinct from one that could not be read. Only a missing or
    broken tmux (a missing binary, a permission error, malformed output) is
    unreachable.
16. A sweep that collects nothing reports failure rather than an idle fleet.
17. A completed sweep persists its memory — including breaking the continuity of
    every host it did not collect, even a total failure. Persistence is
    load-bearing, so a write that fails is reported as unavailable rather than
    left as a success a later run would read as current.

Invariants 9–14 are the ones unit tests cover least well, because they are
statements about *sequences* of sweeps interleaved with fleet events rather than
about a single decision. Every defect found in this area so far — pane ids reused
across a server restart, silence measured from a repainting footer, ownership
ranked by collection order — was of that shape.

## 7. What a model checker would and would not help with

Worth being specific, because the answer is not "all of it".

**Would help.** The temporal invariants above, over a bounded fleet: a few
windows, a couple of PRs, a handful of sweeps, with server restarts, window
churn, and PR lifecycle events interleaved freely. That is a small state space
and exactly the interleaving that hand-written tests sample rather than cover.

**Would not help.** Parsing, REST semantics, tmux formats, ssh transport. These
are where the *inputs* come from, and a specification would have to assume them
correct — so it would model the machine faithfully and miss the bug entirely, as
it would have for the framing and hard-wrap defects.

The useful split is therefore: the checker owns the memory and ownership rules;
tests own everything that turns bytes into the state vector.
