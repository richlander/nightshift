# NIGHTSHIFT.md — this repository's charter

**If you have been invoked as a Nightshift planner (or coordinator) for this repository, this is the
policy for turning *this repo's* issues into orders.** Read your role's skill
([`.github/skills/nightshift-coordinator/SKILL.md`](.github/skills/nightshift-coordinator/SKILL.md)) for
*how* to plan and run a shift — this file adds only what is specific to this repository. Your authority
is this charter **plus** whatever the operator told you this session; where both are silent, do **not**
assume — post on the issue and wait.

The charter concept is in [`docs/design/charter.md`](docs/design/charter.md); the engineering bar every
order is built and reviewed against is in [`AGENTS.md`](AGENTS.md).

## Scope — which issues become work

Candidates are **open issues in this repository** that describe a concrete change to Nightshift,
Turnstile, Octoshift, the skills, or the design docs. Pure discussion, open design questions, and
product-shape decisions with unresolved tradeoffs are **not** planned — they belong to the Product
Manager (the planner may post to move them forward).

## Issue conventions — how work is encoded

An issue here is **planning-ready when it carries an order block** — a trailer naming the order, its
file scope, and any dependency:

> **Order:** `op-k-watch-hide-landed` (plan 2) · **After:** `op-j-status-vocab` · **Paths:**
> `src/nightshift/Cli.cs`, `src/nightshift/Commands/WatchCommand.cs`,
> `tests/Nightshift.Tests/WatchCommandTests.cs`

The block **is** the slice: map it directly — order id, `paths`, and `after` as written. An issue
**without** an order block is a description, not a slice; do not invent one — post asking for the block,
or leave it for shaping.

## Mapping — this repo's rules

- **Honor the declared order block.** This repo authors its issues with deliberate slices; take the
  order id, `paths`, and `after` as given. Re-slicing is a Product-Manager/Planner design act, not a
  planning-time improvisation.
- **Take `after` from the block** when present. Add an edge only where a path dependency is obvious (an
  order edits a file another must create first), and note why in the plan.
- **Set each order's `standard`** to the `docs/design/*` note it must conform to, or inherit the plan's
  when they share one.

Everything else — one order = one landable PR, `paths` kept disjoint across parallel orders, `after`
gating on merge, the two-clean-review gate — is standard Nightshift mechanics; your skill and
[`AGENTS.md`](AGENTS.md) own it.
