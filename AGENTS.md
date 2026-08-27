# Agent Instructions

## Start here

Nightshift is a **gate on GitHub interaction**: it observes what GitHub already knows
about pull requests — mergeability, CI, merges — caches it, and meters access to it so a
fleet of coding agents does not exhaust the API budget re-asking. It predicts nothing; it
observes and reports back pressure. This repository is its design and implementation.

Work directly on the task requested. This repository has no embedded roles or operating
skills; do not assume one or load role-specific guidance unless the user explicitly
provides it.

The product is three tools, each an AOT single binary with no `ProjectReference` to the
others:

- **Turnstile** (`turnstile`) — a credential-free coordination store: kv, leases, and an
  ETag watch over a local Unix socket. No GitHub, no network, no auth. This is the
  long-running state substrate the gate caches into.
- **Octoshift** (`octoshift`) — the single membrane between Turnstile and GitHub. It holds
  the GitHub App credentials agents never touch, observes PR/merge/CI state (preferring
  cheap REST + `If-None-Match` over the exhausted GraphQL half), and writes what it learns
  into Turnstile's cache. `octoshift waiting` joins agent-published tmux window state with
  GitHub to report which windows need a person.
- **Nightsky** (`nightsky`) — a read-only dashboard over the Turnstile keyspace. It renders
  and never mutates: it files no claim, writes no key, and holds no GitHub credentials.

Keep this file to repository-wide engineering rules. Subsystem design lives in
`docs/design/`; current code is authoritative when prose and implementation disagree.

## Where to read more

| Topic | Doc |
| --- | --- |
| Turnstile store (kv/lease/watch, socket) | `docs/design/turnstile.md` |
| Octoshift — the GitHub membrane | `docs/design/octoshift.md` |
| Nightsky — the read-only dashboard | `docs/design/nightsky.md` |
| What `octoshift waiting` is for, by scenario | `docs/waiting-scenarios.md` |
| The waiting state machine and its invariants | `docs/design/waiting-model.md` |
| TLA+ models (waiting / tmux windows) | `docs/model/README.md` |

## Repository-wide engineering constraints

- Target `net10.0`, `Nullable` enable, `ImplicitUsings` enable,
  `InvariantGlobalization`, and **`TreatWarningsAsErrors`** (see
  `Directory.Build.props`). A warning is a build break — fix it, don't suppress
  it blindly.
- Product paths must stay **NativeAOT-friendly**. Every product tool — `turnstile`,
  `octoshift`, and `nightsky` — sets `PublishAot=true` in its csproj so it is published AOT
  (which implies `IsAotCompatible`, so the apps don't set that too). Both
  `System.CommandLine` and `Markout` are AOT-safe; don't introduce reflection-heavy or
  trim-unsafe dependencies on product paths.
- **The CLI contract is load-bearing. Preserve it.** A spawning harness branches on two
  signals from every command:
  - the **exit code as signal** (`ExitCode.*` — `Ok`, `Usage`, `Unavailable`, and the
    tool's siblings), and
  - the **human-readable stdout token on the first line** (`LANDED`, `MERGED`, `ERROR`,
    and their siblings).
  Never change a token's spelling, meaning, or exit code without updating every consumer.
  Adding a new token is fine; silently repurposing one is not.
- **Keep the credential boundary.** Turnstile and Nightsky are GitHub-unaware: they coordinate
  and render over a local socket with no network and no credentials. Octoshift is the *only*
  component that holds GitHub authority and speaks to `gh`. Don't move network or credentials
  onto the Turnstile/Nightsky path.
- Reuse the existing command, state, and PR/order-ref types before adding parallel
  abstractions.
- Keep failure visible. An unreachable socket, an ineligible action, or a GitHub error must
  surface as its token/exit code, never as success-shaped output.

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
| Nightsky | `dotnet run --project tests/Nightsky.Tests` |

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
