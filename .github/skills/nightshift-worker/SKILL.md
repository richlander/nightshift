---
name: nightshift-worker
description: >-
  Operate as a Nightshift night-shift worker: claim one unit of work (an "order"
  = one landable PR) from the Turnstile coordination kernel, do it in an isolated
  git branch, and hand it back. Use this whenever you are launched to work a
  Nightshift backlog, or told to "run nightshift", "take the next order", or
  "join the shift".
---

# Nightshift worker

You are **one worker** on a shift. You take **one order at a time**, do it, and hand it back.
Coordination is not chat — it is state you read from the `nightshift` CLI. There is no one to
talk to. Every instruction you need arrives as the **return value of a gate call** (`next`,
`check`).

An **order** is one **landable PR**: the atomic unit of claim and of merge. It belongs to a
**plan** (a feature/campaign) and may point at a GitHub issue. You never coordinate with other
workers directly — the system serializes and schedules for you.

## The one rule that makes this work

> **Run `nightshift check` before every commit.** `check` renews your lease. Your claim on the
> order is a lease; if you stop renewing it, the system concludes you died and gives your order to
> someone else. There is no other heartbeat, no wrapper, no reminder. **If you don't `check`, you
> lose your work.**

You never see or manage the lease. The CLI owns it, keyed to this worktree. Do not try to track
or pass any token. If your context resets, your claim is intact — recover with `nightshift show`.

## Setup — once per shift

```
nightshift join
```

Registers you on the roster (`active`). That's it. Everything after is handled for you.

## The loop

```
nightshift next                 # ask for one order
```

Read the **first line** — that is your signal (the exit code mirrors it, but read the token):

| `next` prints | Meaning | Do |
|---|---|---|
| `WORK <base>` + fields | You claimed an order | Work it (below) |
| `NOWORK` | Nothing claimable right now | You cannot sleep — exit cleanly; you'll be relaunched |
| `DRAINING` | Shift is winding down | Stop asking; exit |

A `WORK` packet looks like:

```
WORK /plan/9001/order/op4
title: Retain build outcomes across reboot
issue: 1238
paths: src/Foo.cs, src/Bar.cs
standard: docs/design/foo.md
brief: ...one-line intent...
order_sha: a1b2c3d
fence: 7
```

- `base` = `/plan/<plan>/order/<order>` — your order's identity. Note it.
- `paths` = the files you are cleared to touch. Stay inside them.
- `issue` = the GitHub issue this order fixes (may be absent).
- `standard` = the design note you must conform to. Read it.
- `order_sha` = the commit that authorized this work (the plan lives there).

### Do the work

1. **Branch off `origin/main`**, named for the order:
   ```
   git fetch origin
   git switch -c nightshift/<plan>/<order> origin/main
   ```
   e.g. for `WORK /plan/9001/order/op4` → `git switch -c nightshift/9001/op4 origin/main`.
2. **Get context.** Read the `standard` note. If `issue` is set and `gh` is available,
   `gh issue view <issue>` for the full ask. Read any `related` PRs/issues; treat listed
   `antipatterns` as things NOT to do (a prior failed attempt).
3. **Make the change**, touching only files under `paths`.
4. **Before each commit, `nightshift check`** (see table below). Then commit. Repeat.
5. **Commit trailers** — always include, so the merge can be mapped back to this order:
   ```
   Fixes: #<issue>
   Nightshift-Order: /plan/<plan>/order/<order>
   ```
6. **Push the branch:** `git push -u origin nightshift/<plan>/<order>`.
   (Local-dev mode: you produce a branch. A human reviews, merges, and runs `nightshift land`.
   Do **not** open or merge a PR unless explicitly told to.)

### `check` — the heartbeat, read before every commit

```
nightshift check
```

| `check` prints | Meaning | Do |
|---|---|---|
| `OK` | Claim healthy, no directives | Continue; commit |
| `QUERY` + text | An operator answered/asked something | Read the text, comply, keep working |
| `HALT` | Global stop | Stop now. Do not commit. Exit |
| `FENCE_STALE` | You lost the claim (expired/reassigned) | Abandon this order. Do not push. Exit |

### Finish

When the branch is pushed and ready for human review:

```
nightshift release --status done
```

`done` means **"submitted, awaiting merge"** — it does **not** merge and does **not** open
dependent orders. Only a real merge (a human running `nightshift land`) advances the plan. After
`release`, loop back to `nightshift next` (or exit if you were told to do one order).

Other outcomes (use the honest one):

| `release --status` | When |
|---|---|
| `done` | Work complete, branch pushed, awaiting merge |
| `blocked --reason "..."` | Cannot proceed until something external happens |
| `declined --reason "..."` | Returning it to the pool untouched (someone/something else should do it) |
| `refused --reason "..."` | You will not do this (unsafe, out of scope) |
| `escalated` | You handed it back after escalating (see below) |

### When you need judgment — escalate

```
nightshift escalate --reason "API is ambiguous: retry or fail closed?"
```

This **pauses** on the order (you keep the claim) and marks it as needing a human. The answer comes
back through `check` as `QUERY`. If you can keep running, poll `check` for the answer; if you must
exit, the order will wait for a human — it is never silently reassigned.

## Leaving

```
nightshift leave
```

Clocks you out: returns any in-flight order to the pool and drops your roster entry. Idempotent.

## Golden rules

1. **`check` before every commit.** The lease is your claim; `check` renews it.
2. **You cannot sleep.** Headless, you have no next turn after you yield. Never "wait to be
   notified" — block in-line or exit. `NOWORK`/`DRAINING` mean *exit*, not *idle*.
3. **Never remember a lease or token.** The CLI owns it. `nightshift show` recovers your order if
   you reset.
4. **Stay inside `paths`.** They are the conflict-avoidance contract with other workers.
5. **One order, one branch, one worktree.** Don't claim a second order while holding one.
6. **Read the return value, not your assumptions.** `HALT`, `FENCE_STALE`, `DRAINING` override
   whatever you were doing.
