# Nightsky

**One pane of glass over [Nightshift](nightshift-spec.md).**
*Turnstile coordinates. Octoshift translates. Nightsky shows you the whole sky — and touches nothing.*

*Draft spec v0.1 — Rich Lander, July 2026*
*Built on [Nightshift](nightshift-spec.md), [Turnstile](turnstile.md), and [Octoshift](octoshift.md). Not yet built — this is the map.*

---

## Summary

A running shift has three planes of truth, and today you read them one window at a time. [Turnstile](turnstile.md)
holds live coordination — claims, leases, the ready set, directives, control flags. [Nightshift](nightshift-spec.md)
projects that into orders — plans, states, branches, the roster, escalations. [Octoshift](octoshift.md) carries the
GitHub reality — which PR opened, whether it is mergeable, whether it merged. `turnstile watch`, `nightshift watch`,
and the planned `octoshift watch` (#34) each render **one** of these. Nobody renders all three at one address.

**Nightsky is that one pane.** It is the day-shift operator's dashboard: for every order, its Turnstile claim and
lease liveness, its Nightshift state and branch, and its Octoshift PR and merge status — on **one row** — plus the
roster, the ready set, the escalation queue, and the control flags, in one view. It answers the only question the
[Coordinator](workflow.md) actually asks at a glance: *where is everything, and what needs me?*

And it holds one doctrine above all, the same one Octoshift's read-only verbs hold:

> **Nightsky renders. It never mutates.**
> It files no claim, renews no lease, writes no key, opens no PR, lands nothing. It is a window, not a control
> panel — `kubectl get --watch`, never `kubectl apply`. Every decision surface stays exactly where it already
> lives; Nightsky only reflects them.

It reaches **one** end — the local Turnstile socket — and derives all three planes from what is already there.
That keeps it a narrow building block in the [vision](nightshift-vision.md)'s sense, and it keeps it
**credential-free**: Nightsky never touches GitHub.

---

## 1. One pane over three planes

An order [exists on three planes at one identity](octoshift.md) — the tri-plane model is Octoshift's, and
Nightsky is the surface that renders it whole. For order `op1` of plan `9001`:

| Plane | Source of truth | What Nightsky shows | Read from |
|---|---|---|---|
| **Turnstile** | live coordination | who holds the claim, lease liveness (fresh / expiring / stale), fence | the socket, directly |
| **Nightshift** | the order projection | state (`ready`/`claimed`/`done`/`blocked`/`rework`/`escalated`/`landed`), branch, ready-set membership, any standing directive | the socket, directly |
| **Octoshift** | GitHub reality | PR number, mergeable, CI rollup, merged/landed | the socket — from what Octoshift already wrote there |

`nightshift where`/`watch` already fuse the first two planes off the Turnstile keyspace (they range `/plan/` and
derive each row from `{base}/state` and `{base}/branch`). Nightsky is the natural completion of that surface: it
adds the **claim/lease** detail Turnstile holds and the **PR/merge** column Octoshift produces, so the three planes
finally share a row.

This is not a new system of record. Turnstile stays the source of truth for claim and dispatch; GitHub stays the
source of truth for merge; the ledger stays the durable audit. Nightsky invents none of them — it is a join over
state that already exists.

---

## 2. Composition: derive from Turnstile, don't wrap the watch CLIs

**Position: Nightsky reads Turnstile directly and derives all three planes from the keyspace. It does not spawn
`turnstile watch` / `nightshift watch` / `octoshift watch` as subprocesses and merge their streams.**

There were two candidate designs:

- **(a) Wrap.** Spawn the three plane-specific `watch` CLIs, parse their `jsonl` streams, and join by order key
  in Nightsky. Each plane owns its own projection; Nightsky is a stream merger.
- **(b) Derive.** Open **one** Turnstile watch and project all three planes from the keys already there — the same
  primitive `nightshift where`/`watch` use — reusing the in-process row-building helpers rather than three
  subprocesses.

**Derive wins**, for four reasons:

1. **One primitive, one connection.** All three planes are already Turnstile rows: the Nightshift projection is
   `/plan/*` (`{base}/state`, `{base}/branch`, `{base}/directive`), the ready set is `/ready/`, the roster is
   `/agent/{id}`, the control flags are `/control/halt` and `/control/draining`, and — critically — Octoshift's PR
   cache is [the opaque `{base}/pr` tier it already writes to Turnstile](octoshift.md). One SSE watch over the
   relevant prefixes sees every plane. Wrapping would re-serialize state out of the kernel only to re-parse and
   re-join it, when a single range + watch already has it joined by key.
2. **It inherits the level-triggered contract for free.** Turnstile's [`/watch`](turnstile.md) emits a `sync`
   once the backlog drains and returns `410 Gone` on compaction; the canonical loop is *range → reconcile → watch
   → on 410 re-range*. Nightsky is exactly that loop with a renderer bolted on — the same shape `nightshift watch`
   already ships. Merging three independent subprocess streams gives up that single, provable freshness boundary.
3. **It stays credential-free** (see §6). A derived Nightsky never runs `gh` and never needs a subprocess that
   does.
4. **It does not depend on the Octoshift observation verbs (#34).** This is the decisive consequence, stated
   plainly:

   > **Wrapping would make the Nightsky build depend on `octoshift watch` (#34), which is not yet built.**
   > Deriving makes Nightsky depend only on the **Turnstile socket** and on Octoshift **populating its PR/merge
   > cache in Turnstile** — a *data* contract, not a *CLI* contract. Nightsky can be built and shipped now,
   > against Turnstile alone, before `octoshift watch` exists.

The cost of deriving is coupling: Nightsky must know each plane's key schema. That coupling already exists inside
`nightshift` (the `where`/`watch` projection), so Nightsky should **reuse those helpers in-process** rather than
re-implement them — keeping the schema knowledge in one place. That is a smaller, more honest dependency than three
subprocesses and a stream-merge protocol.

The Octoshift column is only as rich as what Octoshift has flushed to Turnstile. In local-dev — or before
`octoshift reconcile` runs — that cache is empty, and Nightsky **degrades gracefully**: the PR/merge cells read
`—`, the Turnstile and Nightshift planes still render fully. Nightsky never blocks on a plane that is quiet; a
missing plane is missing data, not an error.

---

## 3. Read-only: it renders, it never mutates

Nightsky sits on the **observation** side of the same line Octoshift draws between `watch` (read-only) and
`reconcile` (acts). Octoshift's read verbs *observe*; its controller *detects, routes, and records*. **Nightsky
does even less than that** — it does not route and it does not record:

- It **files no claim and renews no lease.** It is not a worker; it holds no session, no fence, no lease. Watching
  the sky must never keep an order alive or steal one.
- It **writes no key.** Not the Turnstile PR cache, not the ledger, not a cursor. Unlike `octoshift reconcile`,
  which is the sole writer-through to durable state, Nightsky is a pure reader. It has no durable-write-through
  responsibility because it has no responsibility to anyone but the eyes in front of it.
- It **is not a gate.** It reflects verdicts, states, and GitHub reality; it never decides mergeability, never
  advances the DAG, never lands. Landing stays the Coordinator's / Octoshift's job.
- It **posts nothing to GitHub.** It is credential-free (§6); it could not write there if it wanted to.

> The boundary is firm: **if deleting Nightsky changes any state, Nightsky was built wrong.** A shift runs
> identically whether or not anyone is looking at it. Nightsky is the looking.

This mirrors `kubectl get --watch`: a viewer that streams state and, by construction, cannot perturb it.

---

## 4. The unified model — one order, one row

The minimal unified model is **the tri-plane join, per order**. One row carries:

```
order            claim/lease           state     branch                         PR        merge
/plan/9001/op1   dev-a  lease 40s ✓     claimed   nightshift/9001/op1            #1251     ci ✓ mergeable
/plan/9001/op2   dev-b  lease 31m ⚠     rework    nightshift/9001/op2            #1258     ci ✗ CONFLICTING
/plan/9001/op3   —                      blocked→op1  —                           —         —
```

- **Claim / lease** (Turnstile plane): the holding agent and lease liveness — *fresh*, *expiring soon*, or *stale*
  (swept). Liveness is **observed, never self-reported**, exactly as the [roster](nightshift-spec.md) is; a
  wedged agent shows `⚠` here without having to answer.
- **State + branch + directive** (Nightshift plane): the order's status from `{base}/state`, its branch from
  `{base}/branch`, whether it is in the ready set, and any standing `{base}/directive` (the same `QUERY` a worker
  reads at its gate).
- **PR + merge** (Octoshift plane): PR number, mergeable, CI rollup, merged/landed — from Octoshift's Turnstile
  cache, or `—` when Octoshift is quiet.

Around the per-order rows, Nightsky renders the **board-level** signals a day-shift operator needs in the same
glance:

- **Roster** — every `/agent/{id}` on duty, `active` or `standby`, and where (liveness observed).
- **Ready set** — the `/ready/` frontier: what a `next` would hand out right now.
- **Escalations** — the queue of orders that pulled the andon cord and are waiting on the Coordinator's judgment.
- **Control flags** — is `/control/halt` raised (everything stopped)? is `/control/draining` raised (no new work
  handed out)? These change the meaning of every other row and belong on the same screen.

By default, Nightsky mirrors `nightshift watch`'s **hide-landed** behavior (the op-k default): terminal `landed`
orders drop out so the board shows live work, with an `--all` / `-a` opt-in to include them. The day shift wants
the frontier, not the archive — until it wants the archive.

---

## 5. Surface: snapshot, stream, and a later dashboard

Nightsky offers the same lifetimes the other observation verbs do, because they map to the same `kubectl`
distinction Octoshift already adopted:

| Surface | Analog | Lifetime | Use |
|---|---|---|---|
| **snapshot** (default) | `kubectl get` | one-shot: render once, exit | a glance; scripting; piping `json`/`jsonl` |
| **stream** (`--watch`/`-w`) | `kubectl get --watch` | resident: redraw live on every change, until Ctrl-C | the day-shift dashboard tail |
| **dashboard (TUI)** | a top-like UI | resident, interactive | *later* — a richer layer on the same stream |

- **snapshot** is `nightshift where`, widened to three planes: range the prefixes once, join, print, exit 0.
- **stream** is `nightshift watch`, widened: establish the watch revision *before* snapshotting (so the backlog
  can only overlap the snapshot, never gap it — the same ordering `nightshift watch` is careful about), redraw on
  each change, no polling, Ctrl-C exits clean. Heartbeats distinguish *quiet* from *dead*.
- **dashboard** is explicitly **not** the MVP. A full TUI is a separate concern (see the standalone dashboard
  order) built on the very same stream; Nightsky's first cut is snapshot + stream so the stream contract is proven
  before any UI is layered on it.

**Scope selectors, not raw identifiers.** Every surface takes a Turnstile-native scope — a **plan**, an **order**,
or a **wave** — which narrows the prefix range, exactly as Octoshift takes scope rather than PR numbers. `nightsky`
with no scope means *everything*.

**Output formats reach parity with the other watch verbs:** `table` (default; the redraw view), `jsonl` (one
structured row per change event, for piping to a log or an external dashboard), and a `json`/`jsonl` snapshot for
scripting. Whatever `nightshift watch` and `octoshift watch` emit, Nightsky emits in the same shapes, so a
consumer can pivot between them.

---

## 6. Topology: one local end, credential-free

Octoshift's defining constraint is that it must reach **both** ends — the local Turnstile socket *and* GitHub — so
it is co-located with the daemon **and** holds GitHub auth. Nightsky's defining property is the opposite:

> **Nightsky reaches exactly one end: the local Turnstile socket. It never holds GitHub credentials.**

It runs wherever a `nightshift` verb can — on the daemon's host, or anywhere the socket is reachable (resolved the
same way every command resolves it: `--socket` > `NIGHTSHIFT_SOCKET` > config > `TURNSTILE_SOCKET` > the default).
That is the entire topology. There is no second end to reach.

The Octoshift plane arrives **pre-translated**. Octoshift is the one membrane that carries GitHub authority and
writes PR/merge state into Turnstile's cache; Nightsky **consumes that already-translated state** and never speaks
to GitHub itself. This is the direct payoff of the composition decision in §2 and of Nightshift's
[credential split](nightshift-spec.md) — the single highest-value control in the threat model:

- The credential-carrying surface stays confined to Octoshift.
- Nightsky, a surface a human leaves open all day on the day shift, holds **no** credentials — so leaving it open
  costs nothing and reveals nothing beyond local coordination state.

This is what keeps Nightsky a **narrow building block** in the [vision](nightshift-vision.md)'s sense: it does one
thing — render the shift — and it inherits its GitHub view from the component whose job is GitHub, rather than
growing a second `gh` integration. The day shift gets its one pane; the night shift's credential boundary is
untouched.

---

## 7. What Nightsky is not

- **Not a coordinator.** It hands out no work, lands nothing, resolves no escalation. It shows you the escalation
  queue; *you* decide.
- **Not a writer.** Not to Turnstile, not to the ledger, not to GitHub. Octoshift's `reconcile` is the
  writer-through; Nightsky is only the reader.
- **Not a gate.** It reflects state and verdicts; it never decides mergeability or eligibility.
- **Not GitHub-aware.** It never runs `gh`, holds no token, and works fully (minus the PR column) with no Octoshift
  present at all.
- **Not a new source of truth.** Turnstile owns dispatch, GitHub owns merge, the ledger owns audit. Nightsky joins
  them for the eye; it authors nothing.

---

## 8. The minimal first cut

- **MVP = snapshot, derived, two planes solid + the third when present.** Range `/plan/`, `/ready/`, `/agent/`,
  `/control/`, and the Octoshift `{base}/pr` cache; join per order; print the unified board once. Reuse the
  `where` projection helpers in-process. Depends on Turnstile only.
- **Next = stream (`--watch`).** Wrap the snapshot in Turnstile's range→watch→(410 re-range) loop and redraw on
  change — the same engine `nightshift watch` runs, widened to three planes. `jsonl` event output for piping.
- **Later = the TUI dashboard** on the same stream, and richer Octoshift detail as `octoshift reconcile` populates
  more of the cache. If Octoshift ever exposes state Nightsky wants that is not yet in Turnstile, the fix is
  *Octoshift writes it to the cache* — not *Nightsky learns to call `gh`*. The credential-free boundary is the
  invariant, not a stage.

The smallest useful Nightsky is a one-shot, credential-free, three-plane `where`. Everything else is the same
join, kept live.

---

## 9. Open decisions

1. **Composition** — derive from Turnstile vs. wrap the plane `watch` CLIs. *Resolved:* derive, so the build
   depends on the Turnstile socket (and Octoshift's data contract) rather than on `octoshift watch` (#34).
2. **Octoshift cache shape** — Nightsky needs PR number, mergeable, CI rollup, and merged/landed per order. The
   opaque `{base}/pr` tier holds the PR binding today; whether merge/CI status rides the same key or a sibling
   (`{base}/pr-status`) is Octoshift's call, made so Nightsky can read it without interpreting GitHub.
3. **Wave selector** — plan and order map cleanly to prefixes; "wave" needs a definition (a labeled subset within
   a plan?) shared with Octoshift's scope resolver so both read it the same way.
4. **Lease-liveness rendering** — thresholds for *fresh* / *expiring* / *stale* and whether Nightsky reads lease
   TTL directly or infers it from last-renew; align with how the roster reports liveness so one order does not
   look alive in one column and wedged in another.
5. **Hide-landed default** — mirror `nightshift watch`'s op-k default (`--all`/`-a` to include terminal orders);
   confirm the two flags stay spelled and defaulted identically so muscle memory transfers.
6. **Standalone binary vs. `nightshift` subcommand** — Nightsky is a distinct *surface* but shares the projection
   code; whether it ships as `nightsky` or `nightshift sky` is a packaging question, not a design one.
