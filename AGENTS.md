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
3. `docs/design/turnstile.md` — the coordination kernel underneath.
4. The skill for the role you are playing (below) before you act.

Keep this file to repository-wide engineering and workflow rules. Role
mechanics live in the skills; subsystem design lives in `docs/design/`. When the
two disagree, the skills and current code win.

## Roles and skills

You are always playing exactly one role. Read its skill first; it is the source
of truth for that role's flow.

| Role | You do | Read first |
| --- | --- | --- |
| **Coordinator** | Start the daemon, author/register a plan, keep the ready set live, land merged orders, drain the shift | `.github/skills/nightshift-coordinator/SKILL.md` |
| **Worker** | Claim one order, build it on an isolated branch, hand it back | `.github/skills/nightshift-worker/SKILL.md` |
| **Reviewer** | Run the two-model adversarial gate on a PR and post one clearance note | `.github/skills/nightshift-reviewer/SKILL.md` |

## Task-specific guidance

| Area | Read first |
| --- | --- |
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
until it has **two clean reviews from two DIFFERENT models** on its final head
(see the reviewer skill). This holds for governance and documentation PRs too,
not just plan orders. Draw the models from:

- Claude Opus 4.8
- GPT-5.x-Codex (e.g. `gpt-5.3-codex`)
- Gemini Pro (e.g. Gemini 3.1 Pro) — add as a third for high-blast-radius orders

Diversity is the point: a single model's blind spots are not a review. Give both
reviewers the same self-contained prompt (exact base and head, design intent,
diff, concrete attack points), isolate each in its own checkout, and reproduce
any blocking finding on a clean exact-head checkout before acting. After fixing
findings, **re-review the fixed head** — the gate passes only when both reviews
are clean on the final head.

Record the verdict as **one** clearance note on the PR (a sidecar comment naming
the models and rounds), not a running commentary. GitHub carries decisions; the
deliberation (findings, fixes, re-reviews) stays in your working notes and the
order ledger.

Scale the review effort to the blast radius — a docs change is cleared by
confirming its claims are accurate, a coordination-logic change by attacking its
correctness — but the two-clean-reviews bar itself does not get waived.
