# The repository charter

**A repository's standing policy for how a Nightshift planner turns issues into work — and the
engineering bar that work is held to.**

This is a *design statement*: it describes what a charter **is** and why it exists. It is not itself a
charter. The charter for *this* repository — the live, dogfooded instantiation — is
[`NIGHTSHIFT.md`](../../NIGHTSHIFT.md) at the repository root.

---

## Why a charter exists

Nightshift's planner turns issues into **orders** (landable PRs) and registers them as a plan. But
*which* issues become work, and *how* they slice into orders, is not universal — it is a property of the
individual repository. Left unstated, the planner would have to **assume** that policy. Assumptions
are exactly what we do not want a planner making — a wrong guess scales churn across many workers before
anyone notices.

So the policy is made explicit and committed to the repo. The charter is where a repository writes down,
once, the answers a planner would otherwise guess.

## What invokes a charter

Opening an agent session does **nothing** on its own. A charter is not a trigger and does not make an
agent "jump to action." The operational model is human-initiated:

1. A person opens a terminal and says, in effect, *"you are a Nightshift planner for this repo."*
2. The invoked planner reads its **skill** (how to plan, in general) and this repository's **charter**
   (how *this* repo maps issues to orders).
3. The operator may add **session instructions** in that same conversation — a narrower focus, a
   priority, a one-off exception.

The planner's authority is therefore layered: **charter + session instructions**. Where both are
silent or an issue is ambiguous, the planner does **not** assume — it falls back to the release valve:
post on the issue naming what is undecided, and wait. This is *charter-first, ask-on-gap*: the charter
carries the common case so work scales without a human in the loop; the gap behavior preserves "no
assumptions."

## Charter vs `AGENTS.md`

Both are repository-level policy, but they serve different readers:

| | `AGENTS.md` | The charter (`NIGHTSHIFT.md`) |
| --- | --- | --- |
| Audience | **Every** role, every session | The **planner/coordinator** at invocation |
| Loaded | Ambient orientation any agent reads first | Consulted when someone is invoked to plan |
| Content | Repo-wide engineering constraints, the role map, build/test commands | The issue→order **mapping policy** and the engineering bar orders are planned against |

`AGENTS.md` orients any agent who lands in the repo. The charter answers a planner's *specific* question —
"given the open issues, what work do I create, and to what standard?" — and most sessions (workers)
never need it.

## What a charter contains

A charter is short and operational. It should answer, for its repository:

- **Scope.** Which issues are candidates for Nightshift work (and which are not).
- **Issue conventions.** How this repo's issues encode plannable work — labels, an embedded order
  block, a required shape — so the planner can tell a planning-ready issue from a sketch.
- **Issue → order mapping.** How an issue becomes one or more orders (one order = one landable PR),
  how finely to slice, and how `paths` are kept disjoint so orders don't collide.
- **Dependency inference.** How order→order `after` edges are determined (declared in the issue, or
  inferred from module/path dependencies).
- **The engineering standard.** The bar an order is planned against — the `standard` a worker checks
  its own work by, and the review gate it must clear. Often a pointer to `AGENTS.md` plus repo-specific
  values (rules and smells the coordinator gates and reviewers review against).
- **Readiness.** What makes an issue design-ready versus needing shaping — the concrete test the
  planner applies before creating orders, and what to post when it fails.
- **Ask-on-gap.** The explicit instruction to post on the issue rather than assume when the charter is
  silent.
