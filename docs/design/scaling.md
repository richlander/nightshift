# Scaling signal

**How many workers a ready set warrants — and how the shift asks for them.**
*Count the frontier, not the backlog. The coordinator informs; the operator staffs.*

*Draft spec v0.1 — Rich Lander, July 2026*
*Built on [Nightshift](nightshift-spec.md) and [Turnstile](turnstile.md). Not yet built — this is the map.*

---

## Summary

A shift with ready orders and too few workers leaves throughput on the table, and today the
[Coordinator](workflow.md) has only a binary way to say so: it flags the **zero-worker** case
("orders are ready and nobody is on") and nothing finer. It has no notion of *how many* workers a
given ready set warrants, and no way to say "start two more."

This note resolves both halves of [issue #30](https://github.com/richlander/nightshift/issues/30):

1. **Guidance** — a concrete rule of thumb for the warranted worker count, grounded in the model
   Nightshift already has: *the DAG's antichain width is the parallelism ceiling*
   ([spec §5, "The DAG is the scheduler"](nightshift-spec.md)). The useful worker count is bounded by
   the number of mutually **independent ready orders**, not the size of the backlog.
2. **Backpressure signal** — a graded generalization of the zero-worker flag. When the active roster
   is smaller than the warranted count, the coordinator surface says "start N more"; when it is
   larger, it already says "idle." The number is **derived from Turnstile state** (ready set + roster)
   by the coordinator surface — not a new coordination primitive inside the kernel.

One invariant sits above both and is the crux of the issue:

> **The Coordinator never spawns workers.** Every scaling action is a *prompt to the operator*. The
> signal informs; it never acts.

---

## 1. What we are counting

Three numbers, all already present in the system, drive everything below:

| Symbol | Name | Where it comes from |
|---|---|---|
| `R` | **ready width** — orders ready to claim *right now* (deps done, unclaimed) | Turnstile state; the `ready:` line of `nightshift board` (`3 wide`) |
| `A` | **antichain width** — the widest the plan's DAG ever gets | the plan's order→order DAG, computed once at registration |
| `N` | **active roster** — workers currently on, holding or renewing a lease | `nightshift roster` |

`R` is the **instantaneous frontier**; `A` is the **shift-level ceiling**. At any instant the ready
set is an antichain of the DAG, so `R ≤ A` always. The distinction matters: `A` tells the operator
how many sessions the shift could *ever* keep busy, while `R` tells them how many would find work *in
the next minute*. Backpressure is computed from `R`; long-horizon staffing is bounded by `A`.

A worker that claims an order past the frontier does not exist — there is nothing to claim. So the
warranted count is a property of the frontier, never of the backlog. **A 40-order plan that is one
order wide warrants one worker.**

---

## 2. Guidance — how many workers?

The warranted worker count at any moment is:

> **`W = min(Rᵉ, C)`**

- **`Rᵉ` — effective ready width.** The ready count `R`, after collapsing orders that cannot truly
  run in parallel because they share a coarse `paths` scope. Two ready orders scoped to the same
  broad glob will serialize on the conflict graph even though the DAG calls them independent, so they
  warrant **one** worker between them, not two. `Rᵉ ≤ R`.
- **`C` — a sane per-machine cap.** Workers contend for RAM, I/O, tokens/min, and CI budget; past a
  point another session buys tokens, not throughput. `C` is an operator-set ceiling (a small default,
  e.g. 4–6, tunable per machine), not a Nightshift-computed value.

For sizing the roster across the whole shift rather than for this instant, replace `Rᵉ` with `A`: the
roster never usefully exceeds **`min(A, C)`**, because `A` is the most parallelism the plan can ever
offer.

### Diminishing returns are the whole point

The rule is a *ceiling*, and three effects make exceeding it strictly wasteful — the same three the
spec already names:

- **Nothing to claim.** "Adding a sixth agent to a three-wide frontier buys nothing but tokens"
  ([spec §5](nightshift-spec.md)). Beyond `R`, workers `next` and block.
- **Coordination overhead overtakes parallelism.** The spec's measurement plan tracks *"marginal
  throughput per added agent — where coordination overhead exceeds parallelism gain"*
  ([spec §16](nightshift-spec.md)). `W` is the estimate of where that curve turns over.
- **Coarse scopes serialize.** *"How coarse can path scopes be before serialization eats the
  parallelism?"* is an open question in the spec ([§20](nightshift-spec.md)); until it is answered,
  `Rᵉ` conservatively discounts ready orders that share a scope.

### Worked example

```
$ nightshift board 3
ready:      op-6, op-8, op-9          (3 wide)
in flight:  op-5 (dev-b, 22m)
blocked:    op-10, op-11  →  op-5
roster:     1 session
→ ready 3, warranted 3, active 1 — start 2 more workers.
```

`R = 3`, no shared scopes so `Rᵉ = 3`, `C` not binding, so `W = 3`. One worker is on, so the deficit
is `2`. Contrast the spec's existing over-staffed board — five sessions on a three-wide frontier — where
the same arithmetic yields "2 sessions idle." Same formula, opposite sign.

---

## 3. Backpressure — the graded signal

Today's rule is a step function: `N = 0 ∧ R > 0` → tell the operator to start a worker
([coordinator skill, the ready-set discussion](../../.github/skills/nightshift-coordinator/SKILL.md)).
This generalizes it to the **staffing deficit**:

> **`D = W − N`**

| Condition | Surface says |
|---|---|
| `D > 0` | **understaffed** — "start `D` more workers" |
| `D = 0` | quiet — the frontier is matched |
| `D < 0` | **over-staffed** — "`|D|` sessions idle" (the existing board line) |

The zero-worker flag is just the special case `N = 0`. Nothing about the current behavior changes; it
becomes the bottom rung of a graded ladder, and the message gains a **number**.

### Where the signal lives

**In the coordinator surface, computed from Turnstile state — not in the kernel.** Turnstile knows
the ready set and the roster; `A` is a static property of the registered plan; `Rᵉ`, `W`, and `D` are
pure functions of those. So the signal is a **derived view line on `nightshift board`**, alongside the
"idle / bottleneck" line the board already prints. `board` is where the operator already looks to
decide whether to intervene; the deficit belongs next to the frontier it describes.

This keeps the kernel a narrow building block, per the project's partitioning philosophy
([vision, "Extending capability"](nightshift-vision.md)): Turnstile gains **no** scaling primitive, no
"desired replica count," no autoscaler. It continues to answer only "what is ready" and "who is on."
The arithmetic that turns those two facts into "start 2 more" is coordinator-side presentation, and a
future gh-aware layer ([Octoshift](octoshift.md)) could relay the same computed line without Turnstile
ever learning the word "scale."

| Option | Verdict |
|---|---|
| New Turnstile primitive (desired-count / autoscale) | **Rejected.** Violates the narrow-kernel rule; bakes policy (`C`, scope discounting) into the credential-free coordination path. |
| Computed hint on the `board` view, from ready + roster + width | **Chosen.** Deterministic, cheap, lives where the operator already reads, leaves the kernel untouched. |
| Pure coordinator judgment, nothing computed | Insufficient. The operator asked for a *number*; leaving `D` un-surfaced reproduces today's guesswork. |

Board **computes and displays** `D`; the Coordinator **reads** it and exercises judgment about whether
to prompt (a deficit during a `drain` is expected and should stay quiet); the operator **acts**.

---

## 4. The hard boundary — inform, never act

The signal is a **prompt to the operator**, full stop. When the board shows a deficit and the
Coordinator judges it worth surfacing, the operator — a human, or an agent acting as operator — opens a
new session and tells it:

> **you are a nightshift worker**

That loads the `nightshift-worker` skill; the new worker sets up its own worktree, `join`s, and pulls
work on its own. The Coordinator does **not** open the session, does **not** `join`, does **not** hand
out the order.

This is not a limitation to route around; it is the same boundary stated everywhere else in the
system. The Coordinator *"never becomes a worker and never spawns one"*
([coordinator skill](../../.github/skills/nightshift-coordinator/SKILL.md)); a Worker is *always a
separate instance* ([workflow](workflow.md)). A scaling signal that spawned workers would collapse
exactly the day-shift/night-shift and coordinator/worker partitions Nightshift exists to keep. The
number tells the operator how many sessions to start; starting them stays a deliberate act on the
human's side of the line.

---

## 5. Open decisions

- **Cap values.** The default `C` and whether it is one number or a vector over RAM / tokens-per-minute
  / CI budget (the spec's admission-control axes) is unsettled and probably repo- and machine-shaped.
- **Scope discounting.** `Rᵉ` currently assumes shared coarse `paths` fully serialize. The real
  discount depends on the conflict graph and on the spec's open "how coarse can scopes be" question;
  the first cut can treat same-scope ready orders as one and refine once the conflict graph informs it.
- **Board line vs. coordinator judgment.** This note puts the computed deficit on `board`. Whether to
  also emit it from `roster`, or to gate the prompt behind an explicit `nightshift board --staffing`
  rather than always printing it, is a presentation choice to settle in implementation.
- **Cross-machine pooling.** With a laptop and desktop as one pool, `C` is per-machine but `W` is
  plan-wide; how the deficit is attributed across machines is deferred to the cross-machine work.
