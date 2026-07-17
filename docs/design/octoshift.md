# Octoshift

**The GitHub membrane for [Nightshift](nightshift-spec.md).**
*Nightshift coordinates. Octoshift translates. GitHub stays quiet.*

*Draft spec v0.1 — Rich Lander, July 2026*
*Built on [Nightshift](nightshift-spec.md) and [Turnstile](turnstile.md). Not yet built — this is the map.*

---

## Summary

Nightshift is deliberately **not GitHub-aware**. It coordinates branches and state over a local
[Turnstile](turnstile.md) socket and knows nothing of pull requests, merges, or the `gh` CLI. That is a
feature: agents produce branches; *who* turns those branches into GitHub-visible changes, and *as whom*, is
a separate and governable question.

**Octoshift is the answer to that question.** It is the single membrane between two systems of record:

> Turnstile is the source of truth for **claim and dispatch.**
> GitHub is the source of truth for **merge.**
> Octoshift is the *only* thing that translates between them.

It composes two CLIs it does not link — `nightshift` and `gh` — as subprocesses. It holds the GitHub
authority that agents never touch. And it enforces one doctrine above all:

> **GitHub carries decisions. Git carries deliberation.**
> The GitHub surface — issues, PRs, comments — holds only the terminal signals a human acts on. Every
> spec, status transition, review round, and audit record lives in a git ledger off to the side. The
> overall GitHub presence is **quiet.**

Octoshift is never a gate and never an author of judgment. Models are the workers and reviewers; octoshift
only *reflects* their verdicts and GitHub's reality. It fixes no code, resolves no conflict, and writes no
opinion. It detects, routes, and records.

---

## 1. The membrane, and where it must run

Octoshift has a hard topology constraint that defines it: it must reach **both** ends.

- The **Turnstile socket** is local (a Unix domain socket). To call `nightshift land`, octoshift must run on
  the daemon's host (or a host with the socket tunneled in).
- **`gh`** is remote and authenticated. To read merges and post signals, octoshift must carry GitHub
  credentials.

So octoshift is co-located with Turnstile and holds GitHub auth — it is the one place where "the local swarm"
and "the GitHub account" meet. A merge-triggered GitHub Action *cannot* be the bridge on its own, because CI
cannot reach a local socket. This is why the membrane is a resident process, not a webhook stub.

Everything octoshift does is **inbound** (GitHub → Turnstile), **outbound** (Turnstile → GitHub),
**reconciliation** (keeping the two honest), or **governance** (the "as me" boundary).

---

## 2. Three planes, one address

An order exists on three planes, at one identity:

| Plane | Role | Nature | Address for order `op1` of plan `9001` |
|---|---|---|---|
| **Turnstile** | live coordination: claims, leases, ready set, directives, roster | hot, low-latency, self-healing | `/plan/9001/order/op1` |
| **Work branch** | the landable change | one branch per order, off `origin/main` | `nightshift/9001/op1` |
| **Ledger branch** | the order *domain*: spec + status history + review verdicts + audit | cold, durable, versioned, pushable | `orders/9001/op1/` |

`OrderRef` (in Nightshift) already expresses the first two; octoshift extends it with the ledger path. **One
identity, three planes** — a merged PR's `headRefName` maps straight to a Turnstile key and a ledger
directory.

---

## 3. The ledger — orphan branch(es) as the order database

The order domain does not belong in GitHub issues and PRs. It belongs in a git ledger: **one or more orphan
branches** (a parallel root, no shared history with `main`) holding a directory per order.

```
orders/9001/op1/order.json      # the spec (authored by the coordinator)
orders/9001/op1/status.jsonl    # append-only state transitions (claimed, done, rework, landed)
orders/9001/op1/pr              # the PR this order landed as (durable binding: order ↔ PR)
orders/9001/op1/review.md       # the full adversarial-review record — every round, every model
```

Why a git ledger:

- **Audit for free.** Every transition is a commit. No separate log, no GitHub noise.
- **Durable and portable.** Pushable to the remote; survives daemon restarts; backed up like any branch.
- **Round-trips through Turnstile.** The ledger is *input* (the coordinator authors `order.json`;
  `nightshift add`/`plan` seeds Turnstile from it) **and** *output* (status/audit flushed back). Turnstile is
  the working set; the ledger is the system of record for the domain. This matches Nightshift's intent that
  `orders.json` be sequencing metadata over per-order `order.json` files, with the branch as storage.

