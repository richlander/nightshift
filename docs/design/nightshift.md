# Nightshift

**A deterministic coordination daemon for parallel coding agents.**

*Draft design spec — Rich Lander, July 2026*
*Working name. Commands are settled in shape; names are still open.*

---

## Summary

Nightshift is a CLI tool and background daemon that lets a team of coding agents (Claude Code, Copilot CLI) work one repository in parallel — across worktrees, across machines, and while you sleep — without corrupting shared state, colliding in the merge queue, or burning CI minutes and tokens on work that was never going to land.

It is **not** an orchestrator, an agent framework, or a chat bus. It is the layer underneath those: a small, fast, boring process that owns the state agents contend over and hands out permission.

Two commitments define it.

> **1. The coordinator is not an AI.**
> Every decision Nightshift makes is deterministic, auditable, and cheap. Where judgment is required, it does not guess — it halts and escalates, with a safe default. Models are the workers and the reviewers. They are never the gate.

> **2. Day shift creates value. Night shift amortizes it.**
> A human and one agent spend two days converging on a design. Twelve agents then apply it across fifty sites overnight. The daemon's job is to make that handoff lossless — and to refuse to run anything unattended that hasn't earned it.

The second is the thesis. The first is how it's kept safe.

---

## 1. The problem, concretely

From running 6–12 agents across git worktrees on two machines, on real .NET work. Every failure below is a **systems** failure, not a model-quality failure. A smarter model inside the loop fixes none of them. A dumb, correct process outside the loop fixes all of them.

| Observed | Root cause |
|---|---|
| "Claim a row on the burndown list, then wait 5 minutes so you don't collide with another agent." | A markdown file is not a transaction. This is a dirty read, and the mitigation is a sleep. |
| One agent is designated merge-conflict watcher and told to wait 30 minutes so two don't do it at once. | A singleton lease, implemented as an honor system. |
| Two agents produce PRs that conflict. Discovered at merge time. | Nothing prevented the collision at *assignment* time, when prevention was free. |
| Agents push every commit; Actions minutes evaporate. | No admission control on a paid, finite, shared resource. |
| A review loop ran **29 rounds** — two adversarial reviewers per round — before a human noticed and stopped it. The architecture was broken. It always is. | No convergence detector. The agent inside the loop is structurally incapable of seeing the loop. |
| "Don't worry, I'll be notified when it's done." | See §7. The agent has terminated. Nothing will notify it. Overnight, this is a silent hang. |
| Can't find which terminal window holds the agent working PR 1234. | No registry. |

---

## 2. The shift model

### Design → Pilot → Propagate

**Design (day).** A human and one contextful agent converge on an invariant. Multiple sessions, often multiple days. The output is a **standard**: a design note precise enough that an agent can check its own work against it. This is the expensive step and it is irreducibly human-led. *It happens outside Nightshift.*

**Pilot (day).** The first few applications of a new standard, run serially and supervised — because they will produce amendments. This is a **gate**, not a phase: the standard is not eligible for unattended work until pilot closes.

**Propagate (night or day).** Apply the validated standard across N sites in parallel. Low judgment required, because *the judgment already happened.*

### Day and night are not modes

There is one machine. The only differences are four defaults:

| | Day | Night |
|---|---|---|
| **Agent lifecycle** | Long-lived sessions. Human starts and stops them. | Ephemeral. Daemon spawns and reaps. |
| **Escalation default** | **Ask the human** — they're here. | **Halt and hold** — nobody is. |
| **Work eligibility** | Anything. | Only work with a validated standard. |
| **Ambiguity** | Ask. | Stop. |

Everything else — the claim table, the conflict graph, the registry, the gate, the board — is identical.

**A session is available for shift work, or it isn't. You toggle it.** That is the entire day/night mechanism. Deep design work: don't join. Elastic capacity: join and leave as you go. Night: everything joined, nobody awake.

### Why this is the right accountability story

The standard human-in-the-loop framing — a human who reviews and approves — is weak, because **a human needs context to judge, and the only way to build that context is to do the work.** A supervisor who sees only escalations has no calibration.

Nightshift inverts it. The human's context is built in day shift, *by doing the design*. The night shift then executes only what that context produced. **Unattended work is not permitted unless a human first did the thinking that makes it safe** — enforced by the daemon, not by a prompt.

That is a stronger accountability claim than "a human is in the loop," because it is structural rather than procedural.

---

## 3. Non-goals

Nightshift does **not**:

