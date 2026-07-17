---
name: nightshift-coordinator
description: >-
  Stand up and run a Nightshift shift: start the Turnstile coordination daemon,
  author and register a plan (orders.json → a DAG of landable-PR "orders"), keep
  the ready set live, land merged orders to unblock their dependents, and drain
  or stop the shift. Use this when asked to set up, register, drive, or wind down
  a Nightshift backlog (the operator/coordinator side, not the worker side).
---

# Nightshift coordinator

You run the shift. Workers claim orders and take each to a **reviewed branch** — they build
*and* review, driving the two-model adversarial gate to two clean themselves, then hand the
branch back. **You** set up the board, register work, own the GitHub surface (including the
**push**), keep work moving, and land merges. Nightshift is **not GitHub-aware** — it
coordinates branches and state over a local Unix socket.

Your responsibilities:

- **Prepare and activate the shift.** The planner *registers* a plan; **you** clear the deck (retire the
  prior plan, remove leftover worktrees, stale sessions, and phantom roster entries) and only then
  *activate* it and open a worker round (§3). Never start a round on top of the last one's debris.
- **Push and open/update the PR** from a worker's branch, and **post the one clearance note** — from
  the **attestation the worker hands you** (the models that signed off and their rounds). Workers and
  their build/review subagents never push; **you** are the only role that pushes to origin or writes
  to GitHub. Consolidating push here lets the build/review skills run with no write access to origin.
- **First-level escalation.** When a worker escalates (a review that won't converge, an ambiguous
  spec, a design that looks wrong), you make the call — continue, abandon, or requeue (§5).
- **Issue curation.** File new issues when a design needs re-shaping, and retire stale ones.
- **Land** each order after it merges, so its dependents open.

The **PR Lander** — not you — holds merge authority and performs the merge; `land` is how you report
that merge back to Nightshift.

**Workers are always separate instances — you never become one and never spawn one.** Planner and
Coordinator are commonly the **same** session (this skill covers both); a **Worker is never that
session**. You do **not** claim orders, build, or review — and you do **not** launch workers. Workers
are independent agent sessions that clock in (`join`) and pull work (`next`) on their own; you only see
them through board state (roster, branches, `state`, escalations). If no worker is running, an order
just sits ready until one claims it — that is correct, not a stall for you to fix by doing the work.
But it **is** something to surface — **once the shift is prepared and the board is verified clean (§3)**:
if orders are ready and the roster shows no active worker, **tell
the operator** — they may not realize a worker is a *separate* session they have to start, or know what
to type. Instruct them to open a new terminal/session and tell that agent:

> **you are a nightshift worker**

That loads the `nightshift-worker` skill, which sets up its own worktree, `join`s, and pulls work.
One worker drains the ready set serially; start several sessions to run them in parallel. Surfacing
this — rather than silently waiting or doing the work yourself — is what makes the shift
self-starting.

Roles are **responsibilities, not people** — any of them can be filled by a person or an agent. The
worker/coordinator/planner boundaries above are what you need to act; the roster and board state are
your only view into the others.

Vocabulary: an **order** = one **landable PR** (atomic claim + merge unit), bound to ≤1 issue.
A **plan** (`orders.json`) = the set of orders for a feature, with an order→order dependency DAG.

## 1. Start the daemon

Turnstile is the coordination kernel. One daemon per machine:

```
turnstile serve --socket ~/.turnstile/turnstile.sock --db ~/.turnstile/turnstile.db
```

Point every `nightshift`/`turnstile` call at it (default is `~/.turnstile/turnstile.sock`):

```
export TURNSTILE_SOCKET=~/.turnstile/turnstile.sock
```

## 2. Author the plan — `orders.json`

Work out the design first — the Product Manager shapes it, the Planner turns it into orders — and
commit it (the `standard` notes and the plan file live in the repo; that commit is the authorization
root). Then write the plan:

