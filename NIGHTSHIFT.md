# NIGHTSHIFT.md — this repository's charter

**If you have been invoked as a Nightshift planner (or coordinator) for this repository, this is the
policy for turning issues into orders.** Read it alongside whatever the operator told you in this
session; your authority is this charter **plus** those session instructions. Where both are silent,
do **not** assume — post on the issue (see [Ask, don't assume](#ask-dont-assume)).

This is the operational instantiation of the charter concept described in
[`docs/design/charter.md`](docs/design/charter.md). For the engineering bar every order is built and
reviewed against, see [`AGENTS.md`](AGENTS.md).

## Scope — which issues become work

Candidates are **open issues in this repository** that describe a concrete change to Nightshift,
Turnstile, Octoshift, the skills, or the design docs. Not every open issue is plannable — see
[Readiness](#readiness).

Out of scope for planning: issues that are pure discussion, open design questions, or product-shape
decisions with unresolved tradeoffs. Those belong to the Product Manager; the planner does not turn
them into orders (it may post to move them forward).

## Issue conventions — how work is encoded

An issue in this repo is **planning-ready when it carries an order block** — a trailer naming the order
and its file scope, like:

> **Order:** `op-k-watch-hide-landed` (plan 2) · **After:** `op-j-status-vocab` · **Paths:**
> `src/nightshift/Cli.cs`, `src/nightshift/Commands/WatchCommand.cs`,
> `tests/Nightshift.Tests/WatchCommandTests.cs`

When an issue carries this block, the block **is** the plan: it gives you the order id, its `paths`
(the conflict-avoidance contract), and any `after` dependency. Map it directly.

An issue **without** an order block is not yet plannable — it is a description, not a slice. Shape it
first (or post asking for the missing block); do not invent the slice yourself.

## Issue → order mapping

- **One order = one landable PR**, bound to at most one issue. If an issue is genuinely one atomic
  change, it is one order. If it names several independently landable pieces, it is several orders
  (usually spelled out as multiple order blocks).
- **`paths` must be disjoint across orders that can run in parallel.** Two orders open at the same time
  must not share files; that is the whole conflict-avoidance contract. If two issues' order blocks
  overlap on `paths`, serialize them with `after` rather than letting them collide.
- **Prefer the issue's declared slice.** This repo authors its issues with deliberate order blocks;
  honor them. Re-slicing is a Product-Manager/Planner design act, not a planning-time improvisation.

## Dependency inference

- Take `after` **from the issue's order block** when present — it is authoritative.
- Where a block omits `after` but a path or logical dependency is obvious (an order edits a file another
  order must create first), add the `after` edge and note why in the plan.
- Remember `after` gates on **land (merge)**, not on a worker reporting done — dependents open only when
  their predecessor merges.

## The engineering standard

Every order is planned against this repo's bar, so a worker can check its own work and the review gate
has something concrete to enforce:

- **Build/test bar:** `net10.0`, NativeAOT-clean (`PublishAot`), **`TreatWarningsAsErrors`** — a warning
  is a build break. Full constraints and the build/test commands are in [`AGENTS.md`](AGENTS.md).
- **Review gate:** every order clears **two clean adversarial reviews from two different models** on its
  final head before it merges — governance and docs orders included. The roster is the single source of
  truth in [`AGENTS.md`](AGENTS.md#adversarial-review).
- Set each order's `standard` to the design note it must conform to (a `docs/design/*` doc), or inherit
  the plan's when they share one.

## Readiness

Before you create orders from an issue, apply this test:

> Does the issue have an **agreed shape** that slices into **`paths`-bounded** orders against a
> **concrete standard**?

- **Yes** → plan it (usually its order block already encodes the slice).
- **No** — scope unsettled, no agreed shape, tradeoffs open, or no order block → it is **not
  design-ready.** Do not plan it. Post on the issue naming the specific design it still needs (the
  decisions to make, the order block/`standard` to write), and leave it for the Product Manager to
  shape.

Forcing an under-designed issue into orders only burns worker time and tokens on churn. Readiness is a
gate, not a formality.

## Ask, don't assume

This charter carries the common case. When it is silent, or an issue is ambiguous in a way the charter
and your session instructions don't resolve, **do not guess** — post on the issue describing exactly
what is undecided and what you'd need to proceed, and wait. Charter-first, ask-on-gap.
