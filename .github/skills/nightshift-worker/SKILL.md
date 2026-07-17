---
name: nightshift-worker
description: >-
  Operate as a Nightshift night-shift worker: claim one unit of work (an "order"
  = one landable PR) from the Turnstile coordination kernel and take it to a
  reviewed branch — building it and reviewing it (usually by spawning
  subagents), driving the adversarial gate to two clean, then handing it back.
  Use this whenever you are launched to work a Nightshift backlog, or told to "run
  nightshift", "take the next order", or "join the shift".
---

# Nightshift worker

You are **one worker** on a shift. You take **one order at a time**, take it all the way
to a **reviewed branch**, and hand it back. You **build and review** each order.

Work comes from head office — the planner and coordinator — and you satisfy it with the
**automated tools on hand**: the `nightshift` CLI hands you orders and carries directives
both ways, and the **subagents** you spawn do the building and reviewing. Coordination is
not chat; there is no one to talk to. Every instruction you need arrives as the **return
value of a gate call** (`next`, `check`).

An **order** is one **landable PR**: the atomic unit of claim and of merge. It belongs
to a **plan** (a feature/campaign) and may point at a GitHub issue. You never
coordinate with other workers directly — the system serializes and schedules for you.

## The one rule that makes this work

> **`nightshift check` must run before every commit.** `check` renews your lease. Your
> claim on the order is a lease; if it stops being renewed, the system concludes you
> died and gives your order to someone else. There is no other heartbeat. **If nobody
> `check`s, the work is lost.**

You never see or manage the lease. The CLI owns it, keyed to this worktree. Do not
track or pass any token. **If you spawn a builder subagent, it carries this same rule**
— the `nightshift-builder` skill makes the builder responsible for its own commits and
for running `check` before each one. If your context resets, recover with **`nightshift show`**
(session intact) or **`nightshift recover`** (session gone — see *Recovery*).

## Setup — once per shift

Everything you do this shift runs from ONE dedicated worktree — that stable directory
is what keeps your identity (and therefore your claim and lease) attached to you.
**Mint a fresh one of your own** off `origin/main` and work inside it for the whole shift:

```
git fetch origin
# <NN> = a random number you pick yourself (say, 0–99). No tool or command runs it — just choose one.
git worktree add ../nightshift-worker-<NN> --detach origin/main
cd ../nightshift-worker-<NN>
nightshift join
```

The suffix `<NN>` is a **random number you choose yourself** (say, 0–99) — you pick it; no
tool or command generates it for you. Identity is the hash of the worktree path, so the name
only has to be unique and stable for the session; picking a random number makes a collision
unlikely, so a fresh worker almost never lands on an occupied name and rarely has to decide
whether to adopt someone else's. `join` registers you on the roster (`active`). Stay in this
directory: every gate call (`next`, `check`, `recover`, `release`) is keyed to it. Do NOT
create a new worktree per order — switching directories changes your identity and orphans
your claim.

> **Never adopt a worktree you didn't create.** Your identity is the hash of your worktree
> path, so a pre-existing `nightshift-worker-*` (or `review-*`) directory is **someone
> else's** identity — a live peer's, or a dead worker's the coordinator hasn't cleared yet.
> Stepping into it makes you inherit their session, lease, and claim: `nightshift show`/
> `check` will report an order you never claimed, and `check` may even return `OK` on it.
> **That order is not yours.** Because you pick your own random suffix, your worktree name is
> almost always free — and if the number you picked already has a worktree, just choose another
> one. You never have to weigh adopting an existing one. The only time you resume an existing
> worktree/branch is genuine **recovery** (below): standing on **your own** order branch after a
> context reset.
>
> **Don't do archaeology.** Other workers' worktrees, sessions, roster entries, and fence
> numbers are the **coordinator's** view, not yours. Orient with exactly three steps —
> `join`, `next`, read the packet. A fresh worker starts clean; it does not go hunting for
> state to adopt.

## The loop

