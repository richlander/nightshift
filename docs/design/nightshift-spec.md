# Nightshift

**Coordination for parallel coding agents.**
*Design → Pilot → Propagate. Day shift creates value; night shift amortizes it.*

*Draft spec v0.3 — Rich Lander, July 2026*
*Built on [Turnstile](turnstile.md). Names still open.*

---

## Summary

Nightshift lets a team of coding agents (Claude Code, Copilot CLI) work one repository in parallel — across worktrees, across machines, and while you sleep — without corrupting shared state, colliding in the merge queue, or burning CI minutes and tokens on work that was never going to land.

It is a **CLI plus a family of small controllers** over [Turnstile](turnstile.md), a credential-free coordination store. There is no monolith. There is no god-mode daemon.

Nightshift is deliberately **not GitHub-aware**: agents produce branches; turning those into PRs, merges, and issue updates — and doing so *as whom* — is a separate, governable concern owned by [Octoshift](octoshift.md), the GitHub membrane. Delete Octoshift and Nightshift still coordinates a local swarm; you merge and `land` by hand.

Two commitments define it.

> **1. The coordinator is not an AI.**
> Every decision is deterministic, auditable, and cheap. Where judgment is required, the system does not guess — it halts and escalates, with a safe default. Models are the workers and the reviewers. **They are never the gate.**

> **2. Day shift creates value. Night shift amortizes it.**
> A human and one agent spend two days converging on a design. Twelve agents apply it across fifty sites overnight. Nightshift's job is to make that handoff lossless — **and to refuse to run anything unattended that hasn't earned it.**

The second is the thesis. The first is how it stays safe.

---

## 1. The problem, concretely

From running 6–12 agents across git worktrees on two machines, on real .NET work. **Every failure below is a systems failure, not a model-quality failure.** A smarter model inside the loop fixes none of them. A dumb, correct process outside the loop fixes all of them.

| Observed | Root cause |
|---|---|
| "Claim a row on the burndown list, then wait 5 minutes so you don't collide." | A markdown file is not a transaction. This is a dirty read, and the mitigation is a sleep. |
| One agent is the merge-conflict watcher, told to wait 30 minutes so two don't do it at once. | A singleton lease, implemented as an honor system. |
| Two agents produce conflicting PRs. Discovered at merge time. | Nothing prevented the collision at *assignment* time, when prevention was free. |
| Agents push every commit; Actions minutes evaporate. | No admission control on a paid, finite, shared resource. |
| A review loop ran **29 rounds** — two adversarial reviewers each — before a human stopped it. The architecture was broken. It always is. | No convergence detector. The agent inside the loop is structurally incapable of seeing the loop. |
| *"Don't worry, I'll be notified when it's done."* | §7. The agent has terminated. Nothing will notify it. Overnight this is a silent hang. |
| A provider content filter kills an agent mid-work. | §8. Recoverable — but not by the agent, which cannot see its own death. |
| Can't find which terminal holds the agent working PR 1234. | No registry. |

---

## 2. The shift model

### Design → Pilot → Propagate

**Design (day).** A human and one contextful agent converge on an invariant. Multiple sessions, often days. The output is a **standard**: a design note precise enough that an agent can check its own work against it. This is the expensive step, it is irreducibly human-led, and **it happens outside Nightshift.**

**Pilot (day).** The first few applications of a new standard, run serially and supervised — because they *will* produce amendments. **A gate, not a phase:** the standard is not eligible for unattended work until pilot closes.

**Propagate (night or day).** Apply the validated standard across N sites in parallel. Low judgment required, because *the judgment already happened.*

### Day and night are not modes

One machine. Four defaults differ:

| | Day | Night |
|---|---|---|
| **Agent lifecycle** | Long-lived sessions. Human starts and stops them. | Ephemeral. `ns-spawn` starts and reaps. |
| **Escalation default** | **Ask the human** — they're here. | **Halt and hold** — nobody is. |
| **Work eligibility** | Anything. | Only work with a validated standard. |
| **Ambiguity** | Ask. | Stop. |

Everything else is identical.

> **A session has joined the shift, or it hasn't. You toggle it.**

That is the *entire* day/night mechanism. Deep design work: don't join. Elastic capacity: join and leave as you go. Night: everything joined, nobody awake.

### Why this is the accountability story

The standard human-in-the-loop framing — a human who reviews and approves — is weak, because **a human needs context to judge, and the only way to build that context is to do the work.** A supervisor who sees only escalations has no calibration.

Nightshift inverts it. **The human's context is built in day shift, by doing the design. The night shift executes only what that context produced.** Unattended work is not permitted unless a human first did the thinking that makes it safe — and that is enforced by a gate, not a prompt.

Structural, not procedural.

---

## 3. Non-goals

- **Wrap or intercept interactive agents.** Skill-first. Agents opt in by calling a CLI. Nightshift never competes for the launch surface (Squad, Conductor, `claude --worktree`, the Copilot app all want it; Nightshift composes with all of them). It *does* spawn headless agents at night — a supervisor role, not a wrapper.
- **Define agent roles, personas, or charters.** That is Squad's job.
- **Provide chat.** §6. No pub/sub, no topics, no social channel.
- **Reimplement a merge queue.** GitHub's native queue, Mergify, and Graphite do speculative merge commits, batching, and bisection well. Nightshift feeds them and closes the loop they can't.
- **Replace human judgment.** Terminal escalation default is always *halt and wait for the operator*.

---

## 4. Architecture

### The family

```
turnstile        coordination store: kv · lease · txn · watch
                 NO credentials. NO egress. NO business logic.

nightshift       the CLI — agents and operator. All recipes.
  ns-plan          watches /order/** → writes /ready/*        (no credentials)
  ns-git           conflict graph via git merge-tree           (local, read-only)
  ns-github        PR / CI / merge-queue state                 (read PAT)
  ns-spawn         supervises headless agents                  (holds agent env)
  ns-merge         merges                                      (THE write token)
```

### The kernel is watched; it never calls out

**Turnstile has no plugin model, no hooks, no callbacks.** Controllers *watch* it and act, using **their own** credentials, in **their own** process, in **their own** language.

A kernel that *invokes* plugins needs to know they exist, needs an exec path, and inherits whatever environment it hands them. That's a plugin host, and plugin hosts leak credentials. **A kernel that is watched knows nothing.**

This is the Kubernetes controller pattern, and it gives three things free:

- **A capability is just a claim.** `ns-merge` CASes a singleton lease on `/cap/merge`. Two mergers is impossible. A dead merger's capability expires. **An unclaimed capability is a visible stall** — *"3 slices ready to merge, no merge capability registered"* — not a silent hang.
- **Readiness is computed by a controller.** `ns-plan` derives the ready set; **Turnstile never learns what a DAG is.**
- **The extensibility question dissolves.** The kernel never decides when to defer, because it never defers.

### The threat model is the deployment

There is no god-mode component carefully partitioning itself with a clever argument. **Each deployment decision is explicit and its blast radius is legible:**