### Single-writer

N agents pushing one shared branch is merge hell. So:

> **Agents never write the ledger. They report live status to Turnstile — which is built for concurrent,
> contention-free writes — and octoshift is the sole writer that flushes Turnstile → ledger commits.**

This unifies "audit log" and "durable store": they are the same mechanism. If a single ledger branch gets
hot, shard by plan (`nightshift-orders/9001`); per-order branches are possible but explode the ref count.
Start with one, shard only if needed.

### PRs are derived, not registered

A PR is a GitHub object; Nightshift must not know it exists. It does not need to. Because every order-PR's
head branch is `nightshift/{plan}/{order}`, **the branch namespace makes GitHub self-indexing** — octoshift
reconstructs the full set of order-PRs from GitHub alone, any time:

```
gh pr list --search "head:nightshift/"          # all order PRs
gh pr list --search "head:nightshift/9001/"     # just plan 9001's
```

So PR knowledge lives in three tiers, none of which is a registry Nightshift maintains:

| Tier | Holds | Role |
|---|---|---|
| **GitHub** | the PRs themselves | authoritative, always derivable via the branch prefix |
| **Turnstile** | optional `{base}/pr` | hot reverse-lookup cache while work is live — **opaque to Nightshift**, written only by octoshift |
| **Ledger** | `orders/{plan}/{order}/pr` + `status.jsonl` | durable audit — which PR landed which order, forever |

The Turnstile tier is octoshift bookkeeping: the kernel stores the string and never reads or acts on it, so
Nightshift stays GitHub-unaware. Octoshift writes it (and the ledger binding) when it opens a PR itself; in
local-dev it skips the cache and relies on the convention. **Branches are registered (in Turnstile); PRs are
derived (from GitHub); the branch is the hinge between the two.**

---

## 4. Inbound: GitHub → Turnstile

### 4.1 Merge → land (the MVP)

The `reconcile` loop that makes the DAG advance. Poll `gh pr list --state merged` past a `/control/pr-cursor`; for
each merged PR, resolve the order (`headRefName` → `OrderRef`, cross-checked against the `Nightshift-Order:`
trailer, with `Fixes:` as a weak confirm) and call `nightshift land <base>`. Idempotent via the cursor. This
alone closes the loop for local-dev mode: **you merge; octoshift lands.**

`headRefName` is the join key, not `Fixes: #N` — an order is one PR (unique), but an issue can fan out to
many orders (ambiguous). The branch is machine-set, unique, and survives PR-body edits and branch deletion.

### 4.2 Rework: merge conflict and CI failure (the common case)

`release --status done` means **"submitted, awaiting merge," not terminal.** The gap between `done` and
`land` is where conflicts and red CI live — *especially* because the merge-driven DAG means **landing `op1`
can break `op2`'s branch, which was cut from pre-land `main`.** Octoshift must have a bounce-back path:

- Poll each open order-PR: `gh pr view --json mergeable,statusCheckRollup`.
- `CONFLICTING` or checks red → return the order to a **`rework`** state (the reconciler re-readies it) with a
  directive carrying the specifics (`rebase onto main`; `CI failed: <job/link>`).
- A fresh agent claims it, `recover`s onto the branch, reads the directive, rebases/fixes, re-pushes,
  re-releases `done`.

> Octoshift **never touches code.** It detects and routes; an *agent* rebases. `done → land` is a retry loop
> — which is what a real merge queue is.

### 4.3 Closed-unmerged, and fan-out issue closing

- A PR **closed without merging** → **default: escalate to a human.** A close is a deliberate out-of-band
  act, not a mechanical failure like a conflict or red CI (§4.2, which route to `rework`); bouncing it straight
  back to `rework` would fight the person who just closed it, and returning it to the pool would let another
  agent silently reopen a decision a human made. So the order is **parked and surfaced** for a human, their
  intent left intact. The other two routes are opt-in: **rework** when the close carries a rework
  directive/label (an explicit "redo this"), and **return to the pool** when the order is explicitly abandoned
  or superseded. (Resolved default — §9.5; governed in §6.)
