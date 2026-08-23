# Agent Instructions

## Start here

Three things to hold at once — they are easy to conflate because all three are true here:

- **(A) What Nightshift is.** Nightshift is a coordination system that drives many units of work to
  completion in parallel by shifting the mechanical oversight onto the night-shift coordinator, so the
  people setting direction don't have to spend attention on it. Work happens on a **night
  shift** — orders built and reviewed to a clean bar — while direction-setting and the merge decision
  stay deliberate acts on the **day shift**.
- **(B) What you are.** You are an agent contributing features **to** Nightshift — this repository is
  the design *and* implementation of that system. You are not a generic assistant here; you are playing
  one **role** in a defined process (roles are below — read yours before you act).
- **(C) How this repo builds.** This repository builds Nightshift **using** Nightshift — it is
  self-hosted. Every feature ships as an **order** through the very process these docs describe: an
  issue becomes an order, a worker builds and reviews it to a clean bar, and the coordinator lands it.
  That is *why* you are playing a role at all — there is no side channel that bypasses the process.

**Roles are responsibilities, not people:** any role can be filled by a person or an agent, and you are
playing one of them (below).

Nightshift has three layers:

- **Turnstile** — a credential-free coordination kernel (kv, leases, watch) over
  a local Unix socket. No GitHub, no network, no auth.
- **Nightshift** — the shift mechanics on top of Turnstile: plans, orders, the
  ready set, claims, and landing.
- **Octoshift** — the optional bridge that watches GitHub merges and drives
  landing back into Nightshift.

The unit of work is an **order**: one **landable PR**, the atomic unit of both
claim and merge, bound to at most one GitHub issue. A **plan** is a DAG of
orders for a feature, with order→order dependencies.

Keep this file to repository-wide engineering and workflow rules. Role
mechanics live in the skills; subsystem design lives in `docs/design/`. When the
two disagree, the skills and current code win.

## Roles and skills

Nightshift has **five roles**, each a set of responsibilities fillable by a
person or an agent (the full model is in
[`docs/design/workflow.md`](docs/design/workflow.md)). You are playing one; read
its guidance first. Roles can collapse into one session — Planner and Coordinator
commonly do — but a **Worker is always a separate instance** (never the
Coordinator/Planner session); most sessions on a machine are workers. The
coordinator never claims, builds, or reviews, and does not spawn workers except
as a last-resort fallback when no worker is on the payroll (see the coordinator
skill) — normally workers `join` and `next` on their own.

| Role | Owns | Read first |
| --- | --- | --- |
| **Product Manager** | The expanding shape of the product: new issues, taste, where features must be re-shaped or composed | `docs/design/workflow.md` |
| **Planner** | Turns intent (often issues) into orders and registers them with nightshift | `.github/skills/nightshift-coordinator/SKILL.md` (§2–3) |
| **Coordinator** | Run the shift: register a plan, keep the ready set live, own the GitHub surface (push branches, open PRs, post the one clearance note), first-level escalation, issue curation, land merged orders, drain/stop | `.github/skills/nightshift-coordinator/SKILL.md` |
| **Worker** | Claim one order and take it to a reviewed branch (handed back for the coordinator to push) — orchestrating its build **and** review | `.github/skills/nightshift-worker/SKILL.md` |
| **PR Lander** | Merge authority; keep sequenced PRs flowing | `docs/design/workflow.md` |

The **Worker** builds and reviews by spawning subagents (an optimization that
preserves its context window and supplies model diversity). Those two
responsibilities each have their own skill the worker points a subagent at:

- **Builder** — build one order into commits on its branch: `.github/skills/nightshift-builder/SKILL.md`
- **Reviewer** — review one order's diff read-only and report inward: `.github/skills/nightshift-reviewer/SKILL.md`

The `nightshift` tool also packages these skills: `nightshift skill` prints the
general orientation (roles and how they fit together), and `nightshift skill
<role>` (`worker`, `coordinator`, `builder`, `reviewer`) prints that role's skill
— the same bytes as the files above, served from the binary.

## Where to read more

New here? Read `docs/design/nightshift-vision.md`, then `docs/design/nightshift-spec.md`,
then your role's skill (above). Reference the rest by topic:

| Topic | Doc |
| --- | --- |
| Why this exists, product intent and direction | `docs/design/nightshift-vision.md` |
| Nightshift mechanics — plans, orders, ready set, landing | `docs/design/nightshift-spec.md` |
| End-to-end workflow and role ownership | `docs/design/workflow.md` |
| Turnstile kernel (kv/lease/watch, socket) | `docs/design/turnstile.md` |
| Octoshift GitHub→land bridge | `docs/design/octoshift.md` |
| Stacked orders — per-order base refs, dependent chains | `docs/design/stacked-orders.md` |
| Issue → order mapping policy (planning) | `NIGHTSHIFT.md` (concept: `docs/design/charter.md`) |

