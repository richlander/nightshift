# `octoshift waiting` — what it is for

**A read-only view of agents running across several machines, joined with what GitHub says about
their PRs.**

You run many coding agents at once, each in a tmux window, spread over more than one host. Each agent
knows its own PR and nothing about the others. GitHub knows the PRs and nothing about the agents. The
join between those two lives only in your head, and it is expensive to maintain.

This tool does that join and reports it. It **never acts on an agent** unless the evidence is strong,
and today it never acts at all — every run says plainly that it sent nothing.

Every example below is real output from a three-host fleet.

---

## 1. A round finished at 03:44 and nobody knew

An agent completes a review round, prints its result, and stops. It is waiting on a CI check, or on
you. Nothing wakes it, and nothing tells you. The window is idle and looks exactly like a window that
is stuck.

```
$ octoshift waiting --host merritt --host fernie --host annies-mac-mini
ATTENTION 6 of 7 window(s) need you
NOT ACTED nothing was sent to any agent; 1 row(s) met the bar to act, 6 did not

WINDOW                       PR      STATE      CONF  FOR    DETAIL
merritt cp:1 pr4586          #4586   STALE      low   1h05m  record describes dca28a060, GitHub head is 06a462d10
fernie cp:19 pr4403          #4403~  CLOSED     med   4m     PR #4403 closed without merging
annies-mac-mini cp:1 pr4537  #4537   UNBLOCKED  high  <1m    waited on checks, and they have all concluded
```

The `FOR` column is the cost. On a real fleet it routinely shows PRs that have been dual-clean and
mergeable for over an hour, and windows stale for most of a day.

## 2. "Which machine is PR 1235 on?"

You are working with an agent on one PR and realise it interacts with another. Finding the second
agent means remembering which host it is on, attaching, and hunting through windows.

```
$ octoshift pr 4537
PR #4537  Add authenticated tsbindgen runtime bindings
  where     annies-mac-mini cp:1 pr4537   idle for 35m
  agent     round 17, reviews 0/2, rec wait
  github    open · mergeable · CI green · head bdc1338a7
  verdict   HOLDING (low) — reviews 0/2
```

Under six seconds across three hosts. Cheap because asking about **one** PR costs two GitHub calls,
not two per open PR, and the hosts are collected concurrently.

## 3. Two agents are fighting over one PR

It happens, and it is slow to notice. On one host they share a worktree and overwrite each other's
edits; across hosts they race to push. Neither agent can see the other.

```
$ octoshift pr 4448
PR #4448  Add full-screen annotated source explorer
  where     merritt cp:9 pr4448          working
  agent     published no state; identified by window name
  where     fernie cp:17 pr4448-blocked  blocked on a prompt
  agent     round 15, reviews 0/2, rec stop
  CONFLICT  2 windows claim this PR across hosts
  github    open · mergeable · CI green · head ff04746cf
```

**Both claims are kept, and ranked.** Rejecting the second loses work that is
really happening; treating them as equals gives two owners and a fight. So the
first to register owns the PR and the rest are *followed* — reported in the row
as a follower, and never spoken to. Driving two agents on one PR is worse than
either agent alone; forgetting the second is equally bad. This standing lives in
the report, not the window name: which window follows is decided across the whole
fleet, over state a per-window rename guard cannot revalidate at the moment it
writes a name (a concurrent retire or a new rival can flip it), so a name — read
at a glance and believed — is never made to assert it.

Ownership is only claimed as a fact when the tool watched both windows register.
Rivals rarely appear in the same moment — one agent starts, another joins later —
which is what makes registration order meaningful, and what makes it unavailable
to a run that started after both. In that case the report still names a likely
owner by seniority, marks it `(order inferred, not observed)`, and treats
**neither** window as actionable: guessing which agent began first and then
driving it is a coin toss whose losing side drives the agent that is not doing
the work.

Knowledge of who registered first is remembered between runs, and deliberately
forgotten when it stops being true. Every sweep records the tmux server's
identity per host; pane ids restart at `%0` when a server does, so after a reboot
the remembered ids name *different* windows. Rather than hand one window
another's registration, the tool drops that host's history and says so —
ownership falls back to inferred, and nothing is driven until it has watched the
registrations again.

Contested PRs also surface in the main view without asking, because neither
window looks wrong on its own — the contest is the finding.

## 3b. Is that second agent actually doing anything?

A window drawing a spinner looks busy. `window_activity` agrees with it, and so does a hash of the
whole screen — both are moved by a repainting footer.

Measured across one host over 45 seconds: four windows advanced their activity stamp and changed on
screen, and one of those had a **byte-identical body**. It was animating and emitting nothing.

