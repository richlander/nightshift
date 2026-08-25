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

## Stopping: publish your state

`Waiting without stalling` covers waits a tool can hold for you. This section covers the
other kind — the wait whose answer lives on GitHub: a CI run, a required check, a
mergeability recompute. You cannot block on those without polling, and polling is what
exhausted the GraphQL budget (see
[#157](https://github.com/richlander/nightshift/issues/157)).

So: **hand the wait off, then stop.**

- **Do not poll GitHub for a state change.** Re-asking about a head that has not moved
  cannot tell you anything new.
- **Never end on a bare narration.** "Waiting for CI" as your last output is a stall with
  extra words: nothing is running to wake you, and the fact you paid a GitHub call to
  learn dies where no tool can read it.
- **Publish your state as a tmux window option instead**, and set it whenever the state
  changes:

```sh
# Refuse to publish without a target of your own. This guard is the load-bearing
# part: with $TMUX_PANE empty, `set` lands on whichever window is current — and
# so does the matching `show`, so reading back through the same target confirms
# a write that went to somebody else's window.
[ -n "$TMUX_PANE" ] || { echo "no TMUX_PANE; do not publish"; exit 1; }

tmux set -w -t "$TMUX_PANE" @agent_state \
  "pr=4626 head=f4a8d1c84 round=1 reviews=2/2 blocked=4629 rec=wait"

# Verify by looking at every window, not by re-reading your own target: exactly
# one should carry your value. More than one means a write escaped.
tmux list-windows -a -F '#{window_name} #{@agent_state}' | grep -c 'pr=4626'
```

**Why an option and not your output.** This UI runs on the alternate screen, so tmux
keeps no scrollback for it — `capture-pane -S -400` returns the same single screen as a
plain capture. Once your report scrolls past the top it is unrecoverable by any tool and
the window is anonymous. An option persists until you change it and cannot be garbled by
line wrapping.

**Clear it when the window stops owning the work**, and re-publish rather than inherit
after a resume or reassignment. A window option outlives the session that set it, so a
stale one keeps advertising a merge or wait decision that nobody is standing behind:

```sh
tmux set -w -t "$TMUX_PANE" -u @agent_state
```

### The fields

| Field | Required | Meaning |
| --- | --- | --- |
| `pr` / `issue` | one of | PR number, or the issue number before a PR exists. |
| `head` | yes | The head sha this state describes. |
| `round` | review only | Review round just completed. |
| `reviews` | yes | `<clean>/<required>` — the dual-clean count. |
| `blocked` | when blocked | Issue or PR numbers. Omit when empty. |
| `waiting` | when waiting | A predicate: `check:<name>`, `checks`, `merge`, `review`. |
| `rec` | yes | `continue`, `wait`, `merge`, `approve`, or `stop`. |

Values carry no spaces.

**Identity before a PR exists.** A worker branch is local until the coordinator pushes it,
so at early round boundaries there is no PR and no GitHub-visible head. Publish `issue=`
then, and switch to `pr=` once the PR is open. A required field with no legal value is how
you get invented ones.

**`head` makes the state falsifiable.** It describes one sha; if GitHub has moved past it,
a reader discards the claims rather than acting on assertions about code that no longer
exists.

**`reviews` is the one fact no tool can observe.** GitHub knows CI and mergeability. It has
no idea one reviewer came back clean and the other has not reported — and that, with
mergeability, is what **ready** means here. A green CI run is evidence, not the definition.

**`blocked` and `waiting` split by who can act.** `blocked` takes issue or PR numbers only:
things a person can open and prioritise, and that the next agent hitting the same wall can
find instead of re-investigating. If a flake blocks you and no issue exists, file one and
cite it. `waiting` takes a predicate a reader evaluates against your `head`, for when
nothing is wrong and nothing is openable — a check that has not reported is not a defect
and does not deserve an issue. `rec=wait` is coherent when either is populated; `blocked=ci`
satisfies neither and is the error the split exists to remove.

**`rec` is the disposition of the window.** `merge`, `approve` and `stop` each need a person
before anything moves; `wait` and `continue` do not.

### Name the window too

```sh
tmux rename-window -t "$TMUX_PANE" pr<number>
```

The name is the fallback identity when no state is published, and it is a good one: set
once, unaffected by the report scrolling away. `octoshift waiting` reads both, joins them
with GitHub, and reports which windows need a person — a state that contradicts itself
(`rec=merge` at `reviews=0/2`) is reported as a defect rather than silently repaired.

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
  stop, so the worker publishes its state ([above](#stopping-publish-your-state)) at each
  one — that is what lets a round finishing at 03:44 be picked up at 03:45 instead of
  whenever someone next looks.
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
