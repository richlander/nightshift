# Model

TLA+ specification of the memory and ownership rules in `octoshift waiting`.

## Running it

Needs a JVM and `tla2tools.jar`:

```sh
brew install openjdk
curl -sLO https://github.com/tlaplus/tlaplus/releases/latest/download/tla2tools.jar

java -cp tla2tools.jar tla2sany.SANY Waiting.tla                     # parse
java -cp tla2tools.jar tlc2.TLC -config Waiting.cfg -workers auto Waiting.tla
```

Current bounds — 3 windows, 2 PRs, 9 steps — check in a few seconds: 809,393 states
generated, 253,795 distinct, depth 10, zero violations.

## What is modelled, and what is not

**Modelled:** what happens to the state vector over time. Windows opening, closing and
switching PRs; the tmux server restarting; sweeps recording registrations; and the
ownership and confidence-of-ownership derived from those memories.

**Not modelled:** parsing `@agent_state`, tmux formats, REST semantics, ssh transport,
confidence grading, the verdict decision table. These turn bytes into the state vector.
A specification covering them would have to *assume* them correct, so it would model
the machine faithfully and miss the two worst defects found so far — a forged
collection frame and a record split by line wrapping — both parser bugs.

The split is deliberate: the checker owns memory and ownership, tests own everything
that produces the state vector. `InvariantTests` in the test project enumerates the
verdict and confidence product exhaustively for the same reason.

## Validating the spec itself

A specification that passes proves nothing until you have seen it fail. Each invariant
here has a mutation that breaks it:

| Mutation | Breaks |
| --- | --- |
| drop the epoch check in `Registered` (the real pane-id bug) | `NoCrossEpochMemory` |
| let two unwitnessed claimants be ordered | `NeverActOnUnwitnessedOrder` |
| make a sole claimant unownable | `SoleClaimantIsAlwaysOwner` |
| let any registered claimant own, not only the first | `AtMostOneOwner` |
| re-register every window on every sweep | `RegistrationStableStep` |
| sort unregistered windows first instead of last | `OwnerStableAcrossSweepStep` |

Every invariant and property in the config has an entry, which is the bar for calling
the run clean. A mutation must also be attributed to the *intended* property: an early
attempt at the last row was caught by `RegistrationStableStep` instead, which says
nothing about whether `OwnerStableAcrossSweepStep` checks anything. The mutation listed
leaves `regTime` untouched, so only the property under test can fire.

That exercise earned its keep twice. TLC refuted `RegistrationStableStep` on the first
run — the property, not the design, had forgotten that a window switching PRs re-registers.
And mutation found that `NeverActOnUnwitnessedOrder` was originally a **tautology**:
phrased as `OwnsClaim(w) => Observed(...)`, it restated a definition, since `OwnsClaim`
already requires `Observed`. TLC cannot report a vacuous invariant as a failure — it
passed happily with the guard it was meant to protect deleted.