So the tool hashes the capture with its footer removed and remembers that between runs. The `FOR`
column reports how long a window has produced no new content, rather than how long since anything
was drawn. Silence is only reported once there are two observations to compare — a first sighting
says nothing rather than a fabricated zero.

## 4. Window names that lie

The suffix convention is sound and its upkeep is not: an agent sets `-blocked` when it blocks and has
to remember to clear it later. Measured on one fleet, six windows carried `-blocked` and **three had
no prompt open**.

```
$ octoshift waiting --rename
RENAMED fernie cp:20 pr4553-blocked -> pr4553-merged
RENAMED merritt cp:9 pr4635-blocked -> pr4635
```

Renaming a window is not talking to an agent — it edits tmux metadata in your own view, cannot reach
an agent's input, and is idempotent, so a fleet already correct costs nothing. The tool owns the
suffix and rewrites it every sweep; the agent owns the `pr####` base and sets it once. A low-confidence
verdict is never published as a name: a row can say "probably", a name is read at a glance and
believed.

The rename plan is computed from a sweep that is already stale by the time it runs — the history lock
is released and GitHub is read first — so each rename is **guarded, atomically in one tmux client**, on
the window's live name, its live `@agent_state`, the live server generation, **and** its live
`window_activity`, all still equalling what the sweep saw. The activity stamp is what makes an
activity-derived suffix defensible: every suffix is read from a pane that had *stopped*, and tmux
advances `window_activity` on any output, so an idle pane that starts working during the GitHub read no
longer matches and the rename aborts rather than naming a resumed window `-ready`. What a per-window
guard *cannot* revalidate — fleet-global ownership, decided across other windows — is deliberately not a
suffix at all: the follower/owner standing stays in the row, never in the name.

## 5. An agent says it is ready and it is not

Agents do not follow a reporting contract reliably. Measured on one fleet in one day: states naming
another window's PR, blockers naming nothing openable, a PR listed as its own blocker, and a `2/2`
review count published by windows whose own round reports read *converging*.

So every verdict carries a confidence grade, and the reasons are shown:

```
fernie cp:3 pr4142   UNTRUSTWORTHY  low  17h  reported done, but the state contradicts itself
                                                (~ the record contradicts itself)
                                                [!] blocked=ci is not a citable issue or PR number
merritt cp:5 pr3967  HOLDING        low  22h  reviews 0/2  [!] rec=merge with reviews=0/2
```

Readiness is **two clean reviews plus a mergeable branch** — not green CI, which goes red for reasons
that have nothing to do with the change. A recommendation to merge is treated as a request, never as
evidence.

## 6. A window is holding a machine slot for work that already landed

```
$ octoshift pr 4553
PR #4553  Define agent behavior on session resume
  where     fernie cp:20 pr4553   idle for 11h37m
  github    merged 3d16h ago
  verdict   MERGED — the window is done
```

Three days merged, eleven hours idle, still occupying a window on a busy host.

## 7. An agent is waiting on something that already cleared

If an agent publishes what it is waiting for, the tool evaluates it against the head it named:

```
annies-mac-mini cp:1 pr4537  UNBLOCKED  high  waited on checks, and they have all concluded
```

`UNBLOCKED` means the agent's own stated condition is satisfied and the agent does not know yet. This
is the case the tool exists for.

## 8. `octoshift pr` when the answer is not a clean find

`octoshift pr` leads its first line with a token that matches its exit code, so a harness sees the
disposition before the details. A clean find keeps the `PR #…` lead; anything else fails (exit
`Unavailable`) and says why:

```
$ octoshift pr 4999
NOTFOUND PR #4999 — no window claims it and GitHub has no such PR

$ octoshift pr 4537   # merritt is a declared member and could not be reached
PARTIAL PR #4537 — fleet partly unreachable; a claim may be on a host not swept
  ...
  UNREACHABLE merritt: no server running on /tmp/tmux-1000/default
```

Every sweep covers the **declared fleet** — the local machine plus every remote ever attempted — not
merely the hosts this invocation named, so a member is never silently omitted. `--host` *adds* to that
set (and declares the host for next time); it does not narrow to it. That is what makes a complete view
reachable: once local and a host have each been swept, a later run reaches both together. A member the
sweep cannot reach surfaces as `PARTIAL`, and a member that should no longer count is retired (§9), so
the `NARROWED` disposition — fewer hosts than have been collected before — is now reserved for the
internal ownership guard rather than an ordinary run.

The `--json` form carries the same truth in its `viewComplete` and `unreachable` fields with the same
unavailable exit, so it stays a single valid JSON document — the token is never prepended to it.

## 9. Managing the fleet — `octoshift fleet`