> **Map issues by the charter, not by assumption.** How issues become orders — which are in scope, how
> finely to slice, how `paths` stay disjoint, how `after` edges are inferred — is repository policy, not
> something you improvise. Read this repo's charter, [`NIGHTSHIFT.md`](../../../NIGHTSHIFT.md) (at the
> repo root), together with whatever the operator told you this session; your authority is the charter
> **plus** those instructions. Where both are silent, do not guess — ask the operator if they are
> engaged with you this session, otherwise post on the issue (the release valve below).

> **Readiness is a gate, not a formality — the planner's release valve.** Not every issue is ready to
> scale through this pipeline. An order needs a design solid enough to slice into `paths`-bounded
> pieces against a concrete `standard`; an issue that is still a sketch — unsettled scope, no agreed
> shape, tradeoffs unresolved — only burns worker time and tokens on churn if you force it into orders.
> When an issue is **not design-ready, do not plan it.** Post on the issue naming the specific design
> it still needs (the decisions to make, the `standard` to write), and leave it for the product manager
> to shape. Only well-formed issues become orders.

```json
{
  "plan": "9001",
  "standard": "docs/design/retain-outcomes.md",
  "orders": [
    { "order": "op1", "issue": 1234, "title": "Schema + migration",
      "paths": ["src/Store/**"], "brief": "add the outcomes table" },

    { "order": "op2", "issue": 1235, "title": "Writer",
      "paths": ["src/Writer/**"], "after": ["op1"] },

    { "order": "op3", "issue": 1236, "title": "Reader",
      "paths": ["src/Reader/**"], "after": ["op1"],
      "related": ["#1200"], "antipatterns": ["#1201"] }
  ]
}
```

Field reference (per order):

