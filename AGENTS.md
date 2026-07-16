# Agent Instructions

## Start here

Nightshift is a coordination system that lets one human operator drive many AI
coding agents without spending their attention on mechanics. **You are one of
those agents.** You do structured work on the night shift — claiming and
executing orders through the mechanism described here; the operator works the
day shift, evaluating the shape of the product. The whole point is that you make
trustworthy progress without the operator having to babysit the mechanics.

It has three layers:

- **Turnstile** — a credential-free coordination kernel (kv, leases, watch) over
  a local Unix socket. No GitHub, no network, no auth.
- **Nightshift** — the shift mechanics on top of Turnstile: plans, orders, the
  ready set, claims, and landing.
- **Octoshift** — the optional bridge that watches GitHub merges and drives
  landing back into Nightshift.

The unit of work is an **order**: one **landable PR**, the atomic unit of both
claim and merge, bound to at most one GitHub issue. A **plan** is a DAG of
orders for a feature, with order→order dependencies.

Read, in order:

1. `docs/design/nightshift-vision.md` — why this exists and the shape of the
   solution.
2. `docs/design/nightshift.md` — the Nightshift mechanics (plans, orders, ready
   set, landing).
3. `docs/design/workflow.md` — the end-to-end workflow: how an order travels from
   idea to merge, and which role owns each step.
4. `docs/design/turnstile.md` — the coordination kernel underneath.
5. The skill for the role you are playing (below) before you act.

Keep this file to repository-wide engineering and workflow rules. Role
mechanics live in the skills; subsystem design lives in `docs/design/`. When the
two disagree, the skills and current code win.

## Roles and skills

You are always playing exactly one role. Read its skill first; it is the source
of truth for that role's flow.

| Role | You do | Read first |
| --- | --- | --- |
| **Coordinator** | Start the daemon, author/register a plan, keep the ready set live, own the GitHub surface (open PRs, post the clearance note), land merged orders, drain the shift | `.github/skills/nightshift-coordinator/SKILL.md` |
| **Worker** | Claim one order, build it on an isolated branch, push, hand it back; fix review findings | `.github/skills/nightshift-worker/SKILL.md` |
| **Reviewer** | Run the two-model adversarial gate on a PR read-only and report the verdict to the coordinator | `.github/skills/nightshift-reviewer/SKILL.md` |

## Task-specific guidance

| Area | Read first |
| --- | --- |
| End-to-end workflow and role ownership | `docs/design/workflow.md` |
| Nightshift plans, orders, landing | `docs/design/nightshift.md` |
| Turnstile kernel (kv/lease/watch, socket) | `docs/design/turnstile.md` |
| Octoshift GitHub→land bridge | `docs/design/octoshift.md` |
| Product intent and direction | `docs/design/nightshift-vision.md` |

## Repository-wide engineering constraints

- Target `net10.0`, `Nullable` enable, `ImplicitUsings` enable,
  `InvariantGlobalization`, and **`TreatWarningsAsErrors`** (see
  `Directory.Build.props`). A warning is a build break — fix it, don't suppress
  it blindly.
- Product paths must stay **NativeAOT-friendly**. `nightshift` sets
  `IsAotCompatible=true` (analyzer-enforced) and is published AOT with
  `-p:PublishAot=true`; `Octoshift` sets `PublishAot=true` in its csproj. Both
  `System.CommandLine` and `Markout` are AOT-safe; don't introduce
  reflection-heavy or trim-unsafe dependencies on product paths.
- **The CLI contract is load-bearing. Preserve it.** Agent shell loops depend on
  two signals from every command:
  - the **exit code as signal** (`ExitCode.*`), and
  - the **human-readable stdout token on the first line**
    (`WORK` / `NOWORK` / `OK` / `HALT` / `DRAINING` / `QUERY` / `FENCE_STALE`
    and their siblings).
  Never change a token's spelling, meaning, or exit code without updating every
  consumer (the skills and their shell loops). Adding a new token is fine;
  silently repurposing one is not.
- Nightshift is **not GitHub-aware**. It coordinates branches and state over a
  local socket; the GitHub side (review, merge, telling Nightshift a merge
  happened) is the coordinator's or Octoshift's job. Keep that boundary — no
  network or credentials on the coordination path.
- Reuse the existing command, state, and order-ref types (`Commands/*.cs`,
  `OrderState`, `OrderRef`, `Reconciler`) before adding parallel abstractions.