## Repository-wide engineering constraints

- Target `net10.0`, `Nullable` enable, `ImplicitUsings` enable,
  `InvariantGlobalization`, and **`TreatWarningsAsErrors`** (see
  `Directory.Build.props`). A warning is a build break — fix it, don't suppress
  it blindly.
- Product paths must stay **NativeAOT-friendly**. Every product tool —
  `nightshift`, `turnstile`, `octoshift`, and `nightsky` — sets `PublishAot=true`
  in its csproj so it is published AOT (which implies `IsAotCompatible`, so the
  apps don't set that too). Both
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

## Waiting without stalling

Every role waits on a **blocking** call — the coordinator on `nightshift watch`,
a worker on `next`/`check`, a builder on a long `dotnet build`/`test` or a `check`
directive, a reviewer on a build or probe. Its *return* is the signal; how you
wait on it decides whether the loop keeps running:

- **Don't stall.** Ending a turn with a bare "I'll keep waiting" leaves nothing
  running to wake you — the wait never resumes and finished work piles up. That
  is the classic stall.
- **Don't poll.** A tight check-sleep-check loop burns turns for nothing.
- **Interactive** (a session that can go idle and be woken): issue the wait as a
  **background shell command**, end your turn, and let its completion
  notification wake you with the result — then act and relaunch. Bound the wait
  so it returns on the signal you care about — but mind *what the call returns*:
  a **token-returning** call (`next`/`check`) hands you the signal directly on
  return (`WORK`, `QUERY`, a build exit); a **raw change stream**
  (`nightshift watch`) only tells you *something* changed (each row carries the
  key + op, **not** the status value), so bound it on the first relevant *key*
  and then **reconcile** (`where` / `get …/state`) to see what actually changed.
  Grepping a change stream for a status word like `done` never fires.
- **Headless** (`-p`, no next turn once you yield): a backgrounded shell is
  reaped the moment you yield, so **block in-turn** on the call and read its
  return, or exit and be relaunched. `NOWORK`/`DRAINING` mean *exit*, not *idle*.

Each skill names the specific call its role waits on and points here for the
shared technique.

## Stopping: the status record

`Waiting without stalling` covers the waits a tool can hold for you. This section covers
the other kind — the wait whose answer lives on GitHub: a CI run, a required check, a
mergeability recompute. You cannot block on those without polling, and polling is what
exhausted the GraphQL budget (see [#157](https://github.com/richlander/nightshift/issues/157)).

So the rule is: **hand the wait off, then stop.**

- **Do not poll GitHub for a state change.** Not on a timer, not "one more check before I
  yield." If you have already learned the PR is on an unchanged head with a check still
  pending, re-asking cannot help you — nothing will change until something outside you
  changes it.
- **Never end on a bare narration.** "Waiting for CI to finish" as your last output is a
  stall with extra words: nothing is running to wake you, and the fact you just paid a
  GitHub call to learn dies in the terminal pane where no tool can read it.
- **End with a status record instead.** One line, machine-readable, as your final output.
  It says where you stopped, what would unblock you, and what you would do next. A tool
  scans for it, holds the wait on your behalf, and wakes you when the condition it names
  actually flips.

### The record

The **last non-blank line** of your final output, when you stop and cannot proceed:

```
NIGHTSHIFT-STATUS pr=4595 head=722512e25 round=2 verdict=gated waiting=check:ci-required
```

Keep whatever prose you already write — the record is for tools; the prose is for the
operator reading over your shoulder. Just put the record last.

| Field | Required | Meaning |
| --- | --- | --- |
| `pr` | yes | PR number, digits only. |
| `head` | yes | The head sha your record describes. |
| `round` | review only | Review round just completed. |
| `verdict` | yes | Your own conclusion: `converging`, `clean`, `gated`, `escalating`, `blocked`. |
| `waiting` | yes | The condition that would unblock you — see below. |
| `next` | yes | What you would do the moment it unblocks, as a short slug. |
| `at` | optional | UTC stamp, `2026-08-23T03:44Z`. Omit it in tmux — the pane's own activity time is observed rather than claimed, and cannot drift. |

Values carry no spaces, so the record survives being scraped out of a wrapped terminal pane.

**`head` is what makes the record falsifiable.** A record describes one sha. If GitHub's
head has moved past it, you pushed after writing it and the record is stale — a reader
discards it rather than acting on a claim about code that no longer exists. Never write a
record for a head you have not actually produced.

### `waiting` names a predicate, not a mood

The whole value of the field is that a tool can *evaluate* it. `waiting=ci` is nearly
useless — it means "something, somewhere, eventually." Name the specific thing:

| Value | Evaluated as |
| --- | --- |
| `check:<name>` | Does check run `<name>` exist on `head`, and what did it conclude? |
| `merge` | Is the PR mergeable, or is it `CONFLICTING`? |
| `review` | A review round is outstanding; nothing on GitHub will change. |
| `operator` | A human decision is required. **Tools must not wake you for this.** |
| `none` | Nothing external blocks you; you stopped at a round boundary. |

Prefer `check:<name>` over `ci` whenever you know the name — and by the time you are
blocked on a check you almost always do, because you just looked. A named check is one
cheap conditional REST call with an exact answer; `ci` forces a reader to guess which of
several pending checks you meant.

### `next` keeps the tool out of the judgment business

A tool that reads your record decides **when** you resume. It never decides **what** you
do — it releases the instruction *you* already declared in `next`. That boundary is the
same one Octoshift holds: it detects, routes, and records, and it authors no judgment.

Write `next` as the instruction you would want handed back to you, short and imperative:
`round-3`, `rebase`, `fix-ci`, `merge`, `escalate`.

### Worked examples

A review round finished, nothing external pending — the tool confirms the PR is green and
mergeable, then hands back `round-3`:

```
NIGHTSHIFT-STATUS pr=4563 head=f5c2b3fac round=2 verdict=converging waiting=none next=round-3
```

A rerun is outstanding and the required check has not reported yet. There is nothing to do
and nothing to re-poll; the tool owns the wait until `ci-required` lands on that exact sha:

```
NIGHTSHIFT-STATUS pr=4595 head=722512e25 round=2 verdict=gated waiting=check:ci-required next=round-2-review
```

Four rounds without convergence — escalation is a human decision, so no tool wakes this one:

```
NIGHTSHIFT-STATUS pr=4470 head=91ab3c7de round=4 verdict=escalating waiting=operator next=escalate
```

### Why this earns its keep

An idle pane carrying a record is **unambiguous**: the agent is stopped, the wait is
declared, and ownership of it has transferred. An idle pane *without* one is equally
unambiguous — it is stuck, and it should be looked at. Today both look identical, which is
why a finished round can sit unnoticed for six hours.

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

This list is the single source of truth for the reviewer roster; the skills and
`docs/design/workflow.md` reference this section rather than restating the models.
**Do not review with your own model** — a reviewer subagent runs on a model
different from the builder's, and a single-model worker cannot review its own
build (see below).

**Who does what — the responsibilities do not overlap:**

- **The builder builds; it never reviews its own work.** Building an order — as
  the worker itself or a builder subagent it spawns — produces a committed branch,
  handed back for the coordinator to push. A builder never pushes (it is read-only
  w.r.t. origin), does not review that branch, and does not open PRs, post to, or
  merge on GitHub (reading an order's issue with `gh issue view` is fine — that is
  context, not a write). A builder grading its own homework is not a review.
- **The worker runs the gate.** It drives two clean reviews from two **different**
  models on the final head — preferably as reviewer subagents on models different
  from the builder's, each read-only in its own checkout, given a self-contained
  prompt (exact base and head, design intent, diff, concrete attack points).
  Findings go to the **builder** to fix; after fixes the worker **re-reviews the
  fixed head**; the gate passes only when both reviews are clean on the same final
  head. Four rounds without convergence → the worker **escalates to the
  coordinator**. Without subagents a worker is one model and cannot review its own
  build — the review goes to a different worker, and a worker offered review of an
  order it built declines as an invalid choice. The worker hands the coordinator
  the **attestation** (models and rounds); it never posts to GitHub. A round boundary is a
  stop, so the worker's final output at each one ends with a **status record**
  ([above](#stopping-the-status-record)) — that is what lets a round that finished at 03:44
  be picked up at 03:45 instead of whenever someone next looks.
- **The coordinator owns the GitHub surface — and the push.** Only the coordinator
  pushes worker branches to origin, opens PRs, posts the single clearance note (from
  the worker's attestation), and lands merged orders. A verdict reaches GitHub through
  the coordinator and nowhere else. Consolidating the push here lets the build/review
  roles run with no write access to origin. The **PR Lander** owns the merge itself —
  the one deliberate step kept in the loop. (A future gh-aware tool — octoshift — may
  take over the coordinator's mechanics, and a factory dial may automate the merge;
  until then the coordinator does its part by hand.)

The PR gets exactly **one** clearance note (a sidecar comment naming the models
and rounds), never a running commentary. GitHub carries decisions; the
deliberation (findings, fixes, re-reviews) stays in git and the order ledger.

Scale the review effort to the blast radius — a docs change is cleared by
confirming its claims are accurate, a coordination-logic change by attacking its
correctness — but the two-clean-reviews bar itself does not get waived.