- **Fan-out issue close** is ledger-and-bridge-only value: since an issue can split into several orders, no
  single PR's `Fixes:` closes it. Octoshift holds the cross-order view and closes `#1234` when its **last**
  slice lands.

---

## 5. Outbound: Turnstile → GitHub (the local → remote → factory dial)

This is the spectrum, and it lives here, not in Nightshift:

- **Local-dev (MVP):** octoshift reads merges only. Humans open and merge PRs.
- **Remote-dev:** on `release --status done`, octoshift **opens the PR** from the pushed branch (title/body
  from the spec; trailers already in the commits), applies labels/reviewers/milestone from plan metadata.
- **Factory:** octoshift **auto-merges** when policy is satisfied. The single most dangerous capability — so it
  is gated (§6).

### 5.1 The review-clearance note — one comment, not a thread

The reviewers are agents; octoshift translates their **aggregate verdict** into a *single* GitHub signal. The
anti-pattern to kill (observed on real PRs): a dozen comments of review narration — rounds, reconciliations,
re-reviews, "ready to merge — three clean sign-offs", naming debates — that a merger has to excavate.

Instead:

- Full detail (each model's findings, every round) → the ledger's `review.md`. Quiet.
- The PR gets **one** terminal comment, only when the gate is met, idempotently updated — never a thread:

  > ✅ **Adversarial review clear** — all-clear from Opus 4.8, GPT-5.5, Gemini 3.1 Pro.
  > Full record: `orders/2585/op1/review.md`.

- Not clear → no clearance note; the order bounces to `rework` (§4.2). The human sees "safe to merge / not
  yet," never the deliberation.

The template is open, but the shape is fixed: **verdict + which models signed off + a pointer to the ledger.**

---

## 6. Governance and identity — the "as me" boundary

Octoshift is the choke point where GitHub authority lives. That is the entire reason Nightshift stays
GitHub-unaware. Everything in this section is declared in **octoshift's config file**, read at startup — one
place a human edits and audits, never a value an agent supplies. A compact illustration of the surface:

```
# octoshift config (illustrative keys, not a schema)
identity:
  app:  nightshift-bot         # the GitHub App octoshift acts as (mechanism: #40)
  from: first-outbound-pr      # when the identity takes over authorship
merge:
  auto:      [plan:2585, label:auto-merge]   # eligible plans/labels; empty = never
  approvals: 1                 # required approving reviews
  checks:    [build, test]     # required green checks
  protected: [".github/**", "SECURITY.md"]   # never auto-merge; always escalate
  enforce-paths: true          # refuse a diff outside the order's claim
caps:
  lands-per-hour:  20
  merges-per-hour: 8
  on-cap: pause                # pause, never drop
```

### 6.1 Identity — from the first outbound action, not the first merge

**Agents never use `gh` as you.** They push branches (optionally under a bot identity) and talk to the
socket; only octoshift holds GitHub authority. The governed question is *when* octoshift stops acting as you
and starts acting as a **distinct bot identity** — and the answer is **the first outbound action, not the
first auto-merge.**

The principle that *the moment anything auto-merges it should not be "you"* generalizes: the moment octoshift
**authors** anything on GitHub, that authorship must be attributable to octoshift and separately revocable.
Opening a PR and posting the clearance note (§5.1) are authored acts; if they went out as you, the trail would
read "you opened it, you cleared it, the bot merged it" — the exact blur the boundary exists to prevent, under
a credential you could not revoke without revoking your own. So identity is a **prerequisite of going outbound
at all** (remote-dev, §5), not a later gate bolted on at factory. The inbound-only MVP stays as-you because it
authors nothing — it only reads merges and calls `land` on the local socket.

The identity is **declared in configuration** (the `identity` block above): octoshift reads *which* identity
to act as from its config file at startup, rather than inferring it or hard-coding it. The identity
**mechanism** — provisioning a GitHub App, minting installation tokens, scoping permissions — is #40 and out of
scope here; this section fixes the *when* (first outbound PR) and the governing policy, not the App plumbing.

### 6.2 Merge policy knobs

Auto-merge is the single most dangerous capability, so it is never implicit — an order auto-merges only when
**every** knob in the `merge` block is satisfied:

| Knob | Meaning | Default |
|---|---|---|
| `auto` | allowlist of plans/labels eligible to auto-merge; empty means nothing auto-merges | empty (off) |
| `approvals` | required approving reviews before merge | policy-set |
| `checks` | named checks that must be green | policy-set |
| `protected` | path globs that force escalation — an order touching them never auto-merges | set |
| `enforce-paths` | diff-vs-`paths` enforcement (below) | on |

**Diff-vs-`paths` enforcement** is the knob that reconnects the claim to a real gate. An order's
`paths`/`extend` claim is a coordination contract in Turnstile — the files it promised to stay inside. Before
merging, octoshift diffs the PR (`gh pr diff --name-only`) and compares the touched set against the order's
claim; **if the diff reaches outside the claim, octoshift refuses to merge and flags the order** (bounce to
`rework` or escalate per §4.3). Until now the claim was enforced only by good behavior; here it becomes a
checked precondition at the membrane — the claim you registered is the claim you are held to.

None of these knobs are octoshift *judging the code*. A cleared order (§5.1) is one the reviewers already
passed; the knobs decide only whether an **already-cleared** order may merge *without a human hand on the
button*. Octoshift still reflects a verdict it did not form (§7).

### 6.3 Blast-radius caps

Autonomy is bounded by rate, not only by policy:

- **Caps per interval** — `lands-per-hour`, `merges-per-hour`. A ceiling on how fast the membrane can move, so
  a mis-registered plan or a runaway loop cannot drain a whole backlog before a human looks.
- **Global pause** — the existing `/control/halt` flag. When set, octoshift performs **no outbound mutation**:
  it opens no PR, posts no note, merges nothing. (Inbound `land` also stops; halt is a full stop.) Any actor
  may set it; octoshift itself sets it when an anomaly trips.

**Hitting a cap pauses; it never drops.** A capped order is not rejected or returned to the pool — it waits for
the next interval (or a human clearing `/control/halt`) and resumes where it left off. The distinction matters:
a cap is a rate limit, not a verdict. Octoshift records the pause (§6.4) so the backlog stays visible. When
octoshift trips the global pause itself, it is choosing "stop and be looked at" over "keep going" — the
conservative failure mode.

### 6.4 Audit

Every outbound action is recorded to the ledger, keyed to the order (`OrderRef`) and the **actor identity**
(§6.1), through the **single-writer** mechanism of §3: agents report to Turnstile, octoshift is the sole
ledger writer. No outbound action is silent.

| Outbound action | Ledger artifact | What it appends |
|---|---|---|
| land triggered | `status.jsonl` | a `landed` transition (the same record the inbound loop writes) |
| PR opened | `pr` + `status.jsonl` | the durable order↔PR binding, and a `pr-opened` transition |
| clearance note posted | `status.jsonl` (+ `review.md`) | a `cleared` transition pointing at the full review record already in `review.md` |
| merge performed | `status.jsonl` | a `merged` transition, then the `landed` it triggers |
| cap/halt pause | `status.jsonl` | a `paused` transition naming the cap or `/control/halt` |

Because every row carries the actor identity, the ledger answers "who did this, as whom" for every
GitHub-visible act — the attributability §6.1 exists to guarantee, made durable. Agents never write these
rows; they cannot, which is what keeps the audit trustworthy.

---

## 7. What octoshift is not

- **Not an agent.** It runs no model, writes no code, resolves no conflict. It detects, routes, records.
- **Not a gate.** It reflects reviewer verdicts and GitHub reality; it never *decides* mergeability.
- **Not inside Nightshift.** Nightshift must build and pass its tests with zero knowledge that octoshift
  exists. If octoshift is deleted, Nightshift still coordinates a local swarm — you just merge and `land` by
  hand.

---

## 8. Execution model — `wait`, `watch`, `reconcile`

One watch *engine* observes GitHub; it is surfaced three ways, by lifetime and by whether it mutates. The
split follows `kubectl`, which keeps observation read-only and puts action in a separate controller:

| Verb | `kubectl` analog | Lifetime | Mutates? | Who uses it |
|---|---|---|---|---|
| `octoshift wait <scope>` | `kubectl wait --for=…` | one-shot: **blocks until** a PR resolves, then **returns the change** | no | an agent awaiting a merge |
| `octoshift watch <scope>` | `kubectl get --watch` | **streams** PR state changes until stopped | no | a human/dashboard tailing a wave |
| `octoshift reconcile` | a k8s controller | resident, continuous | **yes** — `land`, ledger, notes | the always-on membrane |

- **`wait`** is the agent primitive from the design discussion: *"spawn it over a set of PRs, backgrounded, it
  returns with the change when one merges."* It resolves on **any** terminal event — merge, conflict, or close
  — not only merges, so it is also how the rework loop (§4.2) is driven in the agent-spawned mode. `--all`
  waits for the whole set instead of returning on the first.
- **`watch`** is the streaming, read-only view — a live tail of PR transitions for a human or a UI. It never
  returns "the change" as a single result; it emits a stream.
- **`reconcile`** is the controller (what earlier drafts called `serve`): it *consumes* the watch engine and
  *adds* the durable side effects. The name is literal — it reconciles GitHub's **merge truth** against
  Turnstile's **dispatch truth**. This is the resident membrane; it owns `/control/pr-cursor` and is the sole
  ledger writer.

### Scope, not PR numbers

Every verb takes a **scope** in Turnstile-native terms — a plan, an order, a wave — never raw PR numbers.
Octoshift resolves scope → branches → PRs via the namespace (`gh pr list head:nightshift/<scope>`). An agent
thinks in orders; octoshift does the GitHub pivot. `reconcile` with no scope means "everything."

### The durable-write-through rule

> A resolution must be delivered to **durable state** — `nightshift land`, the ledger — **not only** to the
> caller that is blocked in `wait`. The caller may be gone by the time the PR merges.

This is why `wait`/`watch` are read-only and `reconcile` is the thing that *acts*: an ephemeral agent may
`wait` and, if still alive, react (its harness supports async continuation); but the guarantee that a merge
becomes a `land` — even overnight, even if every agent has exited — belongs to `reconcile`. Once the merge is
landed, Turnstile carries it the rest of the way: `land` wakes the live `plan` controller, which opens
dependents, which unblocks some worker's `next`. **No live spawner required.**

### The minimal first cut

- **MVP = `reconcile`, inbound only**: `gh pr list --state merged` past `/control/pr-cursor` → `nightshift
  land`. Read-only on GitHub except reading merges. No webhooks, no bot identity, no auto-merge, no outbound
  PRs. The smallest thing that makes the running system autonomous end-to-end, and the only one that survives
  every agent exiting.
- **Next:** the rework loop (§4.2) and the single review-clearance note (§5.1); `wait` falls out of the same
  engine almost for free and is the nicer agent-facing primitive.
- **Later:** outbound PR creation, the bot identity, auto-merge under policy, and a webhook or merge-triggered
  Action fronting `reconcile` (which still owns the socket).

---

## 9. Open decisions

1. **Single-writer ledger** — agents → Turnstile → octoshift → orphan branch. *Resolved:* the way to avoid
   push contention and unify audit with storage.
2. **Verb taxonomy** — `wait` (block+return, read-only) / `watch` (stream, read-only) / `reconcile` (resident
   controller, acts). *Resolved,* following `kubectl`'s read-vs-controller split; `reconcile` replaces the
   earlier `serve`.
3. **PR knowledge** — derived from GitHub via the branch prefix; cached opaquely in Turnstile; recorded
   durably in the ledger. *Resolved:* branches are registered, PRs are derived.
4. **Rework routing** — a distinct `rework` state that the reconciler re-readies (preferred, visibly
   different from an untouched `declined`), vs. reusing `declined` → pool with a directive.
5. **Closed-unmerged policy default** — `rework` / pool / escalate. *Resolved:* **escalate to a human** is the
   default (§4.3) — a close is a deliberate out-of-band act, not a mechanical failure; `rework` and pool are
   opt-in via directive/label.
6. **Ledger sharding** — one branch, per-plan, or per-order. Start with one.
7. **Bot identity** — introduced at first auto-merge, or from the first outbound PR. *Resolved:* from the
   **first outbound PR** (§6.1) — authorship, not only merge, must be attributable; the identity is
   config-declared, and the App mechanism is #40.
