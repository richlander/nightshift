# Octoshift

**The GitHub membrane for [Nightshift](nightshift.md).**
*Nightshift coordinates. Octoshift translates. GitHub stays quiet.*

*Draft spec v0.1 — Rich Lander, July 2026*
*Built on [Nightshift](nightshift.md) and [Turnstile](turnstile.md). Not yet built — this is the map.*

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

---

## 4. Inbound: GitHub → Turnstile

### 4.1 Merge → land (the MVP)

The watcher that makes the DAG advance. Poll `gh pr list --state merged` past a `/control/pr-cursor`; for
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

- A PR **closed without merging** → policy: bounce to `rework`, return to the pool, or escalate to a human.
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
GitHub-unaware.

- **Agents never use `gh` as you.** They push branches (optionally under a bot identity) and talk to the
  socket. Only octoshift holds GitHub authority.
- **A distinct bot / GitHub App identity** for anything octoshift authors or merges, so factory actions are
  *attributable and separately revocable* — not indistinguishable from you. The moment anything auto-merges,
  it should not be "you."
- **Merge policy is the spectrum knob:** which plans/labels may auto-merge, required approvals and checks,
  protected paths, and **diff-vs-`paths` enforcement** — refuse to merge a PR that touched files outside the
  order's claim. That reconnects `paths`/`extend` to a real gate.
- **Blast-radius caps:** max lands/merges per interval; a global pause (reuse `/control/halt`). Octoshift is
  where autonomy is bounded.
- **Audit:** every land triggered, PR opened, merge performed — recorded to the ledger, keyed to order and
  actor identity. Same single-writer mechanism as §3.

---

## 7. What octoshift is not

- **Not an agent.** It runs no model, writes no code, resolves no conflict. It detects, routes, records.
- **Not a gate.** It reflects reviewer verdicts and GitHub reality; it never *decides* mergeability.
- **Not inside Nightshift.** Nightshift must build and pass its tests with zero knowledge that octoshift
  exists. If octoshift is deleted, Nightshift still coordinates a local swarm — you just merge and `land` by
  hand.

---

## 8. Form factor and the minimal first cut

- **MVP = an inbound poller**, read-only on GitHub except for reading merges: `gh pr list` + `nightshift land`
  + `/control/pr-cursor`. No webhooks, no bot identity, no auto-merge, no outbound PRs. This is the smallest
  thing that makes the running system autonomous end-to-end.
- **Next:** the rework loop (§4.2) and the single review-clearance note (§5.1) — the two that most improve
  correctness and quiet.
- **Later:** outbound PR creation, the bot identity, auto-merge under policy, and a webhook or merge-triggered
  Action fronting the resident process (which still owns the socket).

---

## 9. Open decisions

1. **Single-writer ledger** — agents → Turnstile → octoshift → orphan branch. Confirmed as the way to avoid
   push contention and unify audit with storage.
2. **Rework routing** — a distinct `rework` state that the reconciler re-readies (preferred, visibly
   different from an untouched `declined`), vs. reusing `declined` → pool with a directive.
3. **Closed-unmerged policy default** — `rework` / pool / escalate.
4. **Ledger sharding** — one branch, per-plan, or per-order. Start with one.
5. **Bot identity** — introduced at first auto-merge, or from the first outbound PR.
