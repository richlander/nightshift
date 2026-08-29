# Agent Instructions

## Start here

Nightshift is a **gate on GitHub interaction**: it observes what GitHub already knows
about pull requests — mergeability, CI, merges — caches it, and meters access to it so a
fleet of coding agents does not exhaust the API budget re-asking. It predicts nothing; it
observes and reports back pressure. This repository is its design and implementation.

Work directly on the task requested. This repository has no embedded roles or operating
skills; do not assume one or load role-specific guidance unless the user explicitly
provides it.

The product is two tools, each an AOT single binary with no `ProjectReference` to the
other:

- **Turnstile** (`turnstile`) — a credential-free coordination store: kv, leases, and an
  ETag watch over a local Unix socket. No GitHub, no network, no auth. This is the
  long-running state substrate the gate caches into.
- **Octoshift** (`octoshift`) — the GitHub-facing membrane. It runs an already-authenticated `gh`
  (the host's ambient `gh` credential storage, or an externally provisioned `GH_TOKEN`) and owns no
  credential material of its own — following Git's credential boundary. It observes PR/CI state over
  cheap REST + `If-None-Match` (leaving the
  exhausted GraphQL half alone). `octoshift waiting` joins agent-published tmux window state across
  hosts with what GitHub says about each PR to report which windows need a person; `octoshift pr`
  locates a single PR across the fleet; `octoshift fleet` manages the set of collection targets.
  Both `waiting` and `pr` resolve a claimed PR across the repos the fleet touches — pass `--repo`
  repeatably, or let the scope infer from the current directory's remote — and keep "no such PR in
  the searched repos" distinct from "GitHub could not be read" (#178).

Keep this file to repository-wide engineering rules. Subsystem design lives in
`docs/design/`; current code is authoritative when prose and implementation disagree.

## Where to read more

| Topic | Doc |
| --- | --- |
| Turnstile store (kv/lease/watch, socket) | `docs/design/turnstile.md` |
| What `octoshift waiting` is for, by scenario | `docs/waiting-scenarios.md` |
| The `octoshift waiting` state machine and its invariants | `docs/design/waiting-model.md` |
| TLA+ models (waiting / tmux windows) | `docs/model/README.md` |

## Repository-wide engineering constraints

- Target `net10.0`, `Nullable` enable, `ImplicitUsings` enable,
  `InvariantGlobalization`, and **`TreatWarningsAsErrors`** (see
  `Directory.Build.props`). A warning is a build break — fix it, don't suppress
  it blindly.
- Product paths must stay **NativeAOT-friendly**. Both product tools — `turnstile` and
  `octoshift` — set `PublishAot=true` in their csproj so each is published AOT (which implies
  `IsAotCompatible`, so the apps don't set that too). `System.CommandLine` is AOT-safe; don't
  introduce reflection-heavy or trim-unsafe dependencies on product paths.
- **The CLI contract is load-bearing. Preserve it.** A spawning harness branches on two
  signals from every command:
  - the **exit code as signal** (`ExitCode.*` — `Ok`, `Usage`, `Unavailable`, and the
    tool's siblings), and
  - the **human-readable stdout token on the first line** (`FLEET`, `ERROR`, and their
    siblings, plus the `waiting`/`pr` report lines).
  Never change a token's spelling, meaning, or exit code without updating every consumer.
  Adding a new token is fine; silently repurposing one is not.
- **Keep the credential boundary.** Turnstile is GitHub-unaware: it coordinates over a local
  socket with no network and no credentials. Octoshift is the *only* component that speaks to
  GitHub, and it does so through an already-authenticated `gh` it never owns credentials for —
  authority lives in the host-provided `gh`, not in octoshift. Don't move network or credentials
  onto the Turnstile path, and don't reintroduce octoshift-owned credential material or token
  minting. octoshift inherits the ambient environment untouched and never reads, copies, or unsets
  `GH_TOKEN`/`GITHUB_TOKEN`; the only environment it overrides on the `gh` path is gh's *non-auth*
  execution controls (`GH_TELEMETRY`, `GH_PAGER`, `GH_FORCE_TTY`), which govern gh's side effects
  and output, not credentials (see `GhProcessRunner`, #184).
- Reuse the existing command, state, and PR/repo-scope types before adding parallel
  abstractions.
- Keep failure visible. An unreachable socket, an ineligible action, or a GitHub error must
  surface as its token/exit code, never as success-shaped output.

## Publishing state for `octoshift waiting`

Some waits cannot be handed to a tool: a CI run, a required check, a mergeability recompute —
their answer lives on GitHub. You cannot block on those without polling, and re-asking about a
head that has not moved cannot tell you anything new; it only burns API budget. So **hand the
wait off, then stop** — and leave your state where `octoshift waiting` can read it.

`octoshift waiting` is **read-only**: it observes tmux windows and joins them with what GitHub
reports, and it **never renames or mutates a window** — it only reads and reports (see
[#179](https://github.com/richlander/nightshift/pull/179)). What it can say about your window is
exactly what you publish.

- **Do not poll GitHub for a state change.** A head that has not moved has no news.
- **Never end on a bare narration.** "Waiting for CI" as your last output is a stall with extra
  words: nothing is running to wake you, and the fact you paid a GitHub call to learn dies where
  no tool can read it.
- **Publish your state as a tmux window option instead**, and reset it whenever the state changes:

```sh
# Refuse to publish without a target of your own. This guard is the load-bearing part: with
# $TMUX_PANE empty, `set` lands on whichever window is current — and so does the matching `show`,
# so reading back through the same target confirms a write that went to somebody else's window.
[ -n "$TMUX_PANE" ] || { echo "no TMUX_PANE; do not publish"; exit 1; }

tmux set -w -t "$TMUX_PANE" @agent_state \
  "pr=4626 head=f4a8d1c84 reviews=2/2 blocked=4629 rec=wait"

# Verify by looking at every window, not by re-reading your own target: exactly one should carry
# your value. More than one means a write escaped.
tmux list-windows -a -F '#{window_name} #{@agent_state}' | grep -c 'pr=4626'
```

**Why an option and not your output.** This UI runs on the alternate screen, so tmux keeps no
scrollback for it; once your report scrolls past the top it is unrecoverable and the window is
anonymous. An option persists until you change it and cannot be garbled by line wrapping.

**Clear it when the window stops owning the work**, and re-publish rather than inherit after a
resume or reassignment. A window option outlives the session that set it, so a stale one keeps
advertising a decision nobody is standing behind:

```sh
tmux set -w -t "$TMUX_PANE" -u @agent_state
```

### The fields

Values carry no spaces. `octoshift waiting` reads these keys and ignores unknown ones.

| Field | Required | Meaning |
| --- | --- | --- |
| `pr` / `issue` | one of | PR number, or the issue number before a PR exists. |
| `head` | yes | The head sha this state describes. |
| `round` | optional | The review round just completed, if you track rounds. |
| `reviews` | when reviewed | `<clean>/<required>` — a review count published as evidence, not a gate the tool enforces. |
| `blocked` | when blocked | Issue or PR numbers. Omit when empty. |
| `waiting` | when waiting | A predicate: `check:<name>`, `checks`, `merge`, `review`. |
| `rec` | yes | `continue`, `wait`, `merge`, `approve`, or `stop`. |

**Identity before a PR exists.** A branch may be local before its PR is open, so there is no PR
and no GitHub-visible head yet. Publish `issue=` then, and switch to `pr=` once the PR is open. A
required field with no legal value is how you get invented ones.

**`head` makes the state falsifiable.** It describes one sha; if GitHub has moved past it, a
reader discards the claims rather than acting on assertions about code that no longer exists.

**`reviews` is the one fact no tool can observe.** GitHub knows CI and mergeability; it cannot see
whether a reviewer has signed off. Publishing the count is how that evidence reaches the board — it
is evidence a reader weighs, not a gate `octoshift` enforces.

**`blocked` and `waiting` split by who can act.** `blocked` takes issue or PR numbers only: things
a person can open and prioritise, and that the next agent hitting the same wall can find instead of
re-investigating. If a flake blocks you and no issue exists, file one and cite it. `waiting` takes a
predicate a reader evaluates against your `head`, for when nothing is wrong and nothing is openable —
a check that has not reported is not a defect and does not deserve an issue. `rec=wait` is coherent
when either is populated; `blocked=ci` satisfies neither and is the error the split exists to remove.

**`rec` is the disposition of the window.** `merge`, `approve` and `stop` each need a person before
anything moves; `wait` and `continue` do not.

### Name the window too

```sh
tmux rename-window -t "$TMUX_PANE" pr<number>
```

You name your **own** window; `octoshift waiting` never does. The name is the fallback identity when
no state is published, and it is a good one: set once, unaffected by the report scrolling away.
`octoshift waiting` reads both, joins them with GitHub, and reports which windows need a person — a
state that contradicts itself (`rec=merge` at `reviews=0/2`) is reported as a defect, never silently
repaired.

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
| Turnstile | `dotnet run --project tests/Turnstile.Tests` |
| Octoshift | `dotnet run --project tests/Octoshift.Tests` |

Filter with xUnit v3 args, e.g. `-- -class "Namespace.ClassName"` or
`-- -method "*Pattern*"`. Run the smallest test project that covers your change;
expand only when the change crosses a boundary.

Documentation-only changes need Markdown review, not a product build or tests.

## Git and worktrees

- `main` is protected. Work on a descriptive feature or `chore/` branch.
- Start every change from the latest `origin/main`.
- Never amend or rewrite history; create follow-up commits.
- **Re-read your guidance after re-pulling `main`.** When you refresh from `origin/main`,
  re-read `AGENTS.md` and task-relevant docs before continuing — guidance evolves, and a
  long-lived agent working from a stale copy keeps making the old mistake.
- Reviewers work in isolated **read-only** checkouts at an exact head; they never
  `git reset`, `git add`, or commit in a review tree.
- Don't mix unrelated changes into one commit or sweep another agent's
  working-tree changes into your work.
- Every change is reviewed before it merges to `main`.
