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

You run the shift. Workers claim and execute orders; **you** set up the board, register work, and
land merges. Nightshift is **not GitHub-aware** — it coordinates branches and state over a local
Unix socket. You (a human, or a future bridge) own the GitHub side: opening the PR, gating it through
the adversarial review, posting the one clearance note the reviewer's verdict earns, merging, and
telling Nightshift when a merge happened. The reviewer runs the gate and reports its verdict to you;
**you** are the only one who posts to GitHub.

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

Work out the design with the human first and commit it (the `standard` notes and the plan file
live in the repo — that commit is the authorization root). Then write the plan:

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

## 3. Register it

Two forms, same projection:

```
nightshift add orders.json          # one-shot: seed specs + ready set once, then exit. Safe to re-run.
nightshift plan --plan orders.json  # LIVE controller: seed, then watch and re-reconcile until Ctrl-C.
```

Run `plan` for a live shift — as orders land, it opens their dependents automatically. `add` is the
bootstrap/idempotent form. Either prints how many specs/ready rows it created.

Only ready orders are claimable; a worker's `next` hands out exactly one, exclusively.

## 4. Land merges — the only thing that advances the DAG

A worker's `release --status done` means **"submitted, awaiting merge"** — it does NOT open
dependents. **You** keep the merge loop: open the PR, run it through the adversarial-review gate
(the **reviewer** role clears it and reports a clean verdict to you — see the reviewer skill), post
the one clearance note that verdict earns, merge, then tell Nightshift the merge happened:

```
nightshift land /plan/9001/order/op1
```

`land` writes `state=landed`; the live `plan` controller then opens every order that was
`after: [op1]`. This is the human-in-the-merge-loop invariant: dispatch is autonomous, merging is
deliberate. (A future gh-aware bridge will call `land` for you off merged PRs; today it's manual.)

**Gate from isolated worktrees — never from your shared checkout.** To review or build a worker's
branch, add a detached worktree at its head; never `git checkout`/`switch` the branch in your
working repo (it corrupts a concurrent worker or the controller running there). This is mechanical
and it is yours to manage — one worktree per reviewer/build, removed when the gate closes:

```
git worktree add --detach ../gate-<order> origin/nightshift/<plan>/<order>
# review/build inside ../gate-<order>, then:
git worktree remove ../gate-<order>
```

## 5. Watch the board

`nightshift where` is the board — one row per order that has been claimed or reported:

```
nightshift where            # <order-base>  <status>  <branch>   (status=open while in hand)
nightshift roster           # who is on duty: <agent-id>  active|standby
```

Or inspect the raw keys directly:

```
turnstile get /plan/9001/order/op1/state          # {"status":"done"|"landed"|"escalated"|...}
turnstile get /plan/9001/order/op1/branch          # the worker's branch for this order
turnstile get /agent/<id>                          # a worker's roster entry: active|standby
```

The `/branch` key is written when the order is claimed: it maps the order to `nightshift/<plan>/<order>`.
Today it aids inspection and human review; a future merge→land bridge will use it (with the PR's
`Fixes:` / `Nightshift-Order:` trailers) to map a merged PR back to the order it lands.

Orders with `state.status == escalated` need your judgment. Answer a worker by creating a
directive on its order; the worker sees it as `QUERY` on its next `check`:

```
printf 'Fail closed; do not retry.' | turnstile create /plan/9001/order/op3/directive
```

## 6. Drain and stop

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
   GitHub surface — opening PRs, posting the clearance note the reviewer's verdict earns, merging;
   `land` is how you report a merge back. The reviewer reports its verdict to you and never posts.
2. **`landed`, not `done`, advances the DAG.** Keep yourself in the merge loop.
3. **Orders are self-healing.** A worker that dies (lease expiry) returns its order to the pool
   automatically — no dead-agent detector, just the lease. An `escalated` order waits for you and
   is never silently reassigned.
4. **`paths` overlaps serialize, they don't conflict.** If two orders touch the same files, give
   the second an `after` on the first so a merge conflict becomes a scheduling wait.
