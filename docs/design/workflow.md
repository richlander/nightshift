# The Nightshift workflow

How one unit of work travels from an idea to a merged commit, and who owns each
step. This is the operational spine that the tools (`turnstile`, `nightshift`,
`octoshift`) and the skills (`nightshift-coordinator`, `nightshift-worker`,
`nightshift-reviewer`) serve. Read [`nightshift.md`](nightshift.md) for the
architecture, [`octoshift.md`](octoshift.md) for the GitHub membrane, and the
`nightshift-reviewer` skill for the review gate; this note is the thread that
ties them together.

## Summary

The unit of work is an **order** — one landable PR, bound to at most one issue.
An order is claimed by exactly one worker, delivered as one branch, cleared by an
adversarial review, merged by a human, and **landed** — at which point its
dependents open. A **plan** (`orders.json`) is a DAG of orders for a feature; the
`after` edges express which orders must land before others can start.

Four roles do the work, and their boundaries do not overlap:

| Role | Owns | Never does |
|---|---|---|
| **Planner** | Design → a standard, an `orders.json` DAG, and one issue per order | Execute an order |
| **Coordinator** | Run the shift: register the plan, keep the board, and own the **entire GitHub surface** — open PRs, post the one clearance note, land merges | Write the code; review it |
| **Worker** | Claim one order, build it on its branch, push, `release`; fix review findings on that same branch | Open a PR, post to GitHub, review or gate its own work |
| **Reviewer** | Run the two-model adversarial gate read-only; report the verdict to the coordinator | Edit code; post to GitHub; merge |

> **The coordinator is the only role that writes to GitHub.** Workers and
> reviewers hand their results *inward* — to a branch, to Turnstile, to the
> coordinator — never to the public forum.

This is the accountability story from [`nightshift.md`](nightshift.md) §2 made
operational: the human's judgment is spent on design (day shift) and on the merge
decision, not on mechanics. Everything between "plan committed" and "ready to
merge" runs without the human's attention — and without their name on it.

## The spine: the life of one order

```
 planner        coordinator            worker              reviewer          human
   │  design +      │                    │                    │                │
   │  orders.json ──▶ register (add/plan)│                    │                │
   │  + issues      │  seed specs+ready  │                    │                │
   │                │                    │◀── next (claim) ───┤                │
   │                │                    │  build on branch   │                │
   │                │                    │  push · release ───▶ done            │
   │                │◀── branch pushed ──┤                    │                │
   │                │  open PR ──────────┼────────────────────▶ review (2 mdl) │
   │                │                    │◀── findings ───────┤  read-only     │
   │                │                    │  fix · push ───────▶ re-review      │
   │                │◀────────────────── verdict: clean ──────┤                │
   │                │  post clearance note                    │                │
   │                │                                         │   merge (squash)◀┤
   │                │  land ◀── merged ───────────────────────┼────────────────┤
   │                │  → dependents open                      │                │
```

### 1 — Design and register (planner, then coordinator)

The planner works out the change with the human on the day shift and commits the
result: the **standard** (a design note precise enough that an agent can check
its own work against it) and the **`orders.json`** plan. That commit on `main` is
the *authorization root* — nothing is dispatchable that a human did not first
approve into the repo. The planner files one GitHub issue per order; the order's
`issue` field points at it.

The coordinator registers the plan:

```
nightshift add orders.json          # one-shot seed (idempotent)
nightshift plan --plan orders.json  # live controller: seed, then reconcile until stopped
```

`plan` seeds an immutable `spec` per order and a `/ready/*` row for every order
whose dependencies have all **landed**. The live controller keeps that ready
frontier current as orders land — no manual re-run. See the coordinator skill.

### 2 — Claim and build (worker)

A worker joins the shift and asks for work:

```
nightshift join
nightshift next            # blocks until one ready order is claimed, exclusively
```

`next` hands back exactly one order, mints its branch name
`nightshift/{plan}/{order}`, and records it in Turnstile. The worker:

1. Cuts that branch from fresh `origin/main` **in its own worktree** and
   re-reads its guidance (SKILL.md + `AGENTS.md`) — a long-lived worker is
   self-healing because it re-reads on every order.
2. Builds the order, running `nightshift check` before each commit (which renews
   the lease — the forcing function that proves the claim is still alive).
3. Pushes the branch and reports it in:

   ```
   nightshift release --status done   # "submitted, awaiting merge"
   ```

`done` does **not** advance the DAG — only `land` does. The worker's deliverable
is a **pushed branch**, not a PR. If the order's `issue` is set the worker may
read it with `gh issue view` for context, but that read is the only GitHub it
touches: it never opens a PR, comments, or merges.

### 3 — Open the PR (coordinator)

The coordinator opens the PR from the worker's branch. Opening PRs is a GitHub
write, and every GitHub write is the coordinator's — this is what keeps the
public surface coherent and keeps a dozen agents from each pushing their own
narration onto the repo.

### 4 — Adversarial review (reviewer, and the worker fixes)

The order is not mergeable until it has **two clean reviews from two DIFFERENT
models** on its *final* head. The reviewer runs that gate:

- Two independent models (e.g. one Claude, one GPT; a third for high-blast-radius
  orders), each in its **own read-only worktree** at the exact head. The reviewer
  never edits, stages, commits, or resets.