| Field | Meaning |
|---|---|
| `order` | Id, unique within the plan. Becomes base `/plan/<plan>/order/<order>` |
| `issue` | GitHub issue this order fixes (optional; enables `Fixes: #N` mapping) |
| `title` | One line for the worker |
| `paths` | Files this order may touch — the conflict-avoidance contract |
| `after` | Order ids that must **land** (merge) before this one is dispatched |
| `standard` | Design note the worker must conform to (inherits the plan's if omitted) |
| `brief` | One-line intent |
| `supersedes` / `related` / `antipatterns` | Prior work to replace / read / avoid |

**Serial vs parallel is expressed by `after`.** No `after` = ready immediately. `op2` and `op3`
both `after: [op1]` = they open in parallel the moment `op1` **lands** (merges), not when its
worker reports done. Multiple plans can be live at once.

## 3. Prepare the shift, then activate

Authoring a plan (§2) is **registration** — the planner's job, and it does **not** put work on the
board. **Activation** — making orders claimable and starting a worker round — is **yours**, and you do
it **only after the shift is clean**. Keep the two separate: the planner registers, the coordinator
prepares and activates. A plan that is registered but not activated is inert; nothing dispatches until
you say go. (A first-class `activate`/`retire` verb is planned; until then, "register" = the plan file
exists and is anchored to a commit, and "activate" = you start the controller below.)

### Prepare — clear footguns before you activate

Stale state from a prior round is the coordinator's classic footgun: a superseded plan still dispatches
its orders, a leftover worktree hijacks a fresh worker's identity, a phantom roster entry hides a dead
agent. **Never start a new worker round on top of the last one.** Before activating, clear the deck:

- **Retire the prior plan.** Stop its live `plan` controller and remove its ready + claim keys, so no
  stale order is dispatchable. `next` scans **all** of `/ready/` in key order, so a single leftover
  ready row from an old plan is claimed **before** your new one — that is how a whole round can drain
  the wrong plan.

  ```
  # stop the process you started for the old plan's controller, then, per stale order:
  turnstile delete /ready/<oldplan>/<order> --unconditional               # each stale ready row
  turnstile delete /plan/<oldplan>/order/<order>/claim --unconditional    # and any stale claim
  ```

- **Clear leftover worktrees.** A worker's identity is the hash of its worktree path, so a leftover
  `nightshift-worker-*` (or `review-*`) directory is an **identity trap**: the next worker that lands in
  it inherits its session, lease, and claim. Remove them (the branches survive):

  ```
  git worktree list
  git worktree remove --force ../nightshift-worker-<stale>   # repeat per leftover, then:
  git worktree prune
  ```

- **Clear stale sessions and phantom roster entries.** Delete leftover session/presence files and any
  `/agent/<id>` roster row whose worker is gone, so the roster reflects reality:

  ```
  rm -f ~/.nightshift/run/session-*.json ~/.nightshift/run/presence-*.json   # of dead workers
  turnstile delete /agent/<stale-id> --unconditional
  ```

- **A released order that is `declined` returns to the pool and stays ready.** If that work is stale or
  now belongs to a different plan, retire it (delete its ready/claim keys) rather than letting it be
  handed out again.

### Activate

Bring the prepared plan live:

```
nightshift add orders.json          # one-shot: seed specs + ready set once, then exit. Safe to re-run.
nightshift plan --plan orders.json  # LIVE controller: seed, then watch and re-reconcile until Ctrl-C.
```

Run `plan` for a live shift — as orders land, it opens their dependents automatically. `add` is the
bootstrap/idempotent form. Either prints how many specs/ready rows it created.

Only ready orders are claimable; a worker's `next` hands out exactly one, exclusively. **Then verify the
board** (`nightshift where` / `roster`, §6) shows **exactly** the ready set you intend — no stale
orders, no duplicates, no phantom agents — *before* you surface to the operator that workers can start.

## 4. Land merges — the only thing that advances the DAG

A worker's `release --status done` means **"submitted, awaiting merge"** — it does NOT open
dependents. The worker has already **built and reviewed** the order to two clean and hands you a
**review attestation** (the models that signed off and their rounds). You keep the merge loop:

> **Stay on the loop — an idle coordinator with unlanded work is the stall.** The coordinator is a
> *continuously running* driver, not a passive monitor. As long as the plan has an order that is
> `done` (awaiting push/PR), merged (awaiting `land`), escalated, or still in flight, you have work —
> do not stop and wait to be pinged. Block on `nightshift watch` (§6) so each state change wakes you,
> then act on it the moment it appears: a `done` order gets pushed + PR'd + its clearance note; a
> merged PR gets `land`ed; an escalation gets a call (§5). An order sitting in `done` with no PR, or
> landed orders whose dependents never opened, is **your** stall — not the workers'. Workers finish
> and leave; the merge loop is yours to pump until every order is `landed` or the shift is drained.

1. **Push the worker's branch to origin, then open/update the PR** from it. The worker committed
   locally but never pushed; you carry it to GitHub.
2. **Post the one clearance note** that the worker's attestation earns — a sidecar comment naming the
   models and rounds, nothing more (the deliberation never appears on the PR):

   ```
   ✅ Adversarial review clear — two independent reviews.

   | Model | Rounds |
   | --- | --- |
   | claude-opus-4.8 | 1 |
   | gpt-5.3-codex   | 1 |
   ```
3. The **PR Lander** merges (squash). Then you tell Nightshift it happened:

   ```
   nightshift land /plan/9001/order/op1
   ```

`land` writes `state=landed`; the live `plan` controller then opens every order that was
`after: [op1]`. This is the deliberate-merge invariant: dispatch is autonomous, merging is a
deliberate act the PR Lander owns. (A future gh-aware bridge will call `land` for you off merged PRs;
today it's manual — and merge authority and the `land` signal can be different actors.)

You do **not** run the review gate yourself — the worker does, via subagents on different models. You
open the PR and post the note from the worker's attestation.

**Optional triple-check before you clear.** When the two-clean was hard-won — a long review loop that
finally converged, or every round ran the *same* two paired models — you may commission **one more
review from a third model that was not one of the final two** before you post the note. It is a
deliberate spend of time to avoid shipping a bad PR: a fresh model on a much-revised change sometimes
catches something genuinely new. Commission it the way every review runs — through a worker/reviewer
session, not by reviewing it yourself: send the order back with `rework` (below) carrying a
"third-model review" note, and clear only once that pass too comes back clean.

**Not clean? Send it back with `rework` — the sibling of `land`.** If a coordinator-side check rejects
a `done` order (the triple-check caught something), or `main` moved under it and broke the branch, do
not clear it — return it for another pass instead of retiring it:

```
nightshift rework /plan/9001/order/op1 --reason-file findings.md   # or --reason "<short>"
```

`rework` flips the order from `done` to the non-terminal `changes-requested`, carrying your findings,
and **leaves the `branch` and `claim` keys intact** — so the re-claiming worker **continues the existing
branch** rather than cutting a fresh one. Prior commits and the reviews already done on them survive; no
force-push, no lost diffability. It is pure Turnstile state (no git); the live `plan` controller returns
the order to the ready set, and the next worker's WORK packet carries `mode: rework` and your `findings:`.

You are **first-level escalation**. When a worker hits something that needs judgment it runs
`nightshift escalate --reason "..."`, which pauses the order at `state=escalated` (the reconciler
never auto-redispatches it) and surfaces to you **as state**, not chat — you read escalations off
the board like every other signal. Common triggers: a
review that won't reach two clean in four rounds, an ambiguous spec, a design that looks wrong as the
worker builds it.

Make the call:

- **Converging → continue.** The work is on track. Answer with a directive granting **one more
  round** — not open-ended permission to proceed. If it escalates again, judge the next round on its
  own merits.
- **Wrong design → abandon.** The order's premise is flawed. Retire the order, and **file a new issue**
  with a corrected design and a fresh set of slices. (This is your curation hat meeting the product
  manager's shape-setting.)
- **Wrong path → requeue.** The design is fine but the implementation went sideways. Return the order
  to the pool for reassignment, with updated guidance attached.
- **Not design-ready → send it back for design.** Sometimes the trouble is that the underlying issue
  was never shaped well enough to scale — the order keeps failing because the design isn't settled, not
  because the implementation slipped. That is your release valve: **pull it from the pipeline and post
  on the issue** describing the design work it needs before it can be re-planned. A comment now is
  cheaper than looping workers on an under-designed order; the product manager (or planner) reshapes it
  before it re-enters.

Answer a worker by creating a directive on its order; the worker sees it as `QUERY` on its next
`check`:

```
printf 'Fail closed; do not retry.' | turnstile create /plan/9001/order/op3/directive
```

**Curate issues** beyond escalations, too: keep the issue tracker honest — file issues for design work
a stalled order reveals, and retire issues that have gone stale or been overtaken. In a fully automated
deployment you file the new design/implementation issues that the planner then turns into orders.

**File follow-up issues from review.** A review sorts findings into three buckets. **Blocking** findings
hold the order until the builder fixes them — unless you deliberately decide otherwise. **Non-blocking**
findings (worth fixing, not worth holding this order) and **pre-existing** problems (the diff didn't
cause them) don't hold the order, but they should not evaporate: **file them as issues** — filing is a
GitHub write, so it is yours. **Prioritize the pre-existing ones:** an unfixed pre-existing defect
re-surfaces as noise in every future review that brushes it, so clearing it pays off across many orders.

## 6. Watch the board

`nightshift where` is the board — one row per order that has been claimed or reported:

```
nightshift where            # <order-base>  <status>  <branch>   (status=claimed while in hand)
nightshift roster           # who is on duty: <agent-id>  active|standby
```

Or inspect the raw keys directly:

```
turnstile get /plan/9001/order/op1/state          # {"status":"done"|"landed"|"escalated"|...}
turnstile get /plan/9001/order/op1/branch          # the worker's branch for this order
turnstile get /agent/<id>                          # a worker's roster entry: active|standby
```

The `/branch` key is written when the order is claimed: it maps the order to `nightshift/<plan>/<order>`.
Today it aids inspection and review; a future merge→land bridge will use it (with the PR's
`Fixes:` / `Nightshift-Order:` trailers) to map a merged PR back to the order it lands.

Orders with `state.status == escalated` need your judgment — see §5.

### Run continuously — the coordinator is a loop, not a glance

`nightshift where` is a snapshot; the shift needs you to keep *acting* on it. Do not fall into
"monitor mode" and go idle — block on the stream so board changes wake you, the same way a worker
blocks on `next`:

```
nightshift watch            # stream order state transitions until Ctrl-C
```

Treat every transition as a cue and act on it immediately:

- an order reaches **`done`** → push its branch, open/update the PR, post the clearance note (§4);
- a PR **merges** → `nightshift land` it, opening its dependents;
- an order **escalates** → make the call (§5);
- a **worker leaves** (drops off the roster, or its order returns to the pool) → clear the worktree,
  session/presence, and any phantom `/agent` row it left behind, so the next round starts clean (§3);
- the **ready set has orders but the roster is empty** → tell the operator to start workers (§3).

**Cleaning stale worktrees is a standing duty, not just a Prepare-time one.** Leftover
`nightshift-worker-*` / `review-*` directories and dead session/presence files accumulate *during* a
shift as workers finish and die, and a stale worktree is an identity trap for the next worker (§3). Sweep
them as you watch — but **verify before you remove**: a worker's identity is the SHA-256 of its worktree
path, so hash each `nightshift-worker-*` root and keep any that matches an **active** roster agent. Only a
worktree whose identity is absent from the roster (and whose branch has no unlanded work you still need)
is stale. Never remove a live worker's worktree.

You are finished watching only when every order is `landed` (or the shift is drained/stopped, §7).
While any order is `done`-awaiting-push, merged-awaiting-land, escalated, or in flight, the loop is
still yours to pump — going quiet with unlanded work is the stall, not a rest state. Workers drain the
ready set and clock out; **you** carry every finished order the rest of the way to `landed`.

## 7. Drain and stop

Two different gestures, each a first-class verb (the raw control keys they wrap are shown for reference):

```
# Drain: stop handing out NEW work; let running workers finish. The 95% case.
nightshift drain                                   # workers' next → DRAINING
nightshift drain --resume                           # resume dispatch
#   (raw: printf 1 | turnstile create /control/draining ; turnstile delete /control/draining)

# Stop: every worker halts at its next check. Nothing new commits.
nightshift stop                                     # workers' check → HALT
nightshift stop --resume                             # lift the halt
#   (raw: printf 1 | turnstile create /control/halt ; turnstile delete /control/halt)
```

## Invariants

1. **Nightshift never touches GitHub.** Registering, dispatch, and `land` are local. You own the
   GitHub surface — opening PRs and posting the one clearance note the **worker's attestation** earns;
   the **PR Lander** performs the merge, and `land` is how you report it back. Workers build and review
   and hand results inward; nothing but you writes to the public surface.
2. **`landed`, not `done`, advances the DAG.** Keep the merge loop moving; a stalled lander stalls
   every dependent.
3. **Orders are self-healing.** A worker that dies (lease expiry) returns its order to the pool
   automatically — no dead-agent detector, just the lease. An `escalated` order waits for your call
   (§5) and is never silently reassigned.
4. **`paths` overlaps serialize, they don't conflict.** If two orders touch the same files, give
   the second an `after` on the first so a merge conflict becomes a scheduling wait.
5. **A clean deck before every round.** Registration is inert; **you** activate. Retire the prior plan
   and clear leftover worktrees, session/presence files, and phantom roster rows before activating a new
   plan — a stale ready row dispatches *before* your new orders, and a leftover worktree hijacks a fresh
   worker's identity (§3).