- Keep failure visible. A lost claim, an ineligible order, or a coordination
  error must surface as its token/exit code, never as success-shaped output.

## Building and testing

Build the whole graph:

```bash
dotnet build Nightshift.slnx
```

Tests are **xUnit v3 executable projects** (`OutputType Exe`). Run a suite with
either `dotnet test` (VSTest, via the referenced `Microsoft.NET.Test.Sdk` +
`xunit.runner.visualstudio`) or `dotnet run --project <proj>` (the xUnit v3
console runner) — both discover and execute the tests here.

| Area | Command |
| --- | --- |
| Nightshift | `dotnet run --project tests/Nightshift.Tests` |
| Octoshift | `dotnet run --project tests/Octoshift.Tests` |
| Turnstile | `dotnet run --project tests/Turnstile.Tests` |

Filter with xUnit v3 args, e.g. `-- -class "Namespace.ClassName"` or
`-- -method "*Pattern*"`. Run the smallest test project that covers your change;
expand only when the change crosses a boundary.

Documentation-only changes need Markdown review, not a product build or tests.

## Git and worktrees

- `main` is protected. Work on a descriptive feature or `chore/` branch.
- Start every change from the latest `origin/main`.
- Never amend or rewrite history; create follow-up commits.
- **Re-read your guidance after re-pulling `main`.** When you refresh from
  `origin/main` at the start of a new order or change, re-read `AGENTS.md`, the
  skill for your role, and task-relevant docs before continuing. Guidance
  evolves during a shift, and a long-lived agent that keeps working from a copy
  it read at launch will keep making the old mistake. Re-reading is what makes
  agents self-healing.
- **Workers: one worker, one worktree, for the whole shift.** A worker's
  identity is the hash of its worktree root, and its claim and lease are filed
  under that identity. Run every gate verb (`next` / `check` / `recover` /
  `release`) from that one directory, and `git switch` onto each order's branch
  there — never nest a per-order worktree. See the worker skill.
- Reviewers work in isolated **read-only** checkouts at an exact head; they never
  `git reset`, `git add`, or commit in a review tree.
- Don't mix unrelated changes into one commit or sweep another agent's
  working-tree changes into your work.

## Adversarial review

**Every change clears the gate before it merges** — an order is not mergeable
until it has **two clean reviews from two DIFFERENT models** on its final head.
This holds for governance and documentation PRs too, not just plan orders. Draw
the models from:

- Claude Opus 4.8
- GPT-5.x-Codex (e.g. `gpt-5.3-codex`)
- Gemini Pro (e.g. Gemini 3.1 Pro) — add as a third for high-blast-radius orders

**Who does what — the roles do not overlap:**

- **The builder never reviews or gates its own work.** As a worker you build the
  order and hand it back with `release`; you do not run the adversarial review,
  and you do not open PRs, post to, or merge on GitHub (reading an order's issue
  with `gh issue view` is fine — that is context, not a write). A builder grading
  its own homework is not a review.
- **The reviewer runs the gate.** Two independent models (never the same model
  twice; never the builder self-reviewing), each in an isolated **read-only**
  checkout at the exact head, given the same self-contained prompt (exact base
  and head, design intent, diff, concrete attack points). The reviewer drives
  findings to zero, reproduces any blocking finding on a clean exact-head
  checkout before acting, and — after fixes — **re-reviews the fixed head**. The
  gate passes only when both reviews are clean on the final head. The reviewer
  reports that verdict to the coordinator; it does **not** post to GitHub.
- **The coordinator owns the GitHub surface.** Only the coordinator opens PRs,
  posts the single clearance note, and lands merged orders. A verdict reaches
  GitHub through the coordinator and nowhere else. The **human** owns the merge
  itself — the one deliberate step kept in the loop. (A future gh-aware tool —
  octoshift — may take over the coordinator's mechanics, and a factory dial may
  automate the merge; until then the coordinator does its part by hand.)

The PR gets exactly **one** clearance note (a sidecar comment naming the models
and rounds), never a running commentary. GitHub carries decisions; the
deliberation (findings, fixes, re-reviews) stays in git and the order ledger.

Scale the review effort to the blast radius — a docs change is cleared by
confirming its claims are accurate, a coordination-logic change by attacking its
correctness — but the two-clean-reviews bar itself does not get waived.