- Real findings go back to the **worker**, who fixes them on the same branch. A
  new push is a new head, which starts a fresh round — the gate passes only when
  both models are clean on the *same, final* commit.
- Fixing always stays with the worker; a reviewer that fixed the code would be
  grading its own homework on the next round. The loop is bounded: four rounds
  without convergence → `nightshift escalate` for human judgment.

The reviewer's output is a **verdict, not an action**. It reports the models and
the rounds each needed to the coordinator and stops. It does not post to GitHub.
The gate is growing toward an attestation model — SHA-bound verdict records the
coordinator can check before it lands — so trust becomes a machine-checked
invariant rather than a reviewer's word. See the `nightshift-reviewer` skill for
the gate as it runs today.

### 5 — Clear and merge (coordinator, then human)

On a clean verdict the coordinator posts exactly **one** clearance note on the PR
— a sidecar comment naming the models and rounds, nothing more:

```
✅ Adversarial review clear — two independent reviews.

| Model | Rounds |
| --- | --- |
| claude-opus-4.8 | 1 |
| gpt-5.3-codex   | 1 |
```

The deliberation — findings, fixes, re-reviews — never appears on the PR. *GitHub
carries decisions; git carries deliberation.*

The **human** merges (squash). This is the one place the human stays in the loop
by default: dispatch is autonomous, merging is deliberate. It is also where the
identity boundary lives — the merge is the human's, deliberately theirs, until
they choose to dial it up (see *Governance* below).

### 6 — Land (coordinator)

After the merge the coordinator tells Nightshift the order landed:

```
nightshift land /plan/{plan}/order/{order}
```

`land` writes `state=landed`; the live `plan` controller then opens every order
that was `after` this one. That promotion — a dependent going from blocked to
ready with no human touch — is the payoff. Today `land` is a manual call the
coordinator makes after observing the merge; [`octoshift.md`](octoshift.md) is
the future membrane that watches merges and calls `land` automatically.

## Many orders at once

The single-order spine is the interesting case only because it composes. A plan's
`after` edges make the DAG the scheduler:

- **No `after`** → ready immediately.
- **`after: [op1]`** → opens the moment `op1` **lands** (merges), not when its
  worker reports `done`.
- Two orders that both `after: [op1]` open in parallel and are claimed by two
  workers with no collision — distinct claims, distinct fences.

`paths` is each order's file scope and the conflict-avoidance contract: if two
orders would touch the same files, give the second an `after` on the first so a
merge conflict becomes a scheduling wait instead of a race. See
[`nightshift.md`](nightshift.md) §5 for the DAG-as-scheduler details.

## Two systems of record

The workflow has exactly two sources of truth, and one translator between them:

- **Turnstile** is the **dispatch** truth: who claims what, what is ready, what
  has landed. Credential-free, local, GitHub-unaware.
- **GitHub** is the **merge** truth: what actually shipped.
- **Octoshift** (future) is the only component that reads one and writes the
  other — mapping a merged PR back to its order and calling `land`. Until it
  exists, the coordinator is that translator, by hand.

Nightshift itself never calls `gh` and never parses a PR. `land` is a pure
primitive: "this order shipped." It does not know a merge caused it.

## Governance and identity — the "as me" boundary

The reason the coordinator monopolizes the GitHub surface is the reason Nightshift
exists at all: **night-shift agents must not post as the human.** GitHub is a
public forum, and a maintainer's name should mean "I did product and engineering
work here," not "I presided over an uninterpretable storm of AI contributions."

That yields a dial, run today at its most conservative setting:

| | Local (today) | Remote | Factory |
|---|---|---|---|
| **Branches** | worker pushes | worker pushes | worker pushes |
| **PR + clearance note** | coordinator, by hand | coordinator, as a bot/App identity | coordinator/octoshift, automatic |
| **Merge** | human, deliberate | human, deliberate | bot, under policy |

Moving right is a single authority knob on a **distinct bot/App identity** — never
the human's credentials handed to an agent. The human's merge stays the human's
until they deliberately dial it up. [`octoshift.md`](octoshift.md) §6 owns the
mechanics of that identity boundary.

## The loops: rework and escalation

The happy path is `done → clear → merge → land`. Two loops branch off it:

- **Rework.** Between `done` and `land`, `main` moves — landing `op1` can break
  `op2`'s pre-land branch with a merge conflict or a red CI run. This is common.
  The order routes to a `rework` state with a directive; the **worker** rebases
  and re-pushes (octoshift never touches code). `done → land` is a retry loop, not
  a one-shot.
- **Escalation.** Ambiguity or a review that will not converge does not get
  guessed at. `nightshift escalate` records `state=escalated`, which the
  reconciler treats as ineligible — the order waits for a human and is never
  silently reassigned. At night the escalation default is *halt and hold*,
  because nobody is awake to ask.

## Invariants

1. **One order, one landable PR, one worker.** The claim unit, the branch, and
   the merge unit are the same thing.
2. **`landed`, not `done`, advances the DAG.** The human stays in the merge loop;
   everything else is autonomous.
3. **Only the coordinator writes to GitHub.** Workers push branches and fix
   findings; reviewers report verdicts; both hand their work inward.
4. **The builder never grades its own work, and the reviewer never fixes it.**
   Two different models, read-only, on the final head; findings go back to the
   worker; four rounds then escalate.
5. **Night-shift work never posts as the human.** The GitHub surface stays quiet
   and, when automated, wears a distinct bot identity.