| Deployed | Credentials in play | Worst case |
|---|---|---|
| `turnstile` alone | **none** | wasted time |
| `+ ns-git` | none (local, read-only) | wasted time |
| `+ ns-github` | read PAT | disclosure of public data |
| `+ ns-spawn` | whatever the agent env holds | ← **the real boundary. Containerize here.** |
| `+ ns-merge` | write token | ← **can land code. A few hundred lines. Audit it in an afternoon.** |

`ns-merge` is a reviewable security surface. A monolith isn't. **That's a structural fact, not a clever argument.**

### All state is Turnstile rows

There is no separate claim table. No in-memory anything. **Claims, leases, registry, ready set, escalations, and budgets are keys.**

| Class | Example | Writer | Semantics |
|---|---|---|---|
| **immutable** | `/order/1234/op/0005/slice/a/spec` | you, once, at slicing | `?immutable` — **cannot be changed by anything, ever** |
| **contended** | `.../slice/a/claim`, `/cap/merge` | agents / controllers, by CAS | conditional writes only |
| **owned** | `.../slice/a/state`, `/ready/*` | exactly one controller | blind writes are correct |
| **ephemeral** | `/agent/dev-b` | the client, lease-attached | dies with the process |

**Knowledge state does not live in Turnstile.** Design notes, work orders, and handover reports are **markdown and JSON committed to the repo** — durable, legible, diffable, reviewable in a PR. Coordination state needs atomicity; knowledge state needs to be read by humans. *(This pattern is taken from Squad's `decisions.md`, and it's the right call.)*

### Fencing — and there is no epoch

**`mod_revision` is the fence.** Turnstile issues one per key, globally monotonic, for free. **There is no generation counter, no epoch, and no global version anywhere in Nightshift.**

> **A fence is scoped to the thing it protects.** Per-**claim** for agents. Per-**capability** for controllers. Turnstile issues both, at exactly the right granularity, at no cost.

**Agent claims.** An agent holds a 45-minute lease, wedges for 50, the lease expires, the slice is reassigned — and the original agent wakes and pushes. Classic, subtle, corrupts a merge queue. Prevented because every privileged act presents the `mod_revision` it saw on *its* claim, and a stale one is rejected.

**Controller capabilities.** The corresponding hazard: `ns-merge` is SIGSTOPped (or its laptop sleeps), its capability lease on `/cap/merge` expires, a replacement acquires it, and the zombie wakes believing it still holds the capability. Prevented the same way — **`/cap/merge` is a key, so it has a `mod_revision`:**

```
acquire /cap/merge  →  mod_revision = 4102.  Remember R = 4102.

before every merge:
  POST /txn
    compare: /cap/merge  mod_revision == 4102
    success: put /merge/<pr> {state: merging, by: <pid>}
  succeeded → merge.
  412       → I no longer hold the capability. STOP. Exit.
```

The zombie's `R` is stale, its Txn fails, **and it never calls GitHub.**

**Why not a global epoch.** An earlier draft had one, bumped on controller startup. It fences *everything against everything* — restart `ns-github` (a read-only poller that cannot cause harm) and you invalidate `ns-merge`'s fence, which was fine. That is the same over-broad invalidation as the volatile claim table, one layer up. **Turnstile's per-key revision was the answer both times.** Also: Turnstile already *has* a global monotonic integer — the revision — so a second one is redundant by construction.

**The residual window, honestly.** The fenced Txn closes the gap between *"do I still hold this"* and *"record my intent"* — but not between that write and the GitHub call. A zombie could pass the fence and be descheduled for 30 seconds before the merge lands. Two things bound it: the window is milliseconds and requires the process to be stopped *inside* it; and GitHub is idempotent enough (a merge queue is itself a serialization point; an already-merged PR cannot merge twice; a PR whose head moved fails its precondition). **Perfect fencing would require the external system to check the token, and GitHub doesn't.** Chubby's sequencers have the same residual window and Burrows says so. Bound it and move on.

### ⚠ A controller restart is not a system event

**This corrects an error in earlier drafts**, which had claims living in a volatile table voided by a generation bump on daemon restart. **That was wrong.**

Ask what actually breaks when a controller restarts. **Not the claim.** The claim is a durable fact and it is still true: agent A is still running, still working, still holds op-7. *Nothing about A changed.*

What breaks is **`ns-spawn`'s handle to the child it launched.** That's the entire discontinuity — and it's a *supervisor* problem, not a claim problem. The git observer, the GitHub poller, and the conflict graph all rebuild happily; they're level-triggered and derive from the world. Only the supervisor holds a handle.

**So don't void every claim in the system because one controller lost some PIDs.** That's a global cache invalidation to fix a local problem.

**The fix is to write the handle down:**

```
ns-spawn records {pid, host} in the agent's registry value at spawn.

ns-spawn restarts:
  list /agent/*                       # durable, in Turnstile
  for each on this host:
    pid alive?  → adopt it. resume keepalive. done.
    pid dead?   → nothing to do; the lease was already expiring.
```

Five lines. **The handle was never lost — it was never in memory to begin with.**

> **A controller restart is not a system event. Controllers are level-triggered and reconstruct from the log. If a controller cannot reconstruct something, that thing was never written down — and the fix is to write it down, not to invalidate the world.**

**No epoch is needed to make this safe.** A restarting controller re-lists and reconciles; a *zombie* controller is fenced by the `mod_revision` on its own capability key. Both hazards are handled locally. See §Fencing.

*And the performance argument for a volatile claim table was wrong anyway:* a WAL append is tens of microseconds, inside a CLI call that costs 2–6 ms, inside a model turn that costs 1–10 seconds. **Optimizing the fsync is unmeasurable.**

### Cross-machine

**One Turnstile.** It runs on the always-on host; laptop and desktop reach it over Tailscale.

Broadcast messages are commutative; **claims are not.** Two stores gossiping would both grant op-7 at T=0 and both succeed. That is multi-master replication discovering why it does not do mutual exclusion. Relay latency is not the cause; it is structural.

- **Fail closed on partition.** Cannot reach Turnstile → the claim **fails**. Never fall back to a local claim. *A stalled agent is cheap; two agents on op-7 is the bug being eliminated.*
- **Global:** work orders, claims, leases, registry, escalations, **token/rate-limit budget** (per-*account*, not per-machine), CI budget.
- **Local:** machine resources (RAM/IO semaphore), worktrees, WIP refs, pane control.
- **Machine-pinned work:** `git log origin/<branch>..HEAD` non-empty ⇒ local-only state ⇒ dispatchable only on that host. **The pin is derived, not tracked.**
- **The SQLite file never lives on a NAS.** Locking over NFS/SMB is unreliable and the failure mode is silent corruption.
- **Repo identity is path-independent:** key on the root commit SHA (`git rev-list --max-parents=0 HEAD`). `~/src/runtime` and `/home/rich/git/runtime` are the same repo.

---

## 5. The work order

**The plan is the artifact.** Everything serves it.

Day shift ends with the design agent writing a **work order**: a JSON DAG of operations, each backed by a GitHub issue, with a tracking issue as the currency. `1234` is what you name when you say *"work on 1234."* **It is committed to the repo.**