- **Spawn, wrap, or intercept interactive agents.** It is skill-first. Agents opt in by calling a CLI. It never competes for the launch surface (Squad, Conductor, `claude --worktree`, the Copilot app all want that surface; Nightshift composes with all of them). It *does* spawn headless agents at night — that is a supervisor role, not a wrapper.
- **Define agent roles, personas, or charters.** That is Squad's job.
- **Provide chat.** See §6. There is no pub/sub, no topics, no social channel.
- **Reimplement a merge queue.** GitHub's native queue, Mergify, and Graphite do speculative merge commits, batching, and bisection well. Nightshift feeds them and closes the loop they can't.
- **Replace human judgment.** Its terminal escalation default is always *halt and wait for the operator*.

---

## 4. The work order

**The plan is the artifact.** Everything else serves it.

Day shift ends with the design agent writing a **work order**: a JSON DAG of operations, each backed by a GitHub issue, with a tracking issue as the currency. `1234` is what you name when you say *"work on 1234."*

```jsonc
{
  "order": 1234,                          // tracking issue
  "title": "Finding adoption",
  "standard": "docs/design/finding-adoption.md",
  "pilot": { "operations": [3, 4], "status": "closed" },
  "operations": [
    {
      "op": 4,
      "issue": 1238,
      "title": "Retain acquisition outcomes in LibraryInspection",
      "paths":      ["src/dotnet-inspect/Models/LibraryInspection.cs"],
      "supersedes": ["src/dotnet-inspect/Models/LegacyInspectionResult.cs"],
      "standard":   "docs/design/finding-adoption.md#2",
      "after":      [2]
    }
  ]
}
```

Four fields carry the whole design, and the design agent knows all four at slicing time — and will never know them this cheaply again:

- **`paths`** → claim scope. Conflict prevention, for free.
- **`supersedes`** → the deletion precondition. This catches the accretion failure — the one rule an unsupervised agent will silently violate, because adding is easy and deleting requires knowing what you replaced. **The daemon verifies the file is gone before the PR is night-mergeable.**
- **`standard`** → the pointer that makes the operation checkable against the invariant.
- **`after`** → the DAG. *You* authored the ordering during design, because you know which steps share predecessors.

### The DAG is the scheduler

This is a demotion of the conflict graph, and it's correct: **your judgment about the work's structure beats `git merge-tree`.** The graph becomes a safety net that catches what the DAG missed, not the primary schedule.

The DAG's antichain width is the **parallelism ceiling**. Adding a sixth agent to a plan whose current frontier is three wide buys nothing but tokens. The daemon says so:

```
$ ns board 1234
ready:      op-6, op-8, op-9          (3 wide)
in flight:  op-5 (dev-b, 22m), op-7 (dev-c, 8m)
blocked:    op-10, op-11  →  op-5
joined:     5 sessions
→ 2 sessions idle. op-5 is the bottleneck.
```

### Night eligibility

> **An operation is night-eligible iff it has a validated standard.**

- `standard` points at a design note, **and**
- the note's pilot is closed, **and**
- the operation's `supersedes` and `paths` are declared.

No standard → **day only**. Not a warning at merge time; a refusal at dispatch time. An agent must never receive night work it is not allowed to land.

Loose GitHub issues with no design note are legitimate work — they are simply not *unattended* work.

---

## 5. Command surface

Two vocabularies with opposite constraints. **Agent verbs are paid for in tokens on every call and must be memorizable from a short SKILL.md. Operator verbs are free and can be expressive.**

Naming principle: **plain English on the CLI; the factory vocabulary lives in the docs and the SKILL.md, where it does semantic work.** No `e-stop` when `stop` will do.

### Agent verbs — eight

```
ns join [--work] [--guidance]      register: identity, worktree, branch, pane, availability
ns next [scope]                    claim an operation.  → work | BLOCKED | DRAINING | DONE | NOWORK
ns check                           heartbeat + directives.  → OK | HALT | FENCE_STALE | QUERY | ...
ns standby [--until <event>]       block until summoned. pets only; cattle get USE_CHECK
ns extend --paths <glob>           widen path scope. CAS — CAN FAIL.
ns escalate --reason "..."         stop. I need judgment.
ns release --status done|blocked|declined|escalated [--reason] [--on <op>]
ns leave
```

**`next` and `check` are different questions and were wrongly fused in an earlier draft.**
`next` is a *request for work*, state-changing, called once when idle. An agent two hours into op-6 has no reason to call it.
`check` is a *directive read*, called while working. Its common response is `OK` — two tokens.

**`check` renews the lease.** This is the single most important mechanical decision in the surface: it makes the directive check a **forcing function rather than a request**. An agent that doesn't check loses its work. No hooks, no discipline, no wrapper, no reminder in the prompt — the lease is the enforcement.

**`next` has three scopes, one implementation** (the scope arg narrows a `WHERE` clause):