```
nightshift next                 # ask for one order
```

Read the **first line** — that is your signal (the exit code mirrors it, but read the
token):

| `next` prints | Meaning | Do |
|---|---|---|
| `WORK <base>` + fields | You claimed an order | Take it to a reviewed branch (below) |
| `NOWORK` | Nothing claimable right now | Returned only by `next --once` (script/CI probe); in worker mode you normally do not use `--once` |
| `DRAINING` | Shift is winding down | Exit — no new work will be dispatched |
| `HALT` | Global stop is in force | Stop immediately and exit |

Worker doctrine: the normal worker call is plain `nightshift next` (no `--once`). It **parks inside
that running process** until one of three terminal outcomes happens: `WORK`, `DRAINING`, or `HALT`.
The process is alive and blocked on I/O, not yielded/asleep. Start workers once; as `land`/`rework`
open dependents, parked workers wake and drain the DAG.

A `WORK` packet looks like:

```
WORK /plan/9001/order/op4
branch: nightshift/9001/op4
title: Retain build outcomes across reboot
issue: 1238
paths: src/Foo.cs, src/Bar.cs
standard: docs/design/foo.md
brief: ...one-line intent...
order_sha: a1b2c3d
fence: 7
```

- `base` = `/plan/<plan>/order/<order>` — your order's identity. Note it.
- `branch` = the exact branch this order lives on. It also encodes the order, so it is
  your **recovery anchor**: if you wake on this branch with no session, `nightshift
  recover` re-attaches.
- `paths` = the files this order is cleared to touch. Stay inside them.
- `issue` = the GitHub issue this order fixes (may be absent).
- `standard` = the design note the change must conform to.
- `order_sha` = the commit that authorized this work.