```jsonc
{
  "order": 1234,
  "title": "Finding adoption",
  "standard": "docs/design/finding-adoption.md",
  "pilot": { "operations": [3, 4], "status": "closed" },
  "operations": [
    {
      "op": 4,
      "issue": 1238,
      "title": "Retain acquisition outcomes in LibraryInspection",
      "slices": [
        { "slice": "a",
          "paths":      ["src/dotnet-inspect/Models/LibraryInspection.cs"],
          "supersedes": ["src/dotnet-inspect/Models/LegacyInspectionResult.cs"],
          "standard":   "docs/design/finding-adoption.md#2" }
      ],
      "after": [2]
    }
  ]
}
```

**The slice is the claim unit. The issue is the parent.** Use GitHub's native sub-issues for the hierarchy: tracking issue → operation issues → PRs.

Four fields carry the design, and the design agent knows all four at slicing time — and will never know them this cheaply again:

- **`paths`** → claim scope. Conflict prevention, free.
- **`supersedes`** → the **deletion precondition**. This catches the accretion failure — the one rule an unsupervised agent silently violates, because adding is easy and deleting requires knowing what you replaced. **`ns-merge` verifies the file is gone before the PR is night-mergeable.**
- **`standard`** → the pointer that makes the slice checkable against the invariant.
- **`after`** → the DAG.

**The spec key carries the SHA of the committed work order it came from.** The authorization chain is unbroken: *this slice exists because that JSON, at that commit, in a PR you approved, said so.* Not because a key appeared in a database. Combined with `?immutable`, **"the system cannot fabricate or alter authorized work"** is a property of the store, not a rule people follow.

### The DAG is the scheduler

**Your judgment about the work's structure beats `git merge-tree`.** The conflict graph is a safety net that catches what the DAG missed, not the primary schedule.

The DAG's antichain width is the **parallelism ceiling**:

```
$ nightshift board 1234
ready:      op-6, op-8, op-9          (3 wide)
in flight:  op-5 (dev-b, 22m), op-7 (dev-c, 8m)
blocked:    op-10, op-11  →  op-5
joined:     5 sessions
→ 2 sessions idle. op-5 is the bottleneck.
```

Adding a sixth agent to a three-wide frontier buys nothing but tokens.

### Night eligibility

> **A slice is night-eligible iff it has a validated standard.**

`standard` points at a design note **and** the note's pilot is closed **and** `paths` and `supersedes` are declared.

No standard → **day only**. Not a warning at merge; **a refusal at dispatch.** An agent must never receive night work it is not allowed to land.

Loose GitHub issues are legitimate work. They are simply not *unattended* work.

---

## 6. Command surface

Two vocabularies, opposite constraints. **Agent verbs are called constantly and must be memorizable from a short SKILL.md. Operator verbs are free and can be expressive.**

Naming principle: **plain English on the CLI.** The factory vocabulary (standard work, first article, rework rate, jidoka) lives in the docs and the SKILL.md, where it does semantic work at zero token cost — models have absorbed a century of manufacturing literature and *"pull the andon cord"* means something to them that *"escalate"* doesn't. But **no `e-stop` when `stop` will do.**

