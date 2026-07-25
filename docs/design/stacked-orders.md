# Stacked orders

**Building and de-risking a dependent chain before anything lands.**
*Nightshift branches every order off one base. Stacking makes the base per-order state the coordinator owns.*

*Draft spec v0.1 — Rich Lander, July 2026*
*Built on [Nightshift](nightshift-spec.md) and [Turnstile](turnstile.md). Not yet built — this is the map.*
*Vision: [#118](https://github.com/richlander/nightshift/issues/118).*

---

## Summary

Today every order branches off `origin/main`, and a dependent order is serialized
by its `after` edge: it becomes ready only once its parent **merges**. So the
**end-to-end composition of a dependent chain is only exercised after its slices
have landed on `main`** — the *slice-8 reveals slice-2 was wrong* failure is
discovered in public, after slice 2 already shipped.

Stacking lets a dependent chain be built and adversarially reviewed slice by slice
on top of its predecessors, run its E2E scenario **at the tip before anything is
pushed or merged**, then land in topological order. Each PR stays bounded and
individually reviewable *and* the whole scenario is proven before any of it is
public.

The one architectural idea: **the base an order builds on becomes per-order
coordination state the coordinator owns**, replacing the global constant
`origin/main`. Everything else follows from that.

### What this is not

- **Not parallelism for dependent features.** A dependent feature cannot be built
  before the thing it depends on exists. Independent work already gets true
  parallelism from Nightshift's **path-partitioning**; stacking adds nothing
  there. The payoff is *integration confidence on a decomposed dependency chain*,
  not throughput.
- **Not a merge-queue reimplementation.** A stack lands through the existing queue.
- **Not a kernel change.** Turnstile's kv/lease/watch and the claim/lease/fence
  machinery are untouched. This lives in **coordinator + builder-skill +
  plan-schema**, extending seams that already exist.

## 1. The base ref — per-order coordination state

Every order gains a base ref: the commit-ish the worker branches from.

```
base-ref ∈ { main | <branch> | <commit-sha> }
```

- **`main`** — the default. An independent order, exactly as today.
- **`<branch>`** — "depends on all of the parent." The child tracks the parent's
  moving head.
- **`<commit-sha>`** — **preferred for a stacked child.** An immutable base: the
  child never restacks when the parent's *later* commits churn under review. This
  is the "one interface commit past `main`" case made mechanical — pin the child
  to the parent's stable contract commit, and the parent's other 10 commits are
  irrelevant to the child.

### Where it lives

The base ref is one more per-order key alongside the ones orders already carry
(`{base}/branch`), e.g. `{base}/base-ref`. The **coordinator writes it**; the
**worker reads it at claim**. `{base}` here is the order's Turnstile key path — the
existing "order base" — *not* a git ref; the git base ref is a distinct value this
key introduces.

### The WORK packet

`nightshift next` emits the load-bearing first line `WORK <orderBase>` followed by
body fields (`branch:`, `mode:`, `paths:`, …, then `fence:`). The base ref is a new
**body** line — `base-ref: <commit-ish>` — never the first line. The `WORK <base>`
header, its spelling, and the exit-code contract are unchanged; consumers that
ignore the new field keep working, and `show` / `recover` render it identically
(the recovery contract stays byte-for-byte reproducible).

Default when the key is absent: `main`. That keeps every existing plan valid with
no migration.

## 2. The coordinator owns `main`

Stacking requires a single, stable answer to "what is the base right now." The
coordinator provides it.

- **The coordinator holds `main` checked out in the primary worktree** and
  advances it `--ff-only` after each landing. Workers never check out `main`; they
  only `git switch -c <order-branch> <base-ref>` in their **linked** worktrees.
- **Git enforces most of this for free.** A branch can be checked out in at most
  one worktree, so while the coordinator holds `main`, a worker *cannot* check it
  out — it can only branch from it.
- **"Steal `main` back" is reconciliation, not a normal path.** The one hole git
  leaves is that any worktree can force-move a *shared* ref (`git branch -f main …`)
  without checking it out. The coordinator watches for `main` moving unexpectedly
  and resets it. Discipline (workers branch, never touch `main`) plus git's
  exclusivity means this rarely fires.
- **Local `main` is a coordinator-blessed snapshot** of `origin/main`. It never
  diverges from origin (a land merges on GitHub, origin advances, the coordinator
  fast-forwards local `main`). Its value is a *consistent* base across all workers
  in a tick — necessary for stacking (a child must build on a known base),
  beneficial generally (reproducible bases, a cleaner conflict graph).

Independent orders are unaffected: a worker's branch, once cut, is frozen at its
base; `main` advancing underneath it is fine (the builder skill already covers
integrating fresh `main` — rebase while private, merge once public).

## 3. The readiness predicate shifts

This is the real coordination change. Today an `after` edge means:

> ready when the parent **merges**.

Stacking relaxes it to:

> ready when the **depended-on commit exists locally**.

Because all worktrees of a repo **share one object database**, the moment the
parent's worker commits the contract, that ref is visible in the child's
worktree — no fetch, no push, no merge. The coordinator computes this predicate
(the base ref the child needs now exists and is reachable) and releases the child
into the ready set.

That is what lets **siblings on a shared base proceed concurrently**: once the
contract commit exists, two children that each depend only on it are independent
and can build at the same time. The concurrency is *between siblings on a shared
base*, never between a thing and its dependency.

## 4. Pre-req resolution via escalation

A base ref is a **prerequisite** the worker must be able to reach. On some machine
in a multi-box pool it may not be:

1. The worker checks whether its `base-ref` is reachable locally.
2. If not (the parent branch was built on another machine and never published), it
   **escalates to the primary coordinator** — the existing andon-cord `escalate`,
   a new *reason* to pull it, not a new mechanism.
3. The coordinator satisfies the need, typically by **pushing that branch to
   `origin`** so the worker's machine can fetch it, then clears the escalation.
4. The worker fetches the now-reachable base and proceeds.

This is deliberately **general**: "prereq not available → escalate → coordinator
publishes" covers cross-machine as one case, rather than a bespoke multi-machine
code path. It also preserves the trust seam — workers stay read-only w.r.t.
origin; only the coordinator pushes, including publishing a prereq branch.

A corollary worth stating: a local-only parent branch **machine-pins** any child
that depends on it until it is published. For the single-box shift the vision
targets that never arises; for two machines it becomes a dispatch choice — keep a
stack on one machine, or let the escalation publish the base on demand.

## 5. Landing a stack

- Land in **topological order** through the existing merge queue: contract first,
  then its dependents.
- A child pinned to `<commit-sha>` whose SHA is the parent's contract commit: once
  that commit is an **ancestor of `main`** (the contract PR merged), the child
  rebases cleanly onto `main` and is itself landable — independent of whether the
  rest of the parent has merged. That is the sibling model paying off: the contract
  and each dependent land on their own schedule, gated only by their true edges.
- Each order still clears its **two-clean adversarial gate** on its own head. The
  gate is unchanged; stacking only changes what that head is based on.

## 6. The shape to insist on

Slice the stack at its **dependency edges**, not at feature boundaries.

- The pattern-recognition/lifting **contract** is the stable base slice (the
  "interface commit").
- Independent variants that only need that contract are **siblings on it**, not one
  deep line.

```
main → contract → { variant-A, variant-B, nesting, edge-cases }
```

A shallow tree rooted at the contract restacks far less than
`contract → A → B → nesting`, where a fix in `A` churns everything above it. Spend
the most review rigor on the contract (ideally a design-first slice); the leaves
are cheap. A finding that reaches *back* into an earlier slice restacks and
re-opens everything above it — so front-load the review that is expensive to
relitigate, and keep depth low.

This maps directly onto Nightshift's existing `after` + path-partitioning: the
contract is an order, variants are orders with `after: [contract]`, and they are
path-independent of one another.

## 7. Worked example — a dotnet-inspect raise

A decompiler "raise" in `dotnet-inspect` typically splits into many slices that
are dependency- *and* same-file-coupled, with a runnable E2E signal (raise a real
assembly corpus, diff the C# output). Path-partitioning can't parallelize them
(same files); serialize-on-merge validates the composition too late (post-merge).

With stacking:

1. **Contract slice** — the recognizer/lifting interface. Reviewed hardest, pinned
   as the base SHA for everything below.
2. **Variant slices** — each a bounded PR based on the contract SHA, built and
   **adversarially reviewed as it is added** to de-risk early.
3. **E2E at the tip** — the full raise runs against the corpus before anything is
   pushed. A slice-2 defect surfaces here, while it is cheap, not after it is on
   `main`.
4. **Land in order** — contract, then variants, through the merge queue.

## 8. Open questions

- **Authoring.** Does the planner declare `after` + a dependency *kind* and the
  **coordinator computes** the base ref, or does the plan author base refs
  directly? (Leaning: planner declares intent, coordinator resolves to a concrete
  commit-ish — the coordinator is the only party that knows the live branch heads.)
- **SHA vs branch default** for a stacked child, and the exact rebase step once a
  pinned base SHA becomes an ancestor of `main`.
- **Key layout** for the base ref under `{base}/…` and the precise WORK-packet
  field name, verified against the first-line/exit-code contract and the `show` /
  `recover` reproduction tests.
- **Escalation ergonomics** — how a worker signals "prereq unreachable" (a
  `check`/`escalate` reason code) and how the coordinator's publish-to-origin
  closes the loop and re-arms the worker.
- **Multi-machine policy** — keep a stack on one machine by default vs. eagerly
  publish intermediate bases.
- **Readiness/liveness interaction** — a child released on "base commit exists"
  now has a soft dependency on the parent's *local* branch continuing to exist;
  what happens if the parent order is reworked or its branch is rebuilt under the
  child.

## Non-goals

- No value for genuinely independent work (path-partitioning wins) or for a
  scenario that fits a single reviewable PR.
- No new write authority for workers; no GitHub-awareness in the kernel; no second
  coordination store — the base ref is just another Turnstile key.