Once you re-pull `origin/main` at the start of an order, **re-read your guidance** (this
skill + `AGENTS.md` + the order's `standard`). Re-reading each order is what makes a
long-lived worker self-healing.

**A rework packet** — `mode: rework` with a `findings:` brief — means this order was
**sent back after being submitted** (the coordinator rejected the `done` order, or `main`
broke its branch). The branch already exists on origin with prior work: **fetch and
continue it — do not cut a fresh one** (that would orphan the reviews already done).
Address the `findings`, keep history append-only (merge `main` if you must integrate it;
never rebase or force-push a public branch), and drive it back through review.

## Build and review the order

You take the order from claim to a reviewed branch. You do this by orchestrating
two pieces of work: **building** it and **reviewing** it.

**Subagents are encouraged, not required.** Their value is **context-window
preservation** — a builder subagent and a reviewer subagent each work in their own
context, so your window stays clean across a long shift. They also buy parallelism and
**model diversity**. But you may also do the work in your own session. Two legal shapes:

- **With subagents (preferred for long shifts).** Spawn a **builder subagent** (one
  model) to make the change, then **reviewer subagents** on **different models** to
  review it. Because the reviewers run models different from the builder's, you can
  legitimately **build and review the same order** yourself, through them.
- **Without subagents.** You are a single model. You can build the order or review the
  order — but **not both for the same order**: a model reviewing its own build is
  grading its own homework. The order's build and its review must be different models,
  which without subagents means the **review goes to a different worker**.

> **The self-review rule.** The builder never reviews its own work. With subagents you
> enforce it by choosing a different model for the reviewer; without subagents the
> order's review goes to a different worker (model).

**You own all branch and worktree management** — uniformly, so builders and reviewers
just work. The build happens in your order worktree (already on the branch). For each
reviewer, add a **detached, read-only worktree at the head** and hand it over; tear it
down when the round closes:

```
git worktree add --detach ../review-<order>-<model> <head-sha>
# ... the reviewer reads (never writes) it ...
git worktree remove ../review-<order>-<model>
```

**Spawning subagents (they load skills by file path).** A subagent does **not** inherit
your skill menu — it reads the skill file directly. So tell each one its role, its brief,
and its worktree explicitly:

- Builder subagent: "Read `.github/skills/nightshift-builder/SKILL.md`. You are the
  builder; build this order in this worktree. Brief: `paths`, `standard`, `issue`, and
  the order base." Choose its model.
- Reviewer subagent: "Read `.github/skills/nightshift-reviewer/SKILL.md`. You are a
  reviewer; review the diff in this prepared worktree at head `<sha>`. Report findings or
  clean to me." Choose a model **different** from the builder's.

**Drive the gate to two clean.** An order is not done until it has **two clean reviews
from two different models** on its *final* head:

1. Build → local commits (the builder does this, or you do). Nobody pushes — the
   branch stays local; the coordinator pushes it when it opens the PR.
2. Review the head with two different models (reviewer subagents, or a
   second/third worker).
3. Reviewers classify findings **blocking / non-blocking / pre-existing**. **Blocking**
   findings go back to the **builder** to fix on the branch; a new commit is a new head → a
   fresh round; the gate passes only when both models are clean on the *same, final*
   commit. **Non-blocking** and **pre-existing** findings don't hold the order — carry
   them in your report to the coordinator, which files them as follow-up issues.
4. **Bound the loop:** four rounds without two clean → **escalate to the coordinator**
   (below), do not keep looping.

**Reviewers don't renew the lease — you do.** The lease is keyed to *your* worktree, so
a reviewer subagent never runs `check` and doesn't need to. But the lease has a **45-minute
TTL** and `check` only fires before *commits* — a long review with no fix commits can go
quiet long enough to let it lapse. While you wait on reviews, run `nightshift check`
periodically (e.g. between rounds) to keep your claim alive.

**Declining review as an invalid choice.** If review work for an order **you built** is
ever dispatched to you, you are a **structurally invalid** reviewer — decline it with an
explicit reason ("I built this order; I am not a valid reviewer for it"), not silently.
This is distinct from an ordinary decline, which could loop the same work back to you.

### Finish — hand back a reviewed branch

Ensure the branch is committed with the required trailers (the builder skill owns those),
then:

```
nightshift release --status done
```

`done` means **"submitted, awaiting merge"** — it does **not** merge and does **not**
open dependent orders. Include the **review attestation** (the models that signed off
and how many rounds each needed) in your release/report — the coordinator posts it as
the single clearance note. Your deliverable is a **reviewed branch**; you never push,
open, comment on, or merge a PR — the coordinator owns the GitHub surface, including the
push. After `release`, loop back to `nightshift next` (or exit if you were told to do one
order).

Other outcomes (use the honest one):

| `release --status` | When |
|---|---|
| `done` | Built, reviewed to two clean, branch committed, awaiting merge |
| `blocked --reason "..."` | Cannot proceed until something external happens |
| `declined --reason "..."` | Returning it to the pool untouched (someone/something else should do it) — including "invalid reviewer: I built this order" |
| `refused --reason "..."` | You will not do this (unsafe, out of scope) |
| `escalated` | You handed it back after escalating (below) |

## `check` — the heartbeat, read before every commit

```
nightshift check
```

| `check` prints | Meaning | Do |
|---|---|---|
| `OK` | Claim healthy, no directives | Continue; commit |
| `QUERY` + text | An operator/coordinator answered/asked something | Read it, comply, keep working |
| `HALT` | Global stop | Stop now. Do not commit. Exit |
| `FENCE_STALE` | You lost the claim (expired/reassigned) | Abandon this order. Do not hand it back. Exit |

## When you need judgment — escalate to the coordinator

```
nightshift escalate --reason "review did not converge after 4 rounds: <outstanding findings>"
```

This **pauses** on the order (you keep the claim) and marks it as needing the
coordinator, who is **first-level escalation**. The escalation reaches the coordinator
**as state**, not as chat. The coordinator decides: continue (keep going), abandon
(a new issue + slices replaces this order), or requeue (return to the pool with updated
guidance). Its answer comes back through `check` as `QUERY`. If you can keep running,
poll `check`; if you must exit, the order waits — it is never silently reassigned.

Escalate for anything that needs judgment, not just review non-convergence: an ambiguous
spec, a design that looks wrong as you build it, a path collision you can't resolve, or a
builder that needs files outside its `paths` (the coordinator owns `paths` — don't widen
scope yourself).

## Waiting without stalling

You wait on `next` (for work) and on `check` (for the coordinator's `QUERY` answer after an
escalation). Don't poll in a tight loop, and don't end a turn "waiting to be notified" with
nothing running to wake you. The default worker wait is to stay inside a running `nightshift next`
process; that is the parked state. Exit only on `DRAINING` or `HALT` (or after deliberate one-shot
`next --once`, which is for probes/scripts). Interactive sessions may still background other long
waits and wake on completion. Full technique:
**Waiting without stalling** in [`AGENTS.md`](../../../AGENTS.md).

## Recovery — after a context reset or a reboot

Your task lives in Turnstile and in your git branch, never in your memory. Two ways back:

- **`nightshift show`** — reprints your current WORK packet. Use it when your context was
  compacted but the process/worktree is intact (the session is still on disk). Read-only.
- **`nightshift recover`** — use it when the session is gone (a reboot, a wiped runtime
  dir, or you were relaunched into the worktree fresh). It reads the **branch you are
  standing on** (`nightshift/<plan>/<order>`), finds the order, and — if it is still
  yours or free — re-attaches you under a fresh lease and reprints the WORK packet.
  - WORK packet → you're back; continue the loop (`check` before commits).
  - `done`/`landed`/held by another agent → stand down; go to `nightshift next`.
  - **So always land on your order's branch before recovering** — the branch is the key.

## Leaving

```
nightshift leave
```

Clocks you out: returns any in-flight order to the pool and drops your roster entry.
Idempotent. Then tear down your worker worktree at the end of the shift (after `leave`).
git won't remove the worktree you're standing in, so step back into the main clone first:

```
cd "$(git rev-parse --path-format=absolute --git-common-dir)/.."   # into the main clone
git worktree remove ../nightshift-worker-<NN>   # the random suffix you minted at setup
```

Do NOT remove it between orders — it is your home for the whole shift. While you are
standing on an order branch, `nightshift recover` re-attaches you from that branch.

## Golden rules

1. **`check` before every commit.** The lease is your claim; `check` renews it — whoever
   commits from your worktree runs it.
2. **You orchestrate build *and* review.** Prefer subagents (context preservation, model
   diversity); a builder subagent + a different-model reviewer subagent let you build and
   review one order. Without subagents you cannot review your own build.
3. **Don't stall while waiting.** The worker parks by staying inside a running `next`; do not
   exit on an empty ready set unless you intentionally used `next --once`. In normal worker mode,
   exit on `DRAINING` or `HALT`. Interactive sessions may background blocking waits, but never end
   a turn waiting with nothing backgrounded to wake you (see **Waiting without stalling**).
4. **Never remember a lease or token.** The CLI owns it. Recover with `nightshift show`
   (session intact) or `nightshift recover` (session gone — from your branch).
5. **Stay inside `paths`.** They are the conflict-avoidance contract with other workers.
6. **One worker, one worktree; one order at a time.** Work the whole shift from your
   single dedicated worktree, `git switch`-ing onto each order's branch. Never nest a
   per-order worktree, and never claim a second order while holding one.
7. **Hand results inward, never to GitHub.** You hand back a committed branch and report
   a verdict to the coordinator; the coordinator pushes it, opens the PR, and posts the
   one clearance note; the PR Lander merges.
8. **Read the return value, not your assumptions.** `HALT`, `FENCE_STALE`, `DRAINING`
   override whatever you were doing.
9. **Start clean; never adopt another worker's worktree, identity, or claim.** Mint your
   own worktree; a pre-existing one is someone else's (or a dead worker's uncleared) state.
   The only exception is genuine `recover` while standing on **your own** order branch.