> **Lineage.** `escalate` is the [andon cord](https://en.wikipedia.org/wiki/Andon_(manufacturing)) of the Toyota Production System: any worker may stop the line to surface a defect, and the line never hides a problem to keep moving. Commitment 1 — *"where judgment is required, the system does not guess; it halts and escalates with a safe default"* — is `jidoka` (autonomation: stop-on-defect, human decides). The convergence was independent, but the debt is real.

### Agent verbs — eight

```
nightshift join [--work] [--guidance]   register: identity, worktree, branch, pane, availability
nightshift next [scope]                 claim a slice.  → work | BLOCKED | DRAINING | DONE | NOWORK
nightshift check                        heartbeat + directives.  → OK | HALT | FENCE_STALE | QUERY
nightshift standby [--until <event>]    block until summoned.  pets; cattle get USE_CHECK
nightshift extend --paths <glob>        widen scope. CAS — CAN FAIL.
nightshift escalate --reason "..."      stop. I need judgment.
nightshift release --status done|blocked|declined|escalated|refused [--reason]
nightshift leave
```

**`next` and `check` are different questions.** `next` requests work — state-changing, called once when idle. An agent two hours into op-6 has no reason to call it. `check` reads directives while working. Its common response is `OK` — two tokens.

> **`check` renews the lease.**

That is the most important mechanical decision in the surface. It makes the directive check a **forcing function, not a request.** An agent that doesn't check loses its work. **No hooks, no discipline, no wrapper, no reminder in the prompt** — the lease is the enforcement. *(Same move as `next` being the only way to get work.)*

**`next` has three scopes, one implementation** — the scope arg narrows a prefix range:

| Form | Scheduling authority | |
|---|---|---|
| `next` | **the system** — global priority | must explain itself |
| `next 1234` | **you** — you named the order | the safe default; what you already do |
| `next decompiler` | **the system, inside your fence** | must explain itself |

Every response carries `paths`, `standard`, `lease`, `fence`, plus — when the system chose — a one-line `chose:` explaining why. **A scheduler that explains itself is one you can trust without reading its code.**

Three responses are **not work** and must never be conflated:
`DONE` · `BLOCKED` (says what it's waiting on) · `NOWORK`.

### Operator verbs

```
nightshift start --order 1234 --budget 8h,400k-tokens,200-ci-min
nightshift drain               stop dispatching; running agents finish    ← the 95% case
nightshift stop                everything stops now. nothing merges.      ← works from a phone
nightshift board [order]       deviations, escalations, stalls, capacity
nightshift roster              who's on, where, and who's wedged
nightshift where <pr|issue>    rename the pane and switch to it
nightshift add <order.json>    register a work order
nightshift remove <slice>
nightshift escalations [decline <id>]
nightshift handover            the shift report
nightshift refusals            per-glob content-filter map
```

`drain` and `stop` are two genuinely different behaviors and two plain words. There is no third.

### Role waits are first-class verbs

Each role's core loop verb is its **filtered blocking wait**:

- `work` (worker) waits for a claimable order, control signals, fence loss, or directive answers.
- `coordinate` (coordinator) waits for coordinator-actionable transitions: `done`, `landed`, `escalated`, or worker-death requeue.
- `plan` (planner) waits for planner-actionable shaping work.

All three share one contract:

1. **Wake on raw edge, reconcile internally.** Turnstile watch is key+op+revision only. The verb wakes on a scoped edge, reads the relevant key values, applies the role predicate, and returns **only** on a predicate match. Non-matching edges re-arm internally.
2. **Return one token + minimal payload.** The first line stays machine-legible and includes enough payload (plan, order base, transition/new status) for immediate action without a second query.
3. **One-shot semantics.** The verb returns on the first actionable match; callers re-invoke to re-arm.
4. **Presence heartbeat while parked.** While blocked, the role renews presence on cadence so liveness remains externally observable.
5. **Blocking mode is edge-triggered.** Standing states are surfaced by explicit board reconcile (or a deliberate `--once` probe), not replayed on every blocking re-arm.

For the coordinator, `DRAINING` is a transition signal: blocking `coordinate` returns it when drain begins while parked, but a pre-existing draining flag does not short-circuit startup (drain still requires coordinating land/escalation completion).

This also resolves the `plan` name collision: the wait **absorbs** the existing plan-controller reconcile loop. `plan` already watches and reconciles; adding planner-actionable projection to that same loop is the natural extension.

### The lease belongs to the process, not the model

An LLM has no durable state. It ceases and resumes; it resets, compacts, forgets. **Any design where the model must remember a token is broken by construction** — it is the phantom wait wearing a different hat.

So **the agent never sees the lease.** The client owns it, keyed by worktree hash, in a `0600` file under `$XDG_RUNTIME_DIR`. Session identity is `hash(git rev-parse --show-toplevel)` — one agent per worktree is already an invariant.

**Who keepalives:**
- **Cattle:** `ns-spawn` holds the lease. The agent never touches it. When the child exits — cleanly, crashed, OOM'd, content-filtered — keepalive stops.
- **Pets:** the backgrounded `standby` stream keepalives. One process, two jobs.
- **Fallback:** any CLI call renews. 45-minute TTL, so a long build survives a quiet stretch.

**An agent whose context resets entirely does not lose its claim**, because the claim was never in its context. That property is unattainable if the lease ID is a token the model must carry.

SKILL.md needs one line: *"Run `nightshift join` once. Everything after that is handled for you."*

---

## 7. There is no chat

This design began as *"moltbook as a CLI app"* — pub/sub topics, a social channel, agents discussing merge conflicts. **It ends with zero chat**, and that inversion is the most important thing in the document.

**The coordination problem looked like a communication problem. It wasn't.**

| You want to say | The agent learns it via | Channel? |
|---|---|---|
| "stop" | next `check` → `HALT` | no |
| "stop handing out work" | next `next` → `DRAINING` | no |
| "your claim is revoked" | next `check` → `FENCE_STALE` | no |
| "wait for op-5" | `next` → `BLOCKED: op-5` | no |
| "who's on 1236?" | **you don't ask the agent** — `roster` | no |
| "I need help" | `escalate` → the queue | no |

Every one is a **state change the agent discovers at its next gate call** — and every agent hits a gate constantly, because that is how it gets work and keeps its lease.

> **A broadcast message can be ignored by a model 30k tokens deep in a refactor. A gate's return value cannot.**

What survives is **point-to-point request/response through the escalation queue.** A stuck agent does not broadcast; it escalates, and one entity answers. *If you want to discuss, you discuss with the architect.*

Killing chat also kills, in one move: the agent-to-agent injection surface, the O(N²) read cost, message volume management, and the entire topic/subscription model.

### The residual case

*"Who is working on 1236 — announce yourself"* is a **registry lookup**, and the registry answers **correctly even when the agent is wedged**, which a self-report cannot:

```
$ nightshift roster
dev-a  1234/op-6  %3   wt/json-bench   active 40s ago
dev-b  1234/op-5  %7   wt/analyzer     IDLE 31m  ⚠
```

**Liveness is observed, never self-reported.** A phantom-waiting agent answers roll call *when you call roll*, because calling roll wakes it. **Any liveness check that requires the agent to respond measures the wrong thing.**

---

## 8. Harness reality

Two mechanisms depend on runtime behavior. Both were tested empirically against Copilot CLI and Claude Code v2.1.207.

### The phantom wait

An agent that says *"don't worry, I'll be notified when it's done"* and yields **has terminated.** It is not waiting. Background-completion notifications land on a *subsequent turn*, and a yielded agent has no subsequent turn. When you ask "is it done yet," **your asking creates the turn** and delivers the queued notification. The agent truthfully reports success — and concludes it was notified. **It was. By you.**

By day this is invisible. **At night it is a silent hang**: no human speaks, no turn is created, and the shift quietly ends at 11:40pm.

The agent cannot detect this from inside. It has no representation for *"I am currently blocked"* — it does not pause, it *ceases*. **Only an external observer can see it**, and in this design the observer is a lease that stopped being renewed.

### Capability matrix (measured)

| | Copilot CLI | Claude Code |
|---|---|---|
| **Interactive: background + wake** | ✅ MCP background task, background bash, or background sub-agent | ✅ native — `Bash(run_in_background)` → `<task-notification>` grants a fresh turn |
| **Interactive: 30-min block** | ✅ verified (1800s; the 600s cap governs the *attach window*, not process lifetime) | ✅ verified |
| **Interactive: ask the human** | ✅ `ask_user` | ✅ `AskUserQuestion` |
| **Headless: idle-wake** | ❌ `-p` exits when the model yields with no tool calls | ❌ same; background bash reaped ~5s after final result |
| **Headless: ask the human** | ❌ (`--no-ask-user`) | ❌ |
| **Headless: agent sets exit code** | ❌ `exit 42` in a shell tool → still returns 0 | ❌ same |
| **Headless: coordinator-supplied wake** | ❌ | ✅ `--input-format stream-json` — session stays alive; the coordinator feeds the wake |

**Three consequences:**

**1. Pets/cattle is forced by the runtime, not chosen.**
- **Night workers** — headless, either harness, **synchronous**, ephemeral. They block in-turn or exit. **They cannot sleep.**
- **Night architect** — Claude Code `stream-json` session, hosted by `ns-spawn`, which supplies the wake. **The only harness supporting a resident, contextful, sleeping agent.**
- **Day sessions** — interactive, either harness, sleep on `standby` with context intact.

**2. `standby` behaves differently by mode — and the agent never needs to know.** `ns-spawn` knows which sessions it spawned. Headless callers get `USE_CHECK`. SKILL.md says: *"If you can background a process, use `standby`. Otherwise call `check` before each commit."*

**3. The exit-code finding does not require a stdout wrapper.** The experiment concluded handoff must go through a wrapper parsing a `DECISION:` token from stdout. **It doesn't — Nightshift is the back-channel.** The agent calls `nightshift release --status refused`; the state lands in Turnstile; the agent exits with whatever code it likes. **Nothing reads stdout. Nothing inspects an exit code.** And if the agent dies without releasing, **the lease expires and the slice is reclaimed.** Crash handling for free, from a mechanism that already exists.

*Keep the exit-code result as a warning in the docs. Drop the remedy.*

---

## 9. Supervision and recovery

### Death is a lease

> **Agent death = lease expiry = key deletion = a watch event = a controller reacts.**
>
> **One mechanism. No special case.**

Phantom wait, crash, OOM, machine reboot, content filter — **all identical.** Keepalive stops; the claim key vanishes; `ns-plan` returns the slice to `/ready/`; `ns-spawn` respawns.

**There is no dead-agent detector anywhere in the system. There is a lease.**

### Resume, not respawn

The state was never in the agent — partial work is in the **worktree**, position is in **Turnstile**, intent is in the **work order**. So a dead agent can be re-run. But **the session survives the process**, and resuming is far cheaper than starting over.

Both harnesses support `--resume <session-id>` / `--continue`. `ns-spawn` launched the agent, so it has the session id.

| | Cost | When |
|---|---|---|
| **1. Resume + retry** | ~1 turn | Content filter, transient API error. Budget: **3**. |
| **2. Respawn fresh, rotate model family** | full agent | Resume budget exhausted, or session unresumable |
| **3. Escalate, hold** | — | Respawn budget exhausted |

**Before respawn, `ns-spawn` quarantines the corpse.** Not on the working branch — the dead agent's WIP is *forensic*, unreviewed and unexplained:

```
git stash create  →  update-ref refs/nightshift/corpse/<slice>/attempt-1   # LOCAL ONLY. never pushed.
git reset --hard                                                            # clean tree for attempt 2
```

**Push product; keep evidence.** (And origin has its own scanners: pushing content that tripped a provider's filter into a Microsoft-org repo can raise a repo-level security alert from a false positive at 3am.)

Attempt 2's brief: *"attempt 1 died with a provider refusal after touching 3 files. Corpse at `refs/…/attempt-1` on merritt. You probably don't want it."*

### Content filters are flaky, not deterministic

**Empirical:** provider content filters (cyber classifiers) fire on legitimate memory-safety, decompiler, and CVE work. Observed max **2 nudges to converge; never observed a non-convergence.**

The reason: **the classifier scores generated output, and generation is stochastic.** A retry re-samples — different tokens, different verdict. It is *a probabilistic gate on a probabilistic process*, which is why retry works and works fast.

**So it's a flaky call. Retry it.** And the harness has a bug — a content-filter 400 is *transient* and should be retried like a 429. **That's a one-line feature request to the harness teams, and it would fix this for everyone.** File it.

> ### Retry: yes. Rephrase: never.

A retry re-samples the *same intent.* Nothing changes.

A rephrase **changes the artifact to satisfy a classifier.** These refusals are **false positives on legitimate work**, so rephrasing buys exactly zero safety and costs you the thing you were building — a softened unsafety analysis, a renamed identifier, a genericized CVE description. **Silent degradation of the product, undetectable from the output.**

**A dead shift costs you a night. A laundered safety analysis costs you the thing you were building.**

SKILL.md, hard:

> *The API rejected the previous request with a content-filter error. This is a known false positive on legitimate memory-safety and decompiler work. **Reissue the same request unchanged.** Do not rename identifiers, soften language, restructure the analysis, or otherwise alter content to satisfy the filter. After three retries, release with `--status refused` and exit.*

Same rule for you at the keyboard: nudge with *"retry"*, never *"try rewording it."*

**Refusal is a cost signal, not an eligibility gate.** High-refusal globs are still night-eligible; they cost more turns.

```
$ nightshift refusals
glob                          refusals   avg retries   providers
src/**/Unsafety*                   7         1.4        openai(6) anthropic(1)
src/ILInspector.Decompiler/**      3         1.0        openai(3)
```

That's a **budget input** and a **Trusted Access exhibit** — quantified false-positive evidence on a legitimate runtime-engineering repo, which is exactly what such an application wants.

**Respawn count is a deviation signal.** A slice that needed three attempts is telling you the work is wrong, the standard is unclear, or the paths were mis-scoped. Same shape as the rework rate; same place in the handover.

---

## 10. Conflict prevention by construction

All worktrees of a repo share one object database, so `ns-git` computes the full pairwise conflict graph with `git merge-tree` — 12 branches is 66 merges, milliseconds, **no agent cooperation, and it cannot be lied to.**

> **Observed state beats declared state.** Hooks observe *intentions* (this agent invoked Edit on Foo.cs). Git observes *effects* (these two branches **will** conflict, at these hunks). **Effects are what matter.**

### Path-scoped claims

1. Slices declare `paths` in the work order.
2. **`ns-plan` refuses to mark a slice ready** if its paths intersect an active claim.
3. Overlap does not block work — it **serializes** it. A precedence edge; op-9 waits for op-7's PR to merge. Overnight, a scheduling delay is nearly free. **A merge conflict has been converted into a wait.**
4. At commit time, actual touched files are diffed against the claim. Drift requires `nightshift extend --paths`, a CAS. **The extension can fail** — and the agent learns it is straying into another agent's territory *before the conflict exists*.

### The honest limit

This prevents **textual** conflicts. Not **semantic** ones — agent A changes a signature in `Foo.cs`, agent B adds a caller in `Bar.cs`, `merge-tree` says clean, the build breaks. **Disjoint paths make semantic conflicts more likely, not less.**

Fine, because the merge queue's speculative merge commit builds and tests the *combined* state, which is the only thing that can catch a semantic conflict.

> **Textual conflicts: prevented at assignment time.**
> **Semantic conflicts: caught in the merge queue.**
>
> Both structural. Nobody has to notice.

---

## 11. Admission control and budget

`nightshift next` is the admission point. **`ns-plan` does not mark a slice ready when the gate is closed.**

**Weighted, not counted.** A .NET build is RAM- and I/O-bound; four will thrash long before CPU saturates. Slices carry cost vectors.

**The resource nobody counts: API rate limit and token spend.** Twelve agents for eight hours will hit provider rate limits, and when they do they don't fail cleanly — they stall, retry, and burn wall-clock you cannot recover. **Tokens/min and cumulative spend are first-class resource classes**, and they are **per-account, not per-machine** — an independent reason for one global Turnstile.

This reframes work smoothing from *don't melt the laptop* to **don't waste the night.**

**CI is admission-controlled.** Agents cannot trigger the expensive path; `ns-github` grants against the shift's Actions budget.

Budgets have a **hard stop**, not advice. Exhausted → agents finish their lease and stop.

---

## 12. CI, merge, and the review loop

### Don't rebuild the merge queue

GitHub's native queue creates speculative merge commits, batches, and **bisects automatically on batch failure.** Mergify and Graphite add parallel queues scoped by file path and two-step CI. Batches of four cut CI runs to roughly a quarter.

**Nightshift feeds it**, and does two things the queue structurally cannot:

**Prevention.** The queue is reactive — it acts on PRs that already exist. Nightshift sees the work order *before the commits land*.

**Remediation.** The queue's worst failure — the one that makes teams turn it off — is the **cascading restart**: a PR is evicted, every PR behind it gets a new speculative base and restarts CI, and flaky tests turn this into an hour of nothing merging. In a human team, someone must go rebase. **You have agents.** `ns-github` detects the eviction; `ns-spawn` dispatches a rebase.

**That is a loop no human team can close, and it is the strongest argument for the whole project.**

### CI burn during the review loop

Pure policy, no cleverness, and probably the bulk of current waste:

- **PR stays draft during the loop.** Required checks and queue entry don't fire on drafts.
- **`concurrency: { group: pr-${{ github.ref }}, cancel-in-progress: true }`** — five pushes in ten minutes currently means five full CI passes; four get cancelled.
- **Two-step CI** — cheap gate during iteration; the expensive suite only at merge-queue time.

**The review loop should burn tokens, not Actions minutes. Today it burns both.**

### Convergence detection

The most expensive failure observed: **29 rounds × 2 reviewers × full PR context.** Almost certainly a larger token sink than the coding itself.

Given the existing (good) practice that **only structurally blocking findings gate a PR** — nice-to-haves are filed as follow-ups — **the finding stream *is* the signal:**

- `4, 2, 1, 0, 0` → converged.
- `3, 2, 3, 2, 4` → the architecture is generating new structural leaks as fast as they are patched.

**A sound architecture exhausts its structural problems in two or three rounds. That is what "structural" means.**

| Signal | Rule |
|---|---|
| Novel blocking finding after round 3 | warn |
| Novel blocking finding after round 5 | **halt** |
| **Recurrence** — a finding class re-raised after being marked fixed | 2 → **halt**, regardless of round. *A recurrence means the fix did not reach the cause.* |
| **Oscillation** — round N reverts hunks from round N−2 | halt. Detectable from `git diff`; **neither reviewer can see it.** |
| LOC rising, blocking findings flat | warn. The signature of patching over a bad design. |

This is a **rework rate.** Excessive rework doesn't mean the workers are bad — **it means the process is not capable.** That is the entire argument, in a word manufacturing has used for a century.

**No LLM is required for any of this**, and it is probably the **highest-leverage cost control in the system.**

### The third-model frame check

After five rounds, all participants have implicitly agreed to the architecture and are negotiating *within* it. **A sixth symmetric round cannot see out of the frame.** A reviewer from a **different model family** buys **decorrelated error**.

> **Hard constraint: the confirmatory reviewer MUST NOT see the review thread.** Standard and final diff only.

If it reads the prior rounds it anchors on the frame those rounds established, and you have bought a very expensive sixth round. Its prompt is different in kind — not *"find issues"* but *"assume this approach is wrong; make the strongest case."* Its output is **a verdict on the frame**, not a finding list.

**Model diversity is also fault tolerance** — the same heterogeneity that decorrelates review error routes around a provider refusal. Two independent reasons for the same architecture.

### Abandonment is an output

When a loop fails to converge, the accumulated findings are **a requirements document written in the negative** — every invariant that kept breaking, every constraint absent from the original standard. *Today that artifact survives only because a human happened to be watching.*

Make it a state transition, and **refuse to let a failed loop close without producing it**:

```
SLICE → ABANDONED
   ├─▶ DESIGN NOTE    (constraints discovered, invariants violated, why the frame failed)
   └─▶ DRAFT ORDER    (de-risking slices, ordered by uncertainty-reduction per unit cost)
```

The slice-ordering objective is unlike anything else in the system: *"what is the cheapest thing we could build that would tell us this new approach is also wrong?"* **Spike-first.** It's how you avoid discovering a second bad basin at round 29.

**It produces a *draft* order for your review — it does not file issues.** See §14.

---

## 13. Escalation: a queue with a default, never a call

**Roles are self-announced routing labels. Authority is a credential.** Never conflate them.

`nightshift join --guidance` is a **singleton lease** — CAS'd, so two agents can't both be architect; a dead architect's lease expires and the role frees. An agent that announces itself as architect gets *routed the escalations*. **It does not thereby acquire the ability to merge — because merge lives in `ns-merge`, which holds a token no agent has ever seen.**

Worst case of a confused or hijacked self-announcement: **it gives bad advice, which `ns-merge` refuses to execute because the preconditions don't check out.** Annoying, not dangerous.

If the system *called* the architect, the architect's absence, wedging, or confusion would break it. Instead it **enqueues**, with a safe default:

```
ESCALATION #7   1234/op-5   non-convergence (6 rounds, blocking findings flat)
  default:      HALT, hold for operator
  claimable by: --guidance
```

Nobody claims it → the default fires → the slice halts → you see it at breakfast → **nothing merged that shouldn't have.**

> **turnstile** (deterministic, always right, limited)
> → **architect** (judgment, usually right, sometimes absent)
> → **operator** (authority, slow, asleep)
>
> Each tier degrades to the next-safest. Terminal default is always *halt and wait.*

**The architect is an accelerant, never a link in a chain.** Its absence costs throughput, never correctness.

### The architect is a deputy, not a genius

Not "the smartest agent." **Your proxy overnight, operating a delegated mandate:** apply judgment you already expressed to cases no controller can resolve mechanically. Within mandate, it acts. Outside mandate, it halts.

When dispatched:

- **Never ask "should we continue?"** An agent asked that will say yes. **Load the prior from evidence:** *"This slice failed to converge in 6 rounds. Recurring finding class: X. The architecture is presumed broken. Make the case that it is sound, or specify the alternative."* **Burden of proof on continuation.**
- **Give it the loop's full history.** The rounds of findings are the entire point.

---

## 14. Threat model

### Rank them

| | Threat | Blast radius |
|---|---|---|
| **T1** | A compromised agent lands malicious code on `main`. | **Catastrophic.** Supply chain. |
| **T2** | A compromised agent exfiltrates credentials. | **Catastrophic and silent.** |
| **T3** | Prompt injection via issue / CI-log content. | The *vector* for T1 and T2. |
| **T4** | A rogue machine DOSes claims. | A wasted night. **Recoverable.** |

T4 is the visible one and the **least** bad — leases expire, `roster` shows a machine holding 40 claims, `nightshift evict --machine laptop` fixes it. T1 and T2 are the ones that end badly.

### The credential split — the single highest-value control

An agent with a bash tool running as you can read `~/.config/gh/hosts.yml`, `~/.ssh/id_*`, `$GITHUB_TOKEN`. **It doesn't need to *use* `gh` — it can `cat` the token and curl the API.** That is your full identity as a Microsoft engineer with write access to dotnet repos.

| | Can | Cannot |
|---|---|---|
| **Agent PAT** (fine-grained, single repo) | read, push to `agent/*`, open PRs | **merge · force-push · touch `main` · workflows · other repos** |
| **`ns-merge` token** | merge, queue, label, comment | — **never in an agent's environment** |

**Omit the Workflows permission.** A push touching `.github/workflows/**` is then **rejected server-side by GitHub** — not by convention, not by a hook. That closes the nastiest escalation path in the design: an agent that can edit a workflow can grant itself anything on the next CI run. It's a checkbox.

**Rulesets on `main`:** require PR, require checks, block force-push. Now even `contents:write` cannot land code.

Then **"agents propose, the system disposes" is not a convention an injected agent can talk its way around. It is a credential it does not possess.**

**Isolation:**
- **Night agents are cattle, and `ns-spawn` owns their environment. Containerize them.** Highest risk (unattended, merge-adjacent, reading untrusted CI logs), lowest friction (headless, nobody waiting). `HOME=/home/agent` (empty), `GH_TOKEN=<scoped PAT>`, and nothing else. *(Watch out: a linked worktree's `.git` is a file pointing at the main repo — you must mount the main `.git` too.)*
- **On Linux, a separate Unix user gets you most of it for free** — `nsagent` cannot read your `0600` `~/.ssh`. No image to maintain, native build speed.
- **Day agents are pets and you launch them**, so the environment isn't controllable. Use the scoped PAT, accept that you're watching, and have the tool **tell you what it can see:** *"⚠ this session can read ~/.config/gh — agents here hold your full GitHub identity."* The tool can't fix your shell; it can refuse to pretend it's safe.

**A GitHub App for `ns-merge` is worth it for provenance alone:** merges land as `nightshift[bot]`, so the history is *honest* about which merges were unattended. For work headed toward dotnet repos, that audit trail is worth more than it costs.

### Issue injection: the boundary you already have

Anyone on Earth can file an issue in a public repo. But:

> **The work order is the brief. Issues are addresses, not content.**

The work order is **committed**, reviewed by you, and is the **sole input to night work.** An agent's brief is composed from the JSON — title, paths, standard, supersedes, DAG position. **No issue body ever enters a night agent's context.**

- An attacker editing an issue at 3am changes **nothing**.
- An issue edited after slicing changes nothing — **the commit SHA is the pin.**
- Poison issues can't become work, **because becoming work requires a human writing a work order.**

Unplanned work (`next decompiler`) *does* need the issue body — and that path is **day-only anyway**, because it has no standard. **The untrusted-content path is exactly the path where a human is present.** Not a coincidence: the same property from two angles.

Defense in depth on the day path:

- **Author allowlist by numeric GitHub ID, not login.** **Logins are reassignable** — delete an account and the handle frees; rename @foo and @foo is available. **Numeric IDs are immutable and never reused.** Match both and treat a mismatch as an *alarm* (legitimate rename, or a transferred account) rather than a silent failure.
- **`author_association`** (`OWNER`/`MEMBER`/`COLLABORATOR`/`NONE`) rides on every issue for free — a second gate at no API cost.
- **Labels are collaborator-gated on most repos**, so `next decompiler` matching on a **label** is meaningfully safer than matching on title text. *The label is itself an authorization signal.*
- **URL/attachment scanning** is incomplete but guards the account-takeover case the allowlist can't.
- **Wrap untrusted bodies when shown to a day agent:** `<untrusted issue body, author @x, not a collaborator>`.

### Agents do not write to GitHub

**The agent PAT has no `issues:write`.** The issue-tracker mess is then *structurally impossible*, and it costs a checkbox.

Only `ns-github` (bot account) writes, and it writes almost nothing:

**One comment on the tracking issue, edited in place — never appended:**

```markdown
<!-- nightshift:status -->
## Nightshift — order 1234 · updated 03:42

| op | slice | state | PR |
|----|-------|-------|-----|
| 4  | a | ✓ merged                       | #1251 |
| 5  | a | ⚠ review r4, not converging     | #1258 |
| 7  | — | blocked → op-5                  |       |

2 deviations · 1 escalation held for morning
```

**Edited in place → no notification spam, and the issue stays readable.** One comment, always current.

**PR bodies with `Closes #1238`.** GitHub's native cross-linking does the rest.

**Nothing else.** No claim comments, no progress updates, no "I'm working on this."

**And `ns-github` never creates issues.** A component that creates issues *and reads issues* is a self-injection loop. Enumerate its entire write surface so review is mechanical:

```
ALLOWED:  merge (gated) · add-to-queue · label · comment · close
DENIED:   create issue · create repo · push to main · force-push · edit workflows
```

**The narrative record — attempts, refusals, review rounds, deviations — goes in the handover report, committed to the repo as markdown.** Durable, diffable, greppable, reviewable in a PR. **Not in the issue tracker.**

### Bounding T4

Claims **must** be global — machine-local claims *guarantee* double-claims, the exact bug being eliminated. So bound the damage instead:

- **Quotas.** Max claims per agent (1–2) and per machine (= its concurrency limit). **A machine cannot claim more work than it can execute.**
- **Leases expire.** A rogue claim self-heals in ≤45 min.
- **Attribution + alarm.** Claims carry machine and agent. A spike in claim rate trips a board warning, **not a silent success.**
- **`nightshift evict --machine <m>`.**
- **Machine liveness is a separate signal from agent liveness.** *Agent dead, machine alive* → confirmable → reclaim. *Machine unreachable* → **do not reclaim.** Hold as `UNREACHABLE` and escalate after a threshold. Fencing makes reclaiming *safe* during a partition, but it's **wasteful** — both agents burn tokens on the same slice and only one can land.

---

## 15. Terminal integration

**tmux only. No other terminal is modeled.**

The daemon observes and controls **one** surface, or none. tmux gives a stable, scriptable pane model; every other terminal gives a bespoke IPC or nothing. **Ghostty has no CLI title control** (rename is a GUI action only). Kitty, wezterm, and iTerm each have their own protocol.

- **tmux sessions:** full pane control. **`nightshift join` harvests `$TMUX_PANE` from the environment** — the agent doesn't need to *know* anything; the env comes along for the ride. Then a **controller renames the pane**, tracking *observed* state:

  ```
  PR opened   → tmux rename-window -t %7 "1240 ⏳"
  CI red      → "1240 ✗"
  escalated   → "1240 ⚠ NEEDS YOU"
  wedged      → "1240 💤 31m"
  ```

  **Zero tokens, works on wedged agents** — which is exactly when a self-rename can't.

  *(This propagates outward for free: tmux with `set-titles on` emits OSC to the outer terminal, so a Ghostty tab shows the active window's title without Nightshift knowing Ghostty exists.)*

- **Everything else: registry only.** `nightshift where 1240` prints machine, window, worktree. **You walk over.**
- **Cross-machine:** tell, don't switch. Pane control across a Tailscale hop eats a weekend for a marginal win.

---

## 16. Measurement

Every coordination protocol is paid for in tokens, on the expensive side of the ledger. **Nobody in this space has published what coordination costs.**

- Tokens spent on coordination vs. work, per agent, per slice.
- **Marginal throughput per added agent** — where coordination overhead exceeds parallelism gain.
- **Rework rate** — review rounds to convergence, by standard and by reviewer pairing.
- CI minutes per landed PR, before and after prevention.
- Refusal rate by path glob and provider.
- **Night-shift failure rate, keyed by design note.**

That last one is the interesting result. **It is an empirical score of how well a design note encodes its own cost function** — the same shape as grounding-evaluation work, applied to design artifacts rather than package docs. *A standard whose slices deviate often is a standard that wasn't ready — and that tells you where to spend tomorrow's day shift.*

### What makes a standard night-eligible (empirical claim, to be tested)

From examining a real, hard-won design corpus (the `Finding` nomenclature/producer/adoption/coordinates docs in `dotnet-inspect`):

- **A named failure mode, not a principle.** *"An empty match is evidence of a trivial alignment; a manufactured match is a costume."* **An agent can check its diff against a named failure. It cannot check it against a vibe.**
- **At least one merged exemplar per rule** — a PR *and* a file path. Grounding by worked example.
- **Explicit negative space** — a typed list of what the thing *isn't* is worth more to a model than any amount of what it is.
- **A halt list** — *"these require explicit design before dependent producers rely on them."*
- **An amendment protocol** — *when the standard doesn't fit, stop and file an amendment. Do not improvise.*

**Rules that additionally have an analyzer are night-*safe* rather than merely night-*eligible*.** Many of these rules are analyzer-shaped — and the tool that inspects .NET assembly shape is `dotnet-inspect`, so the eligibility gate can be your own tool asserting invariants against the built assembly.

**Corollary observed in that corpus:** *a rule that must be restated in two documents is a rule under sustained pressure — the fix hasn't reached the cause.* Same recurrence logic as the convergence detector, applied to design docs. **It's the rule most likely to break overnight, and the one that most earns an analyzer.**

*Why an agent wouldn't find these rules on its own:* **every collapse the design forbids is locally convenient and locally correct-looking.** Returning an empty list on failure simplifies the call site *right there.* The cost is non-local and deferred. The agent doesn't lack the insight — **it lacks the cost function**, and the collapses are the dominant idiom in its training data. **The design note is that cost function, externalized.** That is precisely why it takes two days of day shift, and precisely why it then propagates for free.

---

## 17. Relationship to Squad

**Squad is the org chart. Nightshift is the operating system.** Ship together; neither absorbs the other.

Squad's bet — accessible, legible multi-agent development without heavy orchestration, with team knowledge in a versioned `decisions.md` — is correct, and it's why Squad has traction. But **a markdown blackboard has no atomicity**, and `.squad/externalize` exists precisely because repo-resident coordination state fights worktree and branch workflows.

| Squad has | Nightshift adds |
|---|---|
| `decisions.md` drop-box | Atomic CAS claims, leases, fencing |
| Coordinator agent routes work | A deterministic gate: **work is unobtainable except through `next`** |
| Ralph watch loop | Leases, admission control, budgets, a fail-closed escalation queue |
| Members produce PRs | **Path-scoped conflict prevention before the PR exists**; merge-queue feeding; eviction remediation |
| Architect reviews PRs | Convergence detection, round budgets, third-model frame check, formal abandonment |
| — | Cross-machine coordination |

**What Squad provides that Nightshift will not:** roles, personas, charters, team persistence, onboarding, the natural-language front door. **Nightshift has no opinion about who does the work.**

**Integration:** Squad already scans Copilot CLI skill paths and configures MCP in `squad init`. **A Squad member is just a joined session** — it calls `nightshift next` like any other tool. No change to Squad's core.

### The divergence, stated plainly

Squad's posture is human-led. Nightshift's motivating scenario is twelve agents merging PRs overnight.

Not the same philosophy, and worth saying so. The argument — and it holds — is that **Nightshift does not merely permit human involvement; it structurally requires it.** Nothing runs unattended without a validated standard, and a validated standard is the output of a human doing the design. The deterministic gate, the enforced scope, the fail-closed escalation, the credential split, and the budget stop are what make that safe rather than reckless.

**A coordinator that is not an AI is a stronger accountability story than a coordinator that is.**

---

## 18. Delivery

- **Not a `dotnet-` tool.** Nothing here is .NET-specific. Prefixing it caps the audience exactly when the interesting adoption is cross-ecosystem.
- **NuGet for credibility; npm because that's where Squad's users are.** Native AOT gives one binary; also brew and curl.
- **Turnstile ships separately.** `turnstile-lock`, `turnstile-queue`, and `turnstile-elect` are **usable by anyone today with no agents involved** — a distributed mutex with automatic release-on-death, one binary, no cluster, is a real gap. **Turnstile can have its own adoption curve, and Nightshift becomes a consumer of it — exactly as Kubernetes is a consumer of etcd.**
- **Also ships:** MCP server manifest, `SKILL.md`, a one-line `AGENTS.md` snippet, optional hooks JSON.

### The pitch

> **Nightshift turns a design into a plan, a plan into slices, and slices into merged PRs — while telling you, continuously, where the plan is wrong.**
>
> Day and night are the same machine. The only difference is whether your session has joined, and whether anyone is awake to answer.

### The name

**Nightshift** is a nod to the dark factory, and the frame does real work. What makes lights-out manufacturing possible is not comfort with risk — it is that everything is jigged, fixtured, and interlocked so precisely that **no runtime judgment is required.**

> **Lights-out is the reward for rigor, not a substitute for it.**

That is the entire safety argument, and it predates AI by seventy years.

**Turnstile** names the mechanism: *one at a time, and it keeps count.* Mutual exclusion plus a monotonic log — and **no opinion about who you are.** Authorization is somebody else's job.

---

## 19. Build order

Each stage is independently useful. If a later stage is a token furnace, the earlier ones stand.

| # | Milestone | Value on its own |
|---|---|---|
| 0 | **Turnstile** + `turnstile-lock` / `-queue` / `-elect` | **Standalone product.** Kills the 30-minute merge-watcher honor system with `turnstile-lock` alone. |
| 1 | **Registry + board + `ns-github` poller** | Value with **zero agent involvement.** "Which PR is ready?" and "where is that terminal?" |
| 2 | **Work orders + claims + `next` / `check`** | Kills the 5-minute stagger. |
| 3 | **Admission control** (RAM, I/O, tokens/min, CI budget) | Stops overnight stalls and Actions burn. |
| 4 | **`ns-git` conflict graph → path-scoped dispatch + `supersedes` verification** | **Prevention by construction. The differentiated feature.** |
| 5 | **`ns-merge` + eviction remediation** | The loop no human team can close. |
| 6 | **Convergence detection + escalation queue** | **Highest-leverage cost control, and it needs no LLM.** |
| 7 | **`ns-spawn`: headless spawn/reap, resume ladder, night architect** | Unattended operation, safely. |
| 8 | **Cross-machine over Tailscale** | Laptop + desktop as one pool. |

Stages 0–3 are days. **Stage 4 is the interesting one. Stage 6 probably pays for the whole project on its own.**

---

## 20. Open questions

1. **Does the daemon requirement kill adoption?** Squad's zero-infrastructure bet is why it has users. Is there a credible **CLI-only single-machine mode** — Turnstile as a library against a local SQLite file, no server, no controllers — that is good enough to be the default, with the daemon as an opt-in upgrade? **This is the question Brady will ask first.**
2. **Are the convergence thresholds right, or are they an n=2 anecdote?** They should be *learned from data* — and Nightshift is the thing that collects it.
3. **Does the conflict graph hold up on a repo the size of dotnet/runtime?** 66 `merge-tree` calls is milliseconds on paper; that object database is not small.
4. **How coarse can path scopes be before serialization eats the parallelism?** There is an optimum, and it is repo-shaped.
5. **Where does the merge-queue boundary sit** when the repo has branch protection you don't control?
6. **Does `standby` survive a multi-hour block**, and what happens on machine sleep? Verified to 30 minutes; not beyond.
7. **Is the night architect worth it at all**, or is halt-and-hold sufficient? It's the most complex component and the only one requiring a specific harness. **Possibly a v2.**