| Form | Scheduling authority | |
|---|---|---|
| `ns next` | **daemon** — global priority | must explain itself |
| `ns next 1234` | **you** — you named the order | the safe default; what you already do |
| `ns next decompiler` | **daemon, inside your fence** | must explain itself |

Every response carries `paths`, `standard`, `lease`, `fence` — the four fields that make an operation claimable, checkable, and enforceable — plus, when the daemon chose, a one-line `chose:` explaining why. A scheduler that explains itself is one you can trust without reading its code.

Three responses are **not** work and must never be conflated:
`DONE` (order complete) · `BLOCKED` (nothing ready; says what it's waiting on) · `NOWORK` (nothing matches).

### Operator verbs

```
ns start --order 1234 --budget 8h,400k-tokens,200-ci-min
ns drain                    stop dispatching; running agents finish     ← the 95% case
ns stop                     everything stops now. nothing merges.       ← works from a phone
ns board [order]            deviations, escalations, stalls, capacity
ns roster                   who's on, where, and who's wedged
ns where <pr|issue>         rename the pane and switch to it
ns add <order.json>         register a work order
ns remove <op>              dequeue
ns escalations [decline <id>]
ns handover                 the shift report
```

`drain` and `stop` are two genuinely different behaviors and two plain words. There is no third.

---

## 6. There is no chat

This design began as *"moltbook as a CLI app"* — pub/sub topics, a social channel, agents discussing merge conflicts. **It ends with zero chat**, and that inversion is the most important thing in the document.

The coordination problem *looked* like a communication problem. It wasn't. Walk every message you'd want to send:

| You want to say | The agent learns it via | Channel needed? |
|---|---|---|
| "stop" | next `ns check` → `HALT` | no |
| "stop handing out work" | next `ns next` → `DRAINING` | no |
| "your claim is revoked" | next `ns check` → `FENCE_STALE` | no |
| "wait for op-5" | `ns next` → `BLOCKED: op-5` | no |
| "who's working on 1236?" | **you don't ask the agent** — `ns roster` | no |
| "I need help" | `ns escalate` → the queue | no |

Every one is a **state change the agent discovers at its next gate call** — and every agent hits a gate constantly, because that is how it gets work and keeps its lease.

> **A broadcast message can be ignored by a model 30k tokens deep in a refactor. A gate's return value cannot.**

What survives is **point-to-point request/response through the escalation queue**. An agent that is stuck does not broadcast; it escalates, and one entity answers. If you want to discuss, you discuss with the architect.

Killing chat also kills, in one move: the agent-to-agent prompt-injection surface, the O(N²) read cost, message volume management, and the entire topic/subscription model.

### The residual case

*"Who is working on 1236 — announce yourself"* is not a message. It's a **registry lookup**, and the registry answers **correctly even when the agent is wedged**, which a self-report cannot:

```
$ ns roster
dev-a  order 1234  op-6  %3  wt/json-bench    active 40s ago
dev-b  order 1234  op-5  %7  wt/analyzer      IDLE 31m  ⚠
```

`ns where 1236` renames the pane and switches you to it. Then you're *in* the session — just type.

**Liveness is observed, never self-reported.** A phantom-waiting agent (§7) will answer roll call *when you call roll*, because calling roll wakes it. Any liveness check that requires the agent to respond measures the wrong thing.

---

## 7. Harness reality

Two mechanisms in this design depend on runtime behavior, so both were tested empirically against Copilot CLI and Claude Code v2.1.207.

### The phantom wait

An agent that says *"don't worry, I'll be notified when it's done"* and yields has **terminated**. It is not waiting. Background-completion notifications land on a *subsequent turn*, and a yielded agent has no subsequent turn. When you ask "is it done yet," **your asking is what creates the turn** and delivers the queued notification. The agent then truthfully reports success — and concludes it was notified. It was. By you.

By day this is invisible. **At night it is a silent hang**: no human speaks, no turn is created, and the shift quietly ends at 11:40pm.

The agent cannot detect this from the inside — it has no representation for "I am currently blocked," because it does not pause, it *ceases*. **Only an external observer can see it.** Lease held + no `check` in N minutes + no PR opened = phantom wait. This is what `IDLE 31m ⚠` means.

### Capability matrix (measured)

| | Copilot CLI | Claude Code |
|---|---|---|
| **Interactive: background + wake** | ✅ via MCP background task, background bash, or background sub-agent | ✅ native — `Bash(run_in_background)` → `<task-notification>` grants a fresh turn |
| **Interactive: 30-min block** | ✅ verified (1800s; the 600s cap governs the attach window, not process lifetime) | ✅ verified (65s; same mechanism, unbounded by launch window) |
| **Interactive: ask the human** | ✅ `ask_user` | ✅ `AskUserQuestion` |
| **Headless: idle-wake** | ❌ `-p` exits when the model yields with no tool calls | ❌ same; background bash reaped ~5s after final result |
| **Headless: ask the human** | ❌ (`--no-ask-user`) | ❌ |
| **Headless: agent sets exit code** | ❌ `exit 42` in a shell tool → copilot still returns 0 | ❌ same |
| **Headless: coordinator-supplied wake** | ❌ | ✅ `--input-format stream-json` — session stays alive; the coordinator feeds the wake |

**Three consequences, all load-bearing:**

**1. The pets/cattle split is forced by the runtime, not chosen.**
- **Night workers** — headless, either harness, **synchronous**, ephemeral. They block in-turn or exit. They cannot sleep.
- **Night architect** — Claude Code `stream-json` session, hosted by the daemon, which supplies the wake. The only harness that supports a resident, contextful, sleeping agent.
- **Day sessions** — interactive, either harness, can sleep on `ns standby` with full context intact.

**2. `ns standby` behaves differently by mode — and the agent never needs to know.** The daemon knows which sessions it spawned. A headless caller gets `USE_CHECK`. SKILL.md says one line: *"If you can background a process, use `ns standby`. Otherwise call `ns check` before each commit."*

**3. The exit-code finding does not require a stdout wrapper.** The experiment concluded that handoff must go through a wrapper parsing a `DECISION:` token from stdout. **It doesn't — Nightshift is the back-channel.** The agent calls `ns release --status halt --reason "..."`; the daemon writes it to the claim table; the agent then exits with whatever code it likes. **The daemon never reads stdout and never inspects an exit code.** And if the agent dies without releasing, the lease expires and the daemon reclaims. Crash handling for free, from a mechanism that already exists.

Keep the exit-code result as a *warning* in the docs — do not try to signal through exit codes — but the fix is a back-channel, not a wrapper.

---

## 8. Architecture

### Shape

A single native-AOT binary. First invocation starts a background daemon; subsequent invocations are thin clients over a Unix domain socket / named pipe.

```
agent ──CLI/MCP──▶ nsd ──▶ SQLite (WAL, durable)
                    │
                    ├──▶ in-memory claim table (single-writer actor)
                    ├──▶ git observer (merge-tree, worktree list)
                    ├──▶ GitHub poller (issues, PRs, CI, merge queue)
                    ├──▶ supervisor (spawn/reap headless agents; budget)
                    └──▶ board writer (per-agent feed files; markdown render)
```

### Why a daemon

Four things a stateless CLI cannot do:

1. **Zero-contention claims** — an in-memory single-writer claim table makes `ns next` a hashmap operation behind a socket. Sub-millisecond; latency is dominated by process spawn.
2. **Continuous observation** — the conflict graph, CI state, and resource utilization must be computed on a timer, not on demand.
3. **Cross-machine authority** — a single writer of record (§13).
4. **Supervision** — spawning and reaping headless agents, and hosting the night architect's `stream-json` session.

### State classes

| Class | Store | Guarantee |
|---|---|---|
| Claims, leases | In-memory, write-through | Ephemeral. On restart, bump a **generation**; all leases void; next agent call returns `EPOCH_CHANGED → reclaim`. No fsync on the claim path. |
| Work orders, PR state, budgets, escalations | SQLite (WAL) | Durable. Survives restart. |
| Design notes, handover reports | **Markdown in the repo** | Durable, legible, reviewable, diffable. *This pattern is stolen from Squad's `decisions.md` and it is the right call.* |

Coordination state needs atomicity, so it lives in a database. *Knowledge* state needs to be read by humans, so it lives in files.

### Contention

Correctness never depends on a multi-statement transaction. Claims are **single-statement compare-and-swap**:

```sql
UPDATE operations
   SET owner = @agent,
       lease_expires = unixepoch() + @ttl,
       fence = fence + 1
 WHERE id = @id
   AND (owner IS NULL OR lease_expires < unixepoch());
-- changes() == 1 → acquired.  0 → held.
```

Single statements are implicitly atomic: no `BEGIN IMMEDIATE` discipline to get wrong, no read-then-write race, no WAL upgrade deadlock. **Contention becomes a pure performance question, and at 12 agents there isn't one.** The 5-minute stagger disappears in one line of SQL.

Lease expiry is evaluated **at the daemon**, never at the client. Clock skew between laptop and desktop must not decide who owns an operation.

### Fencing tokens

Every claim returns a monotonic `fence`. Every **privileged** operation (merge, force-push, queue mutation) must present it; the daemon rejects any token below the current holder's.

Without this: an agent holds a 30-minute lease, wedges for 35, the lease expires, the daemon reassigns, and the original agent wakes and pushes. Classic, subtle, and it corrupts a merge queue.

### Readers never touch the DB

A long-running reader holding an open transaction pins the WAL and prevents checkpointing. So: **the daemon owns the DB and writes rendered feed files.** `ns board` reads a file. Writers hit the DB; readers hit the filesystem.

**DB for state and gates; files for fan-out.**

---

## 9. Conflict prevention by construction

All worktrees of a repo share one object database, so a single process can compute the full pairwise conflict graph with `git merge-tree` — 12 branches is 66 merges, milliseconds, **no agent cooperation, and it cannot be lied to.**

> **Observed state beats declared state.** Hooks observe *intentions* (this agent invoked Edit on Foo.cs). Git observes *effects* (these two branches will conflict, at these hunks). Effects are what matter.

### Path-scoped claims

1. Operations declare `paths` in the work order.
2. Claiming an operation claims its paths. **The daemon refuses to dispatch** an operation whose paths intersect an active claim.
3. Overlap does not block work — it **serializes** it. A precedence edge is added; op-9 is not dispatched until op-7's PR merges. Overnight, a scheduling delay is nearly free. **A merge conflict has been converted into a wait.**
4. At commit time, the daemon diffs the agent's *actual* touched files against its claim. Drift requires `ns extend --paths`, a CAS against the same registry. **The extension can fail** — and the agent learns it is straying into another agent's territory *before the conflict exists*.

### The honest limit

This prevents **textual** conflicts. It does not prevent **semantic** ones — agent A changes a signature in `Foo.cs`, agent B adds a caller in `Bar.cs`, `merge-tree` reports clean, the build breaks. Disjoint paths make semantic conflicts *more* likely, not less.

That's fine, because the merge queue's speculative merge commit builds and tests the *combined* state, which is the only thing that can catch a semantic conflict.

> **Textual conflicts: prevented at assignment time.**
> **Semantic conflicts: caught in the merge queue.**
>
> Both structural. Nobody has to notice.

---

## 10. Admission control and budget

`ns next` is the admission point. The daemon does not dispatch when the gate is closed.

**Weighted, not counted.** A .NET build is RAM- and I/O-bound; four will thrash long before CPU saturates. Operations carry cost vectors.

**The resource nobody counts: API rate limit and token spend.** Twelve agents for eight hours will hit provider rate limits, and when they do they don't fail cleanly — they stall, retry, and burn wall-clock you cannot recover. Tokens/min and cumulative spend are first-class resource classes. This reframes work smoothing from *don't melt the laptop* to **don't waste the night**.

**CI is an admission-controlled resource.** Agents cannot trigger the expensive path directly; the daemon grants against the shift's Actions budget.

Budgets are metered with a **hard stop**, not advice. When exhausted, agents finish their current lease and stop.

---

## 11. CI, merge, and the review loop

### Don't rebuild the merge queue

GitHub's native queue creates speculative merge commits, batches, and **bisects automatically on batch failure**. Mergify and Graphite add parallel queues scoped by file path and two-step CI. Batches of four cut CI runs to roughly a quarter.

**Nightshift feeds the queue.** It does two things the queue structurally cannot:

**Prevention.** The queue is reactive; it acts on PRs that already exist. Nightshift sees the work order *before the commits land* and prevents the collision at assignment time.

**Remediation.** The queue's worst failure — the one that makes teams turn it off — is the cascading restart: a PR is evicted, every PR behind it gets a new speculative base and restarts CI, and flaky tests turn this into an hour of nothing merging. In a human team, someone has to go rebase. **You have agents.** The daemon detects the eviction and dispatches a rebase. **That is a loop no human team can close, and it is the strongest argument for the whole project.**

### CI burn during the review loop

Pure policy, no cleverness, and probably the bulk of current waste:

- **PR stays draft during the loop.** Required checks and queue entry don't fire on drafts.
- **`concurrency: { group: pr-${{ github.ref }}, cancel-in-progress: true }`** — five pushes in ten minutes currently means five full CI passes; four get cancelled.
- **Two-step CI** — cheap gate (build + unit) during iteration; the expensive suite only at merge-queue time.

**The review loop should burn tokens, not Actions minutes. Today it burns both.**

### Convergence detection

The most expensive failure observed: 29 rounds × 2 reviewers × full PR context. Almost certainly a larger token sink than the coding itself.

Given the existing (and good) practice that **only structurally blocking findings gate a PR** — nice-to-haves are filed as follow-up issues — the finding stream *is* the signal:

- `4, 2, 1, 0, 0` → converged.
- `3, 2, 3, 2, 4` → the architecture is generating new structural leaks as fast as they are patched.

**A sound architecture exhausts its structural problems in two or three rounds. That is what "structural" means.**

| Signal | Rule |
|---|---|
| Novel blocking finding after round 3 | warn |
| Novel blocking finding after round 5 | **halt** |
| **Recurrence** — a finding class re-raised after being marked fixed | 2 → **halt**, regardless of round. A recurrence means the fix did not reach the cause. |
| **Oscillation** — round N reverts hunks from round N−2 | halt. Detectable from `git diff` alone; neither reviewer can see it. |
| LOC rising, blocking findings flat | warn. The signature of patching over a bad design. |

This is a **rework rate**. Excessive rework does not mean the workers are bad; it means the process is not capable. That is the entire argument, in one word manufacturing has used for a century.

**No LLM is required for any of this**, and it is probably the highest-leverage cost control in the system.

### The third-model frame check

After five rounds, all participants have implicitly agreed to the architecture and are negotiating *within* it. A sixth symmetric round cannot see out of the frame. A reviewer from a **different model family** buys **decorrelated error**.

> **Hard constraint: the confirmatory reviewer MUST NOT see the review thread.** Standard and final diff only.

If it reads the prior rounds it anchors on the frame those rounds established, and you have bought a very expensive sixth round. Its prompt is different in kind — not *"find issues"* but *"assume this approach is wrong; make the strongest case."* Its output is a verdict on the frame, not a finding list.

### Abandonment is an output

When a loop fails to converge, the accumulated findings are **a requirements document written in the negative** — every invariant that kept breaking, every constraint absent from the original standard. Today that artifact survives only because a human happened to be watching.

Make it a state transition, and **refuse to let a failed loop close without producing it**:

```
OPERATION → ABANDONED
   ├─▶ DESIGN NOTE   (constraints discovered, invariants violated, why the frame failed)
   └─▶ OPERATIONS[]  (de-risking slices, ordered by uncertainty-reduction per unit cost)
```

The slice ordering objective is unlike anything else in the system: *"what is the cheapest thing we could build that would tell us this new approach is also wrong?"* Spike-first. It is how you avoid discovering a second bad basin at round 29.

---

## 12. Escalation: a queue with a default, never a call

**Roles are self-announced routing labels. Authority is daemon policy.** These must never be conflated.

`ns join --guidance` is a singleton lease — CAS'd, so two agents can't both be architect, and a dead architect's lease expires and the role frees. An agent that announces itself as architect gets *routed the escalations*. **It does not thereby acquire the ability to merge.**

Worst case of a confused or hijacked self-announcement: it gives bad advice, which the daemon refuses to execute because the merge preconditions don't check out. **Annoying, not dangerous.**

If the daemon *called* the architect, the architect's absence, wedging, or confusion would break the system. Instead the daemon **enqueues**, with a safe default:

```
ESCALATION #7   order 1234 / op-5   non-convergence (6 rounds, blocking findings flat)
  default:      HALT, hold for operator
  claimable by: --guidance
```

Nobody claims it → the default fires → the operation halts → you see it at breakfast → nothing merged that shouldn't have.

> **daemon** (deterministic, always right, limited)
> → **architect** (judgment, usually right, sometimes absent)
> → **operator** (authority, slow, asleep)
>
> Each tier degrades to the next-safest. Terminal default is always *halt and wait*.

The architect is an **accelerant, never a link in a chain.** Its absence costs throughput, never correctness.

### The architect is a deputy, not a genius

It is not "the smartest agent." It is **your proxy overnight, operating a delegated mandate**: apply judgment you already expressed to cases the daemon can't resolve mechanically. Within mandate, it acts. Outside mandate, it halts.

When dispatched:

- **Never ask "should we continue?"** An agent asked that will say yes. Load the prior from evidence: *"This operation failed to converge in 6 rounds. Recurring finding class: X. The architecture is presumed broken. Make the case that it is sound, or specify the alternative."* **Burden of proof on continuation.**
- **Give it the loop's full history.** The rounds of findings are the entire point.

---

## 13. Cross-machine

Broadcast messages are commutative. **Claims are not.** Two SQLite instances gossiping over a relay will both grant op-7 at T=0, both return `changes() == 1`, and both succeed. That is multi-master replication discovering why it does not do mutual exclusion. Relay latency is not the cause; it is structural.

Therefore: **one writer of record per repo.** The daemon runs on the always-on host; laptop and desktop reach it over Tailscale; claims are RPCs to that single authority.

- **Fail closed on partition.** Cannot reach the authority → the claim **fails**. Never fall back to a local claim. *A stalled agent is cheap; two agents on op-7 is the bug being eliminated.*
- **The SQLite file never lives on a NAS.** SQLite locking over NFS/SMB is unreliable and the failure mode is silent corruption. Local disk on the daemon host; the NAS is for backup.
- **Repo identity must be path-independent.** `~/src/runtime` and `/home/rich/git/runtime` are the same repo. Key on the **root commit SHA** (`git rev-list --max-parents=0 HEAD`); carry the remote URL as a display label only.

---

## 14. Security

Nightshift's reason for existing is to be the thing that is *safe* to leave running overnight.

1. **Untrusted inputs.** CI logs, PR comments, review bots, and web content are authored by parties you do not control. An agent that reads them and holds merge authority is a prompt-injection target with commit rights.
   → **Merge preconditions are verified by the daemon against the GitHub API, never by a model's reading of a log.** Agents propose; only the daemon executes. A poisoned log cannot merge a red PR, because the model is not the thing that checks.
2. **Scope.** The shift's allowlist — orders, path globs, max merges, no force-push, no direct writes to `main` — is a policy file the daemon enforces. **Not a prompt.** Prompts are advice; policies are enforcement.
3. **No chat means no agent-to-agent injection bus.** Operational directives are typed records. There is no free-form channel for one agent to instruct another.
4. **Budget** — tokens, CI minutes, wall clock — metered with a hard stop.
5. **`ns stop`** works from a phone.

---

## 15. Measurement

Every coordination protocol is paid for in tokens, on the expensive side of the ledger (input reads, cache invalidation). **Nobody in this space has published what coordination costs.**

Nightshift instruments itself:

- Tokens spent on coordination vs. work, per agent, per operation.
- **Marginal throughput per added agent** — where coordination overhead exceeds parallelism gain.
- **Rework rate** — review rounds to convergence, by standard and by reviewer pairing.
- CI minutes per landed PR, before and after prevention.
- **Night-shift failure rate, keyed by design note.**

That last one is the interesting result. **It is an empirical score of how well a design note encodes its own cost function** — the same shape as grounding-evaluation work, applied to design artifacts rather than package docs. A standard whose operations deviate often is a standard that wasn't ready. That tells you where to spend tomorrow's day shift.

### What makes a standard night-eligible (empirical claim, to be tested)

From examining a real, hard-won design corpus (the `Finding` nomenclature/producer/adoption/coordinates docs in `dotnet-inspect`), the properties that appear to matter:

- **A named failure mode**, not a principle. *"An empty match is evidence of a trivial alignment; a manufactured match is a costume."* An agent can check its diff against a named failure. It cannot check it against a vibe.
- **At least one merged exemplar per rule** — a PR *and* a file path. Grounding by worked example.
- **Explicit negative space** — a typed list of what the thing *isn't* is worth more to a model than any amount of what it is.
- **A halt list** — *"these require explicit design before dependent producers rely on them."*
- **An amendment protocol** — *when the standard doesn't fit, stop and file an amendment. Do not improvise.*

Rules that additionally have an **analyzer** are night-*safe* rather than merely night-*eligible*. Many of these rules are analyzer-shaped, and the tool that inspects .NET assembly shape is dotnet-inspect — so the eligibility gate can be your own tool asserting invariants against the built assembly.

**Corollary observed in that corpus:** a rule that must be restated in two documents is a rule under sustained pressure — the fix hasn't reached the cause. That's the same recurrence logic as the convergence detector, applied to design docs. It's the rule most likely to break overnight, and the one that most earns an analyzer.

---

## 16. Relationship to Squad

**Squad is the org chart. Nightshift is the operating system.** They should ship together and neither should absorb the other.

Squad's bet — that multi-agent development can be accessible and legible without heavy orchestration infrastructure, with team knowledge in a versioned `decisions.md` — is correct, and it is why Squad has traction. But a markdown blackboard has no atomicity, and `.squad/externalize` exists precisely because repo-resident coordination state fights worktree and branch workflows.

| Squad has | Nightshift adds |
|---|---|
| `decisions.md` drop-box | Atomic CAS claims, leases, fencing |
| Coordinator agent routes work | A deterministic gate: work is unobtainable except through `ns next` |
| Ralph watch loop (poll, dispatch, escalate) | Leases, admission control, budgets, and a fail-closed escalation queue with safe defaults |
| Members produce PRs | Path-scoped conflict prevention *before the PR exists*; merge-queue feeding; eviction remediation |
| Architect reviews PRs | Convergence detection, round budgets, the third-model frame check, formal abandonment |
| — | Cross-machine coordination |

**What Squad provides that Nightshift will not:** roles, personas, charters, team persistence, onboarding, the natural-language front door. **Nightshift has no opinion about who does the work.**

**Integration surface:** Squad already scans Copilot CLI skill paths and configures MCP in `squad init`. Nightshift ships as MCP server + CLI + skill. **A Squad member is just a joined session** — it calls `ns next` the same way it calls any other tool. No change to Squad's core.

### The divergence, stated plainly

Squad's posture is human-led: a productivity tool, not a replacement, with people accountable for priorities and approvals.

Nightshift's motivating scenario is twelve agents merging PRs overnight.

These are not the same philosophy, and it's worth saying so. The argument — and I think it holds — is that **Nightshift does not merely permit human involvement; it structurally requires it.** Nothing runs unattended without a validated standard, and a validated standard is the output of a human doing the design. The daemon's deterministic gate, enforced scope, fail-closed escalation, and budget stop are what make that safe rather than reckless.

**A coordinator that is not an AI is a stronger accountability story than a coordinator that is.**

---

## 17. Delivery

- **Not a `dotnet-` tool.** Nothing here is .NET-specific — claims, leases, `merge-tree`, path scoping, merge-queue feeding, convergence detection. Prefixing it caps the audience at exactly the moment the interesting adoption is cross-ecosystem.
- **NuGet for credibility; npm because that's where Squad's users are.** A NuGet global tool can set `<ToolCommandName>` to a bare word. Native AOT gives one binary; also ship via npm, brew, curl.
- **Command:** `ns` — agents type it constantly and every character is a token. (Precedent: ripgrep ships as `rg`.)
- **Also ships:** an MCP server manifest, `SKILL.md`, a one-line `AGENTS.md` snippet, and optional hooks JSON for Copilot CLI and Claude Code.
- **Second channel:** Copilot plugin packaging (skill + hooks + MCP config, installable from a repo) — adjacent to the `dotnet skill` marketplace work.

### The pitch

> **Nightshift turns a design into a plan, a plan into operations, and operations into merged PRs — while telling you, continuously, where the plan is wrong.**
>
> Day and night are the same machine. The only difference is whether your session has joined, and whether anyone is awake to answer.

### Naming

**Nightshift** is a nod to the dark factory — and the frame does real work. What makes lights-out manufacturing possible is not comfort with risk; it is that everything is jigged, fixtured, and interlocked so precisely that no runtime judgment is required. **Lights-out is the reward for rigor, not a substitute for it.** That is the entire safety argument, and it predates AI by seventy years.

*Risks:* Apple owns "Night Shift" (display feature) — different category, but a discoverability tax. Mitigate with one word, always paired with a descriptor. *Not settled:* `ns` may collide on npm/nuget; verify before committing. Alternates: Interlock, Tower, Yard.

---

## 18. Build order

Each stage is independently useful. If a later stage is a token furnace, the earlier ones still stand.

| # | Milestone | Value on its own |
|---|---|---|
| 1 | **Registry + board + PR/CI poller** | Immediate value with **zero agent involvement**. Answers "which PR is ready" and "where is that terminal." |
| 2 | **Work orders, claims, leases, fencing, `ns next` / `ns check`** | Kills the 5-minute stagger and the 30-minute honor system. |
| 3 | **Admission control** (RAM, I/O, tokens/min, CI budget) | Stops overnight stalls and Actions burn. |
| 4 | **Conflict graph → path-scoped dispatch + `supersedes` verification** | Prevention by construction. **The differentiated feature.** |
| 5 | **Merge-queue integration + eviction remediation** | The loop no human team can close. |
| 6 | **Convergence detection + escalation queue** | Highest-leverage cost control in the system, and it needs no LLM. |
| 7 | **Supervisor: headless spawn/reap + night architect (`stream-json`)** | Unattended operation, safely. |
| 8 | **Cross-machine authority over Tailscale** | Laptop + desktop as one pool. |

Stages 1–3 are days. Stage 4 is the interesting one. **Stage 6 probably pays for the whole project on its own.**

---

## 19. Open questions

1. **Does the daemon requirement kill adoption?** Squad's zero-infrastructure bet is why it has users. Is there a credible **daemonless single-machine mode** — SQLite as the coordinator, CLI-only, no supervision, no continuous observation — that is good enough to be the default, with the daemon as an opt-in upgrade? *This is the question Brady will ask first.*
2. **Are the convergence thresholds** (novel-after-3 warn, novel-after-5 halt, 2 recurrences halt) **right, or are they an n=2 anecdote?** They should be learned from data — and Nightshift is the thing that collects it.
3. **Does the conflict graph hold up on a repo the size of dotnet/runtime?** 66 `merge-tree` calls is milliseconds on paper; that object database is not small. Needs measurement.
4. **How coarse can path scopes be before serialization eats the parallelism?** There is presumably an optimum, and it is repo-shaped.
5. **Where does the merge-queue boundary sit** when the repo has branch protection you don't control?
6. **Does `ns standby` survive a multi-hour block** in an interactive session, and what happens on machine sleep? Verified to 30 minutes; not beyond.
7. **Is the night architect worth it at all**, or is halt-and-hold sufficient? The resident architect is the most complex component and the only one requiring a specific harness. It may be a v2.