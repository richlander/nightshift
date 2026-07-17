# NIGHTSHIFT.md — this repository's charter

This document is the Nightshift engineering charter for this repository. It is written for a **planner**
(or coordinator) to import at the start of a shift and use to turn this repo's issues into **orders**
that workers execute. Read your role's skill for *how* to plan and run a shift; this charter carries only
what is specific to this repo. Your authority is this charter **plus** whatever the operator told you
this session — where both are silent, do not assume: post on the issue and wait.

Repository-wide engineering requirements are in [`AGENTS.md`](AGENTS.md).

## Scope — which issues become work

Candidates are **open issues in this repository** that describe a concrete change to Nightshift,
Turnstile, Octoshift, the skills, or the design docs. The **Product Manager tells you the theme at the
start of each session** — work the open issues that match that theme. Pure discussion, open design
questions, and product-shape decisions with unresolved tradeoffs are **not** planned; they belong to the
Product Manager (you may post to move them forward).

## Turning issues into orders

The order — its id, `paths`, `after` edges, and `standard` — is something **you produce** to drive the
shift; it is not authored into the issue. Issues stay ordinary issues. To turn the ones that match the
theme into orders:

- **Size each order to about an hour.** Break an issue into orders an agent can finish in **no more than
  an hour** and that are a reasonable size to **adversarially review at high quality**. In many cases a
  single issue already fits and needs no breakdown.
- **Design-first for the hard ones.** If an issue is ambiguous, carries tradeoff decisions, has
  significant interactions with other systems, or is a new foundational capability other features will be
  built on, start with a **docs-only design PR** before any implementation.
- **Design PRs still clear the gate.** A design PR gets the same adversarial review as code — often it
  matters more: a bad design produces bad code, and by then how the code is written barely matters.
- **Collapse strong overlap.** If two issues overlap heavily, consider merging them into a single order,
  or into a shared set of overlapping slices, rather than planning orders that will collide on the same
  files.

The mechanics of expressing an order and driving it to merge — the plan format, the two-clean review
gate, landing — are standard Nightshift and belong to your skill.