The declared fleet grows on its own: attempting a target declares it, which is what keeps a first-time
failure from being forgotten. The manual steps are *retiring* a member that should no longer count — a
decommissioned box, a renamed alias, a typo — because otherwise it is attempted forever and every sweep
that cannot reach it stays partial; and *adding* one back, which is the only way to re-declare the local
machine once it has been retired.

```
$ octoshift fleet
FLEET 3 member(s)
  local
  host fernie
  host merritt

$ octoshift fleet retire --host merritt        # merritt was decommissioned
RETIRED host merritt

$ octoshift fleet retire --host typoo
UNKNOWN host typoo not in the declared fleet   # exit Usage — a typo cannot silently retire the wrong thing

$ octoshift fleet add --host banff --local     # declare a new box, and bring local back
ADDED host banff, local
```

`fleet retire`/`fleet add` take `--host <alias>` (repeatable) for remotes and `--local` for this machine;
both validate all targets before writing anything (an empty or option-shaped alias is rejected), and an
unknown *retire* target makes the whole command a non-success with nothing written. Retiring a member
also prunes the host, pane, and registration state kept under it, so no ownership can be derived from a
retired host's stale claim.

**Kind, preserved.** Every label distinguishes the local machine from an ssh alias literally *named*
`local`: the real machine is `local`, an alias is `host <alias>` (so an alias named `local` reads `host
local`), and `--json` carries the same distinction structurally as `{"kind":"local"}` versus
`{"kind":"host","host":"local"}` — a consumer can always tell whether it was `--local` or `--host local`.

**Empty on purpose is not the same as fresh.** A never-established fleet scans the local machine by
default; a fleet emptied by retiring its last member scans **nothing** until a target is added back —
retiring local must not be silently undone by re-bootstrapping it next sweep. `fleet list` says which it
is. A bare `waiting` over an emptied fleet leads with a new `EMPTY` token and succeeds rather than
sweeping local behind your back; `pr` still locates the PR on GitHub, reporting `no windows collected`
alongside whatever GitHub says, since a lookup does not depend on any window existing. The distinction is
persisted (an `initialized` flag), so it survives across runs.

`--json` emits the members, the added set, or the retired set as one document, each target a
kind-preserving identity. The fleet is credential-free and GitHub-unaware: a set of `tmux` collection
targets kept in the same machine-local history the sweeps use, mutated under the same transaction lock so
a retire or add cannot race a concurrent sweep.

---

## What it costs

REST with conditional requests throughout, so a re-run of an unchanged fleet is free:

| | calls | free (304) | budget consumed |
| --- | --- | --- | --- |
| cold sweep, 3 hosts, 18 windows | 78 | 0 | 78 |
| the same sweep again | 60 | 60 | **0** |

It uses `pulls/{n}` and `check-runs` rather than `gh pr list --json`, because the latter is GraphQL.
On a busy account the GraphQL budget is the one that runs out — measured at `0/5000` while REST sat
at `4967/5000`.

## What it will not do

- **It does not act on an agent.** Every run prints `NOT ACTED` with a count of
  rows that met the bar, including when that count is zero — otherwise "did
  nothing" and "saw nothing" look the same. (`--rename` writes window names,
  which is metadata in your own view, never input to an agent.)
- **It does not guess.** Missing evidence produces a low-confidence row saying
  what was missing, rather than a cheerful default.
- **It does not put credentials on your agent hosts.** Collection is a `tmux`
  dump over ssh; every GitHub call is made from the machine you run it on.
- **It does not require anything of agents.** Window names alone are enough to
  locate work. Agents that publish state get richer rows; those that do not are
  still found.

## Trying it

```sh
# this machine
octoshift waiting

# collected over ssh, nothing installed on the hosts — fernie and merritt join
# the declared fleet, so later runs reach them (and this machine) automatically
octoshift waiting --host fernie --host merritt

# include the quiet and healthy windows too
octoshift waiting --all

# correct stale window-name suffixes from what the tool observes
octoshift waiting --rename

# locate one PR and report what is happening to it
octoshift pr 4537

# show, extend, or prune the declared fleet
octoshift fleet
octoshift fleet add --host banff --local
octoshift fleet retire --host merritt

# same rows, for a dashboard
octoshift waiting --json
```

Hosts are ssh destinations, so `~/.ssh/config` handles jump hosts and control
sockets. Every sweep covers the whole declared fleet, so `--host` adds a target
rather than restricting to it; retire a member with `octoshift fleet retire` when
it is gone for good.

Agents that publish a `@agent_state` tmux window option get richer rows — see
[`AGENTS.md`](../AGENTS.md) — but the tool is useful before any agent does.
