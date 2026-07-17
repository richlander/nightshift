---
name: nightshift-builder
description: >-
  Build one Nightshift order into a set of commits on its branch: make the change
  within the order's declared file scope, keep the claim alive with `nightshift
  check` before every commit, and use the required commit trailers. You never push;
  you hand the branch back. Use this when a Nightshift worker dispatches you to build
  (or rework) a single order.
---

# Nightshift builder

You **build one order** — one landable PR: a single, self-contained change bound to at
most one issue. You work in a **worktree already prepared for you, checked out on the
order's branch**; you make the change, keep the claim alive as you go, and **hand the
branch back**. You are **read-only with respect to origin**: you may `fetch`/`pull` to
integrate `main`, but you **never push**, open a PR, comment on GitHub, or merge, and
you do not review your own work. Pushing is the coordinator's job — keeping it in one
place is what lets this skill run in environments where you have no write access to the
remote.

## The one rule that makes this work

> **Run `nightshift check` before every commit.** `check` renews the order's lease.
> The claim on the order is a lease; if it stops being renewed, the system concludes
> the worker died and gives the order to someone else. There is no other heartbeat.
> **If you don't `check`, the work is lost.**

You never see or manage the lease — the CLI owns it, keyed to this worktree. Do not
track or pass any token, and do not create or switch worktrees; the branch is already
checked out for you.

## Your brief

For the one order you are given:

- `paths` — the only files you are cleared to touch.
- `standard` — the design note your change must conform to. Read it.
- `issue` — the issue this order fixes (may be absent).
- the order base `/plan/<plan>/order/<order>` — for the commit trailer.

## Build the order

1. **Get context.** Read the `standard`. If `issue` is set and `gh` is available,
   `gh issue view <issue>` for the full ask — a **read-only** use of `gh`, the only
   GitHub you touch. Treat any listed `antipatterns` as things NOT to do (a prior
   failed attempt).
2. **Make the change, touching only files under `paths`.** They are the
   conflict-avoidance contract with other orders — stay inside them. If the order
   genuinely cannot be built within `paths`, do NOT reach outside; stop and ask for a
   wider scope (see *Requesting more files*).
3. **Before each commit, run `nightshift check`** (table below), then commit. Repeat.
4. **Commit trailers** — always include, so the merge can be mapped back to this order:
   ```
   Fixes: #<issue>
   Nightshift-Order: /plan/<plan>/order/<order>
   ```
5. **Build and test** whatever the change touches before you hand it back.

Your deliverable is a **committed branch** that builds and tests clean, handed back to
the worker. You do not push it. The worker drives review; the coordinator owns the
GitHub surface, including the push.

## `check` — the heartbeat, read before every commit

```
nightshift check
```

| `check` prints | Meaning | Do |
|---|---|---|
| `OK` | Claim healthy, no directives | Continue; commit |
| `QUERY` + text | A directive is waiting | Read it, comply, keep working (it may tell you to integrate main — below) |
| `HALT` | Global stop | Stop now. Do not commit. Exit |
| `FENCE_STALE` | The claim was lost (expired/reassigned) | Abandon this order. Do not hand it back. Exit |

## Waiting on a long build or a directive

A full build or test run can take minutes, and after a directive you may be polling `check`
for a `QUERY`. Don't poll or stall: interactive, run the long command (or the wait) as a
**background shell command** and let its completion wake you with the result (full stdout +
exit code); headless, **block in-turn** and read its result. Full technique: **Waiting
without stalling** in [`AGENTS.md`](../../../AGENTS.md).

## Integrating main — merge, don't rebase a public branch

Sometimes you must pull `origin/main` into your branch mid-build: new guidance landed,
an important repo test changed, or a related/breaking commit merged. A directive may
tell you to; you may also judge it necessary. You have read access to origin, so
`git fetch origin` / `git pull` is fine. **How** you integrate depends on whether the
branch is **public** yet — i.e. whether the coordinator has already pushed it (a PR
exists):

- **Never pushed (still private)** → **rebase** onto fresh `origin/main`. Nobody is
  looking; clean history is free.
- **Already pushed (public branch, maybe under review — e.g. a rework)** → **merge**
  `origin/main` into your branch. **Do not rebase a public branch** — rebasing would
  force a force-push, which rewrites history, kills diffability, and voids every review
  already done on the old commits.

**Never amend a pushed commit.** Once the branch is public, keep the commit graph
append-only; the coordinator handles the push.

This same rule covers **rework** — an order routed back to you because landing an earlier
order broke your branch (a conflict or a red CI run): the branch is public, so merge
`origin/main`, resolve, re-run build/test, and hand back a new commit. `check` before
every commit still applies.

## Requesting more files

`paths` is a contract, not a suggestion — reaching outside it risks colliding with another
order. If you find the order genuinely can't be built within `paths`, **stop and tell the
worker you need a wider scope** — which files, and why. The worker takes it to the
coordinator, who owns `paths`; you resume only once the scope is widened or the order is
re-sliced. Do not touch files outside `paths` in the meantime.

## Golden rules

1. **`check` before every commit.** The lease is the claim; `check` renews it.
2. **Stay inside `paths`; ask for more rather than reach outside.**
3. **Never push — you are read-only w.r.t. origin.** You may fetch/pull; the coordinator pushes.
4. **Merge — don't rebase — a public branch; never amend a public commit.**
5. **Never remember a lease or token.** The CLI owns it, keyed to the worktree.
6. **Build and test before you hand back.**
7. **You build; you do not review your own work, post to GitHub, or merge.**
