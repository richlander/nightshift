---- MODULE Waiting ----
(* Model of the memory/ownership coupling in `octoshift waiting`, described in
   docs/design/waiting-model.md.

   The bug class: the tool decides whether it may speak to an agent partly from things
   it *remembered* across runs -- which window first claimed a PR, and which tmux
   server its pane ids belonged to. Remembering is not optional (registration order
   cannot be recovered after the fact), so the whole question is whether a remembered
   observation is ever presented as current fact after it has stopped being one.

   It is the mirror of the ZfsHoldTight bug class. There, the tool re-derived from
   today's policy what it should have remembered. Here, the tool remembered what it
   should have forgotten: pane ids restart at %0 with the tmux server, so after a
   restart the same id names a different window, and ownership derived from the old
   record would be wrong *and* labelled observed -- which is the label that gates
   action.

   Scope: windows, claims and sweeps are abstract. Deliberately NOT modeled -- parsing
   @agent_state, tmux formats, REST semantics, ssh transport, confidence grading, or
   the verdict decision table. Those turn bytes into the state vector; this models what
   happens to the state vector over time. A spec that included them would have to
   assume them correct, so it would faithfully model the machine and miss the two worst
   defects found so far (a forged collection frame, and a record split by line
   wrapping), both of which were parser bugs. Confidence and verdict are covered by
   InvariantTests, which enumerates their product exhaustively.

   `AgentActs` is exogenous and nondeterministic: agents open windows, close them, and
   switch PRs for reasons this tool neither controls nor predicts.

   Status: parsed cleanly with SANY and model-checked with TLC 2.19 against Waiting.cfg --
   no errors, 8,484,242 states generated, 1,539,916 distinct, depth 9, zero violations
   (~10s, 12 workers; the per-host epoch, the empty sweep, modelling an empty host's first
   window as a server start, separating the hosts a sweep ATTEMPTED from the subset that
   ANSWERED, growing persistent fleet membership from ATTEMPTED, the operator Retire that
   shrinks it and the operator Add that grows it back all widen the space from
   the earlier single-epoch model, while the server-start
   advance prunes some by failing more first looks closed; the distinct-state count is
   unchanged by Add, since an Add followed by a sweep reaches only configurations a sweep
   already reaches, but it adds transitions). Re-run TLC
   after any change here to regenerate the count
   and confirm zero violations. Validated by mutation:
   reintroducing the real pane-id-across-restart bug (dropping the epoch check in
   Registered) violates NoCrossEpochMemory; allowing two unwitnessed claimants to be
   ordered violates NeverActOnUnwitnessedOrder; making a sole claimant unownable violates
   SoleClaimantIsAlwaysOwner; growing knownHosts from collected instead of attempted -- the
   round-9 first-time-failed-host laundering, where a host that fails on its first attempt is
   forgotten and a later omission reads complete -- violates CompletenessCoversEveryAttemptedHost;
   a Retire that removes a host from knownHosts but leaves it in lastCollected with viewComplete
   still TRUE -- so a retired host's stale sole claim stays actionable -- violates
   NoOwnerFromRetiredHost; and dropping the regWitnessed clause from Observed -- or
   recomputing witness from the current sweep's coverage, the fleet-expansion laundering
   where the third sweep of a full fleet promotes a claim first recorded under a narrow
   view -- violates NoOwnerFromUnwitnessedRegistration, and doing the same recomputation
   while the claim continues violates the RegWitnessedStableStep temporal property; and an Add
   that grows knownHosts without re-stamping a collected window's regFleet leaves it stale, so
   the next otherwise-unchanged sweep flips Owner and violates OwnerStableAcrossSweepStep. The
   corresponding code is covered
   by InvariantTests, ModelCorrespondenceTests and WaitingScanTests against the real
   implementation, not just this model. *)

EXTENDS Integers, FiniteSets

CONSTANTS
    Windows,    \* abstract window identities, standing in for tmux pane ids
    Hosts,      \* the machines those windows live on
    PRs,        \* abstract PR numbers
    MaxTime     \* bound the model so the state space is finite

ASSUME /\ MaxTime \in Nat
       /\ Hosts \subseteq (Nat \ {0})
       /\ Hosts # {}
       /\ Windows \subseteq (Nat \ {0})
       /\ PRs \subseteq (Nat \ {0})
       /\ Windows # {}
       /\ PRs # {}

\* Windows and PRs are positive integers so that ties can be broken on a fixed order;
\* 0 is the "claiming nothing" / "no window" sentinel, and -1 means never recorded.
\* Which host a window is on. Fixed: a tmux pane does not migrate between machines.
HostOf(w) == 1 + (w % Cardinality(Hosts))

NoPr    == 0
NoWindow == 0
NoTime  == -1

VARIABLES
    now,         \* logical clock, advanced by every action
    claims,      \* window -> PR or NoPr: what each window claims RIGHT NOW
    live,        \* set of windows that currently exist
    epoch,       \* host -> the tmux server generation on that host; advances on its restart AND when its first window opens (a server start); 0 means no server has run
    regEpoch,    \* host -> the epoch the registrations remembered for that host belong to
    regTime,     \* window -> time it was first SEEN claiming its current PR
    regPr,       \* window -> the PR it was seen claiming
    regFleet,    \* window -> the set of hosts known when its registration was made
    viewComplete, \* DERIVED by each sweep -- see Sweep
    knownHosts,   \* persistent fleet membership: every host ever ATTEMPTED, answering or not
    lastCollected, \* hosts the most recent sweep actually looked at
    regWitnessed, \* window -> was the current registration witnessed (recorded under observation)
    everAttempted \* GHOST: every host ever attempted, kept independently of knownHosts so a
                  \* mutation that grew membership from collected instead of attempted is refutable

vars == << now, claims, live, epoch, regEpoch, regTime, regPr, regFleet,
            viewComplete, knownHosts, lastCollected, regWitnessed, everAttempted >>

TypeOK ==
    /\ now \in 0..MaxTime
    /\ claims \in [Windows -> PRs \union {NoPr}]
    /\ live \subseteq Windows
    /\ epoch \in [Hosts -> Nat]
    /\ regEpoch \in [Hosts -> Nat]
    /\ regTime \in [Windows -> Int]
    /\ regPr \in [Windows -> PRs \union {NoPr}]
    /\ regFleet \in [Windows -> SUBSET Hosts]
    /\ viewComplete \in BOOLEAN
    /\ knownHosts \subseteq Hosts
    /\ lastCollected \subseteq Hosts
    /\ regWitnessed \in [Windows -> BOOLEAN]
    /\ everAttempted \subseteq Hosts

(* ---- What the tool derives from what it remembers ---------------------------------

   Ownership among the windows claiming one PR. A window's registration counts only if
   it was recorded under the CURRENT epoch and for the PR the window still claims. *)

\* Only windows the last sweep actually collected. The tool cannot count a window it
\* never looked at, and an earlier version of this spec did -- which made the model
\* strictly more informed than the implementation and let it "check" ordering between
\* windows the real tool would never have seen together.
Claimants(p) == { w \in live : claims[w] = p /\ HostOf(w) \in lastCollected }

\* A registration counts only if it was made under the current tmux server, for the PR
\* the window still claims, AND against the same fleet the tool knows about now.
\*
\* That last condition is what stops a narrow sweep manufacturing an order. Registering
\* one claimant while its rival's host went uncollected makes the swept one look like the
\* earlier arrival, when all that happened is that it was LOOKED AT first. TLC found this
\* by exploring sweeps over host subsets: two unwitnessed claimants, a sweep covering one
\* host, and ownership flipped. Requiring the fleet to match means an order established
\* before the tool knew about a host is not an order over the windows on it.
Registered(w) ==
    IF /\ regEpoch[HostOf(w)] = epoch[HostOf(w)]
       /\ regPr[w] = claims[w]
       /\ regTime[w] # NoTime
       /\ regFleet[w] = knownHosts
    THEN regTime[w]
    ELSE NoTime

\* A window with no registration on a host swept before under this epoch must have
\* appeared since that sweep -- so it is newer than everything already recorded.
Placement(w) == IF Registered(w) # NoTime THEN Registered(w) ELSE MaxTime + 1

\* The order is a FACT only when every claimant's registration was witnessed, every one is
\* recorded, and the recorded times are distinct.
\*
\* Witnessed means the registration was made while the tool was already watching the
\* window's host under a complete view. This is read from regWitnessed -- persisted with
\* the registration and preserved for as long as the same claim continues -- not
\* recomputed from this sweep's coverage. That distinction is the whole of the
\* fleet-expansion fix: a claim first recorded under a narrow view has regWitnessed FALSE
\* and keeps it across every later sweep, so collecting the whole fleet on a subsequent
\* sweep cannot turn its first look into a witnessed appearance and promote it. Only a
\* release and a witnessed re-registration set it TRUE. An earlier version consulted the
\* current sweep's host set here, and the three-sweep counterexample -- W recorded under a
\* narrow view, its rival's host added, then the same full fleet swept -- promoted the
\* order on the third sweep, exactly the laundering this closes.
Observed(p) ==
    LET C == Claimants(p)
        recorded == { w \in C : Registered(w) # NoTime }
        unrecorded == C \ recorded
    IN /\ \A a, b \in recorded : a # b => Registered(a) # Registered(b)
       /\ unrecorded = {}
       /\ \A w \in C : regWitnessed[w]

\* Deterministic pick of the earliest claimant, ties broken on a fixed key, so an owner
\* never changes identity merely because a set was enumerated differently.
Owner(p) ==
    LET C == Claimants(p)
    IN IF C = {} THEN NoWindow
       ELSE CHOOSE w \in C :
              \A o \in C \ {w} :
                 \/ Placement(w) < Placement(o)
                 \/ (Placement(w) = Placement(o) /\ w < o)

\* The one output with consequences: this window may be spoken to about this PR.
\* A sweep that could not reach every host does not know that a window is the only
\* claimant of its PR -- the other one may be on the host that did not answer. Since a
\* sole claim is the one shape that is always actionable, a partial view is exactly the
\* condition under which the tool would drive the wrong agent.
OwnsClaim(w) ==
    /\ w \in live
    /\ claims[w] # NoPr
    /\ viewComplete
    /\ LET p == claims[w] IN
         \/ Claimants(p) = {w}                     \* uncontested
         \/ (Owner(p) = w /\ Observed(p))          \* contested, and the order is known

(* ---- Actions ---- *)

\* knownHosts = {} is the UNINITIALIZED fleet -- nothing declared yet. The model's hosts are symmetric, so
\* it does not distinguish "never established" from "established then emptied by retirement": that
\* distinction matters only to the implementation's local-machine bootstrap (a bare sweep defaults to the
\* local machine while uninitialized, and must NOT re-add it once the fleet has been emptied on purpose),
\* which is a code concern -- the persisted `initialized` flag -- outside this abstraction. What the model
\* does carry is the safety that survives either reading of an empty fleet: a window on a host outside
\* knownHosts can never own (NoOwnerFromRetiredHost), so emptying the fleet, by retirement or by never
\* declaring it, strands no actionable ownership. Add and Retire are the deliberate operator acts that grow
\* and shrink membership; Sweep grows it as a side effect of attempting a target.
Init ==
    /\ now = 0
    /\ claims = [w \in Windows |-> NoPr]
    /\ live = {}
    /\ epoch = [h \in Hosts |-> 0]
    /\ regEpoch = [h \in Hosts |-> 0]
    /\ regTime = [w \in Windows |-> NoTime]
    /\ regPr = [w \in Windows |-> NoPr]
    /\ regFleet = [w \in Windows |-> {}]
    /\ viewComplete = FALSE
    /\ knownHosts = {}
    /\ lastCollected = {}
    /\ regWitnessed = [w \in Windows |-> FALSE]
    /\ everAttempted = {}

\* Agents open windows, close them, and switch PRs on their own schedule. Exogenous:
\* the tool observes this, it does not cause it.
\*
\* Opening the FIRST live window on a host is a server START. Production records no epoch
\* for a host observed empty (RecordSweptEmpty stores Epoch = null: no tmux server was
\* running, so no pane-id generation was seen), so the first window to appear there begins
\* under a new, unknown generation -- exactly like a restart from the tool's point of view.
\* Modelling that as an epoch advance is what stops an empty sweep from laundering a first
\* look into a witnessed order: the host's remembered epoch (0 from Init, or whatever an
\* earlier generation left) no longer matches the live one, so the first window's claim is
\* unregistered and unwitnessed until a continuous sweep records it under the new epoch.
\* Opening a window on a host that already has one leaves the generation unchanged -- the
\* server is already up -- and closing the last window does not advance the epoch, so the
\* advance happens on the reopen, when the next server starts.
AgentActs ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ \E w \in Windows :
         \/ /\ w \notin live                        \* a window opens on some PR
            /\ \E p \in PRs :
                 /\ live' = live \union {w}
                 /\ claims' = [claims EXCEPT ![w] = p]
                 /\ epoch' = IF \E o \in live : HostOf(o) = HostOf(w)
                             THEN epoch                                              \* server already up
                             ELSE [epoch EXCEPT ![HostOf(w)] = epoch[HostOf(w)] + 1] \* first window: server start
         \/ /\ w \in live                           \* a window closes
            /\ live' = live \ {w}
            /\ claims' = [claims EXCEPT ![w] = NoPr]
            /\ UNCHANGED epoch
         \/ /\ w \in live                           \* a window switches PR
            /\ \E p \in PRs :
                 /\ p # claims[w]
                 /\ live' = live
                 /\ claims' = [claims EXCEPT ![w] = p]
            /\ UNCHANGED epoch
    /\ UNCHANGED << regEpoch, regTime, regPr, regFleet, viewComplete, knownHosts, lastCollected, regWitnessed, everAttempted >>

\* One tmux server restarts: pane ids on THAT host restart, so its ids may now name
\* different windows, while every other host is untouched. Restarts are per host because
\* the fleet is many independent servers -- one machine rebooting says nothing about the
\* others -- so only the chosen host's epoch advances and only its windows are replaced.
ServerRestarts ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ \E h \in Hosts :
         /\ epoch' = [epoch EXCEPT ![h] = epoch[h] + 1]
         /\ live' = { w \in live : HostOf(w) # h }
         /\ claims' = [w \in Windows |-> IF HostOf(w) = h THEN NoPr ELSE claims[w]]
    \* regWitnessed is NOT touched here, and that is deliberate. The production tool cannot
    \* alter persisted provenance for a host it did not collect: a restart is a fact about
    \* the server, learned only when a later sweep reaches the host and AdoptEpoch sees the
    \* epoch mismatch, which is what clears or freshens the witness on disk. Clearing it
    \* here would be a change to a host that may be absent from lastCollected, which
    \* NoPhantomDepartureStep forbids -- and it is unnecessary, because Registered already
    \* fails the instant regEpoch[HostOf(w)] # epoch[HostOf(w)], so a stale witness confers
    \* nothing until the next collecting Sweep rewrites it under the new epoch.
    /\ UNCHANGED << regEpoch, regTime, regPr, regFleet, viewComplete, knownHosts, lastCollected, regWitnessed, everAttempted >>

\* A sweep over some set of hosts. The set is nondeterministic because it is chosen by
\* whoever ran the tool -- and because a host may fail to answer. Those two are the same
\* thing from in here: windows on an uncollected host are unseen either way.
\*
\* viewComplete is DERIVED, and that is the reason hosts are modelled at all. An earlier
\* version of this spec had a PartialSweep action that simply asserted
\* viewComplete' = FALSE. That checks what FOLLOWS from a partial view and can never
\* check when a view IS partial -- so it held while the implementation derived partiality
\* from failures alone and missed the case of a run merely given fewer hosts: no
\* failures, the view reads complete, and a window that is a follower on the full fleet
\* becomes a sole claimant and is actionable. The model assumed exactly the fact that
\* was wrong.
Sweep ==
    /\ now < MaxTime
    /\ now' = now + 1
    \* Two host sets, not one. The run REQUESTED a set of hosts (attempted) and some subset
    \* of those ANSWERED (collected); a requested host that failed is in attempted \ collected.
    \* Registrations, continuity and epochs key on what ANSWERED; persistent fleet membership
    \* (knownHosts) keys on what was ATTEMPTED. That split is the round-9 correction: a host
    \* attempted for the first time and failing has no epoch or continuity, but it is still fleet
    \* membership, so a later sweep that omits it can tell its view narrowed. Modelling attempted at
    \* all is the round-8 correction: deriving completeness from knownHosts \subseteq collected ALONE let a
    \* freshly requested host FAIL (attempted nonempty, knownHosts still {}) read as a complete
    \* view -- {} \subseteq collected holds vacuously -- while production passes allHostsAnswered
    \* = FALSE. Both sets range over EVERY subset (the empty set included): an empty collected is
    \* a total failure that records lastCollected = {}, so the next return of any host is a gap
    \* that resets its continuity and witness, exactly as a run that collected nothing leaves it.
    /\ \E attempted \in SUBSET Hosts :
       \E collected \in SUBSET attempted :
         \* Completeness on TWO counts, exactly as production's `allHostsAnswered && omitted.Length == 0`.
         \* First, every host the run ATTEMPTED must have answered (attempted \subseteq collected, i.e.
         \* collected = attempted since collected \subseteq attempted): a requested host that failed is a
         \* hole in the view, even one never seen before. Second, the run must cover everything it has
         \* ALREADY attempted (knownHosts \subseteq collected, and knownHosts now accumulates attempted) --
         \* a run over fewer hosts than it has seen is looking at less of the fleet than it knows, and
         \* cannot tell that from its arguments alone (a host it was not told about is indistinguishable
         \* from one that does not exist), only from this memory. The first conjunct is the round-8
         \* correction: deriving completeness from known coverage ALONE let a freshly attempted host FAIL
         \* and still read complete -- attempted = {h}, collected = {}, knownHosts = {}, so {} \subseteq {}
         \* holds vacuously while production reports allHostsAnswered = FALSE. The second, now over an
         \* attempted-grown knownHosts, is the round-9 correction: a host that failed on its FIRST attempt
         \* (never collected, so absent from a collected-grown knownHosts) would be forgotten, and a later
         \* A-only sweep would read complete and own a sole claim while a rival may run on it. Derived ONCE
         \* and threaded into both the view flag and the witness of a fresh registration, exactly as
         \* production threads one `viewComplete` into both: a run whose sibling host failed must not
         \* witness a claim on the host that did answer.
         LET viewIsComplete == (attempted \subseteq collected) /\ (knownHosts \subseteq collected)
         IN
         \* Registrations are renewed or dropped only by a sweep that looked at the host.
         \* A window on an uncollected host is unseen, not gone -- forgetting it would
         \* manufacture a departure and re-register it as new on the next full sweep.
         \* A registration is preserved only across CONTINUOUS observation on an UNCHANGED
         \* server: the host's remembered epoch must still match its live epoch
         \* (regEpoch[HostOf(w)] = epoch[HostOf(w)], so no restart since the last sweep of
         \* it) AND the host must have been in the PREVIOUS sweep (HostOf(w) \in
         \* lastCollected). A host collected now but restarted, or absent last sweep, is a
         \* gap -- its window is freshened (regTime = now) even when the claim is unchanged.
         /\ regTime' = [w \in Windows |->
                          IF HostOf(w) \notin collected THEN regTime[w]
                          ELSE IF w \in live /\ claims[w] # NoPr
                               THEN IF regEpoch[HostOf(w)] = epoch[HostOf(w)] /\ regPr[w] = claims[w] /\ regTime[w] # NoTime /\ HostOf(w) \in lastCollected
                                    THEN regTime[w]
                                    ELSE now
                               ELSE NoTime]
         /\ regPr' = [w \in Windows |->
                        IF HostOf(w) \notin collected THEN regPr[w]
                        ELSE IF w \in live THEN claims[w] ELSE NoPr]
         \* The fleet a registration was made against. This is the KNOWN fleet -- the persistent
         \* membership below, which grows with ATTEMPTED hosts -- so a registration made before a host
         \* was ever attempted is not an order over the windows on it. It tracks knownHosts' exactly.
         /\ regFleet' = [w \in Windows |->
                           IF HostOf(w) \notin collected THEN regFleet[w]
                           ELSE knownHosts \union attempted]
         \* The view flag: complete exactly when every attempted host answered AND the run covers
         \* everything already collected (see viewIsComplete above). Without the attempted-answered half,
         \* a never-before-known host that failed would read complete -- the bug this round closes.
         /\ viewComplete' = viewIsComplete
         \* Provenance, persisted with the registration. A window unseen this sweep keeps
         \* whatever it had. One that keeps the same claim ACROSS CONTINUOUS OBSERVATION on
         \* an UNCHANGED server keeps its witness -- so a first look stays a first look no
         \* matter how many later sweeps see it. A fresh registration is witnessed only when
         \* the host's remembered epoch still matches its live one (no restart since the
         \* last sweep of it), its host was in the PREVIOUS sweep (no gap to have released
         \* across), AND this run's view is complete; otherwise it is a first look, recorded
         \* FALSE. Dropping a claim, a restart, or a gap on the host clears it. This is what
         \* stops the three-sweep promotion AND the gap/restart-return promotion.
         /\ regWitnessed' = [w \in Windows |->
                               IF HostOf(w) \notin collected THEN regWitnessed[w]
                               ELSE IF w \in live /\ claims[w] # NoPr
                                    THEN IF regEpoch[HostOf(w)] = epoch[HostOf(w)] /\ regPr[w] = claims[w] /\ regTime[w] # NoTime /\ HostOf(w) \in lastCollected
                                         THEN regWitnessed[w]
                                         ELSE (regEpoch[HostOf(w)] = epoch[HostOf(w)] /\ HostOf(w) \in lastCollected /\ viewIsComplete)
                                    ELSE FALSE]
         \* Each collected host adopts its live epoch, so a restart it has not yet been
         \* swept under is noticed the next time it is; an uncollected host keeps whatever
         \* it remembered, because the tool cannot learn a restart on a host it did not look
         \* at.
         /\ regEpoch' = [h \in Hosts |-> IF h \in collected THEN epoch[h] ELSE regEpoch[h]]
         \* Persistent fleet membership grows with ATTEMPTED, not collected -- the round-9 correction.
         \* A host attempted for the first time and FAILING before it ever collected has no epoch and no
         \* continuity (those are keyed on collected, above), but it IS fleet membership: growing knownHosts
         \* from collected alone would forget it, and a later sweep that omits it would read knownHosts
         \* \subseteq collected as satisfied -- a complete view -- and own a sole claim while a rival may run
         \* on the host that never answered. Growing it from attempted is what makes that later omission read
         \* as narrowed. everAttempted mirrors this independently so the correspondence property below can
         \* refute a mutation that reverts this union to collected.
         /\ knownHosts' = knownHosts \union attempted
         /\ everAttempted' = everAttempted \union attempted
         /\ lastCollected' = collected
    /\ UNCHANGED << claims, live, epoch >>

\* An operator retires a host from the declared fleet. This is the ONLY action that shrinks
\* persistent membership -- ordinary collection never forgets an attempted target (that is the
\* round-9 rule) -- so a decommissioned, renamed, or mistyped host that would otherwise be
\* attempted forever, keeping every later sweep narrowed, can be removed on purpose. It is a
\* deliberate act, not something a sweep does: only a host already IN knownHosts can be retired,
\* which is how the implementation reports an unknown target as a non-success rather than a silent
\* no-op.
\*
\* Retiring a host removes it from knownHosts, everAttempted and the last sweep's coverage, and
\* clears the per-host registration state kept under it -- exactly what the implementation does
\* when it drops the host from its maps and prunes its pane entries. Two things follow, and both
\* are what NoOwnerFromRetiredHost turns on. First, a window on the retired host is no longer in
\* lastCollected, so it is not a Claimant and cannot own -- ownership can never remain actionable
\* from a retired host's stale claim. Second, changing the fleet invalidates the last sweep's
\* completeness (an ordering established when the fleet was larger is not an ordering over the
\* fleet now), so viewComplete drops to FALSE until a fresh complete sweep re-earns it, exactly as
\* a Sweep would recompute it. everAttempted shrinks in lockstep with knownHosts, so a retired
\* host does not leave CompletenessCoversEveryAttemptedHost demanding coverage of a host no longer
\* in the fleet.
Retire ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ \E h \in knownHosts :
         /\ knownHosts' = knownHosts \ {h}
         /\ everAttempted' = everAttempted \ {h}
         /\ lastCollected' = lastCollected \ {h}
         /\ regTime' = [w \in Windows |-> IF HostOf(w) = h THEN NoTime ELSE regTime[w]]
         /\ regPr' = [w \in Windows |-> IF HostOf(w) = h THEN NoPr ELSE regPr[w]]
         \* The retired host's windows lose their registration fleet; a surviving window that was under
         \* observation has its registration re-understood against the now-smaller fleet, exactly as the
         \* implementation keeps a host it did not retire fully valid (it has no per-registration fleet
         \* snapshot to invalidate). Without this, a surviving window's regFleet would stay pinned to the
         \* pre-retirement fleet, its registration would read stale until the next sweep re-stamped it, and
         \* that sweep -- over an otherwise unchanged fleet -- would flip Owner, an artifact the real tool
         \* does not have. A surviving but uncollected window keeps its fleet, since the tool did not look
         \* at it, and it is not a Claimant anyway.
         /\ regFleet' = [w \in Windows |->
                          IF HostOf(w) = h THEN {}
                          ELSE IF HostOf(w) \in lastCollected \ {h} THEN knownHosts \ {h}
                          ELSE regFleet[w]]
         /\ regWitnessed' = [w \in Windows |-> IF HostOf(w) = h THEN FALSE ELSE regWitnessed[w]]
         /\ regEpoch' = [g \in Hosts |-> IF g = h THEN 0 ELSE regEpoch[g]]
         /\ viewComplete' = FALSE
    /\ UNCHANGED << claims, live, epoch >>

\* An operator adds a host to the declared fleet. The deliberate counterpart to Retire, and -- besides a
\* bare sweep's bootstrap of the local machine, which the implementation does once and never repeats after
\* a retirement -- the only way to (re-)declare a target. In the implementation this is `octoshift fleet
\* add`, whose reason for existing is exactly that ordinary collection no longer re-bootstraps a member
\* retired on purpose (see the initialized/explicitly-empty distinction in PaneHistory): bringing local, or
\* any host, back is now an act rather than a side effect.
\*
\* It grows persistent membership without collecting anything. The added host enters knownHosts (and the
\* everAttempted ghost) with no epoch, no continuity and no pane of its own -- exactly the shape a
\* never-yet-collected attempted target has -- so a window on it cannot own until a complete sweep has
\* actually reached it, and a later sweep that omits it still reads as narrowed. Only a host not already a
\* member is added; re-adding an existing member is an idempotent no-op the implementation allows and which
\* would change nothing here.
\*
\* Growing the fleet invalidates the last sweep's completeness -- an ordering established when the fleet was
\* smaller is not an ordering over the fleet now -- so viewComplete drops to FALSE until a fresh complete
\* sweep re-earns it, exactly as Retire and a Sweep recompute it. A surviving window that was under
\* observation has its registration fleet re-understood against the now-larger fleet, exactly as Retire
\* re-stamps it against the now-smaller one: without this a collected window's regFleet would stay pinned to
\* the pre-addition fleet, its registration would read stale until the next sweep re-stamped it, and that
\* sweep -- over an otherwise unchanged fleet -- would flip Owner, an artifact the real tool (whose ranking
\* is a stable function of registration time and witness, not of a fleet snapshot) does not have. A window
\* the tool did not look at last sweep keeps its fleet, since the tool did not observe it, and it is not a
\* Claimant anyway. The re-stamp does NOT witness anything: regWitnessed is untouched and viewComplete is
\* FALSE, so nothing on the added, uncollected host becomes actionable until a complete sweep reaches it.
\* Because knownHosts changes, RegistrationStableStep, RegWitnessedStableStep and OwnerStableAcrossSweepStep
\* all exclude this step just as they exclude a Retire; NoPhantomDepartureStep permits the regFleet re-stamp
\* because every re-stamped window's host stays in lastCollected' (= lastCollected).
Add ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ \E h \in Hosts \ knownHosts :
         /\ knownHosts' = knownHosts \union {h}
         /\ everAttempted' = everAttempted \union {h}
         /\ regFleet' = [w \in Windows |->
                          IF HostOf(w) \in lastCollected THEN knownHosts \union {h}
                          ELSE regFleet[w]]
         /\ viewComplete' = FALSE
    /\ UNCHANGED << claims, live, epoch, regEpoch, regTime, regPr, lastCollected, regWitnessed >>

Next == AgentActs \/ ServerRestarts \/ Sweep \/ Retire \/ Add

Spec == Init /\ [][Next]_vars /\ WF_vars(Sweep)

(* ---- Safety ---- *)

\* Invariant 8: two windows can never both own one PR.
AtMostOneOwner ==
    \A p \in PRs : Cardinality({ w \in Claimants(p) : OwnsClaim(w) }) <= 1

\* Invariant 2: a contested PR is only ever driven when the tool WITNESSED the order.
\* This is the property the whole epoch mechanism exists to preserve.
\*
\* Stated over the underlying facts -- how many claimants have no registration -- and
\* deliberately NOT as "OwnsClaim(w) => Observed(...)". That phrasing is a tautology,
\* because OwnsClaim already requires Observed, so it restates a definition instead of
\* checking it. TLC cannot report a vacuous invariant as a failure: it passed happily
\* both before and after the guard it was meant to protect was deleted, and only a
\* deliberate mutation of the spec exposed that it was testing nothing.
NeverActOnUnwitnessedOrder ==
    \A w \in live :
        LET C == Claimants(claims[w])
            unwitnessed == { c \in C : Registered(c) = NoTime }
        IN (claims[w] # NoPr /\ Cardinality(C) > 1 /\ Cardinality(unwitnessed) > 1)
             => ~OwnsClaim(w)

\* Invariant 10: a registration recorded under a previous tmux server never counts.
\* Without this, a restarted server hands a new window a departed one's place in the
\* queue -- and labels the result observed, which is what gates action. Per host now: a
\* window whose host's remembered epoch has fallen behind that host's live epoch (its
\* server restarted since the last sweep of it) is not registered, whatever other hosts
\* did.
NoCrossEpochMemory ==
    \A w \in live : regEpoch[HostOf(w)] # epoch[HostOf(w)] => Registered(w) = NoTime

\* Anti-degenerate. Every property above is satisfied by a tool that never acts at all,
\* so one of them has to require that it DOES. A window that is the only claimant of
\* its PR must always be actionable: no amount of caution elsewhere may starve the
\* ordinary case. (Modelled on NoGenuineGapGoesUnnoticed in ZfsHoldTight, which rules
\* out a "fix" that satisfies policy-independence by never reporting anything.)
\* Conditioned on a complete view, and that condition is not a weakening. TLC refuted
\* the unconditioned form in two steps once partial sweeps existed: a sweep that could not
\* reach every host does not know a claim is sole, so "sole claimants are always
\* actionable" and "a partial view owns nothing" are in direct conflict. The second wins
\* -- it is the safety rule -- and this one keeps its job of ruling out a tool that
\* never acts, now restricted to the runs where acting is permitted at all.
SoleClaimantIsAlwaysOwner ==
    viewComplete =>
        \A w \in live : (claims[w] # NoPr /\ Claimants(claims[w]) = {w}) => OwnsClaim(w)

\* A partial sweep owns nothing, however clean the part it could see looks.
NoOwnerWhileViewIncomplete ==
    ~viewComplete => \A w \in live : ~OwnsClaim(w)

\* A contested claim is owned only when every claimant's registration was witnessed. This
\* is the fleet-expansion property: a claim first recorded under a narrow view stays
\* untrusted across every later sweep, so collecting the whole fleet cannot promote its
\* first look into an owned order. Stated over the persisted fact -- every claimant's
\* regWitnessed -- rather than as "OwnsClaim => Observed", which is a tautology TLC cannot
\* refute; a deliberate mutation dropping the regWitnessed clause from Observed, or
\* recomputing witness from the current sweep's coverage, violates this.
NoOwnerFromUnwitnessedRegistration ==
    \A w \in live :
        (claims[w] # NoPr /\ Cardinality(Claimants(claims[w])) > 1 /\ OwnsClaim(w))
          => \A c \in Claimants(claims[w]) : regWitnessed[c]

\* Round 9: a complete view must cover every host EVER ATTEMPTED, not merely every host ever
\* collected. This is the persistent-fleet-membership property. A host attempted for the first time
\* and failing before it ever collected has no epoch and no continuity, so a knownHosts grown from
\* collected alone forgets it -- and a later sweep that omits it reads knownHosts \subseteq collected
\* as satisfied, a complete view, and owns a sole claim while a rival may still run on the host that
\* never answered.
\*
\* Stated over everAttempted, the ghost that accumulates attempted independently of the knownHosts
\* implementation, rather than over knownHosts itself: with the fix knownHosts = everAttempted, so
\* "viewComplete => knownHosts \subseteq lastCollected" is satisfied by the buggy spec too (a
\* collected-grown knownHosts is trivially \subseteq the hosts just collected) and refutes nothing.
\* Against everAttempted the mutation is caught: revert knownHosts' to knownHosts \union collected and
\* the two-sweep counterexample -- attempt A and a never-seen B, B fails, then sweep A alone -- makes
\* viewComplete TRUE while everAttempted = {A,B} \not\subseteq {A} = lastCollected.
CompletenessCoversEveryAttemptedHost ==
    viewComplete => everAttempted \subseteq lastCollected

\* Round 10: ownership can never remain actionable from a retired host's stale claim. Once a host
\* leaves the declared fleet -- retired on purpose, or simply never attempted yet -- no window on it
\* may own a PR. This is the manageable-fleet safety property: retirement must not be a way to strand
\* an actionable ownership on a host the tool is no longer watching.
\*
\* It holds in every reachable state, not only after a Retire: a window on a host outside knownHosts
\* is also outside lastCollected (lastCollected \subseteq the hosts a sweep collected, and a collected
\* host is attempted, so it is in knownHosts), hence it is not a Claimant and cannot own. Stated over
\* the underlying fact -- HostOf(w) \notin knownHosts -- rather than as a consequence of Retire, so it
\* is not a restatement of the action. A deliberate mutation of Retire that removed a host from
\* knownHosts while leaving it in lastCollected and viewComplete TRUE would make its stale sole claim
\* actionable and is refuted here.
NoOwnerFromRetiredHost ==
    \A w \in live : HostOf(w) \notin knownHosts => ~OwnsClaim(w)

(* ---- Step properties ---- *)

\* Invariant 12: a window's registration is stable while it keeps claiming the same PR
\* ACROSS CONTINUOUS OBSERVATION. An owner whose place in the queue drifts is an owner
\* that can change identity.
\*
\* The antecedent uses Registered(w) rather than regTime[w] # NoTime, and that
\* distinction is the whole property. A first version said only "the window is live and
\* its claim did not change across this step", and TLC refuted it immediately: a window
\* that switches from PR 1 to PR 2 and is then swept has an unchanged claim ACROSS THE
\* SWEEP while holding a registration that belongs to the PR it left. Re-registering it
\* is correct -- switching PRs is a fresh registration, and goes to the back of the
\* queue -- so the mistake was in the phrasing, not the design. Registered(w) is null
\* in exactly that case, which is why stating it this way is not merely a patch.
\* A sweep that could not reach a host must not forget what it already knew about it.
\* Registrations are only ever renewed or dropped by a sweep that actually looked.
\*
\* The antecedent also requires the host to have been in the PREVIOUS sweep
\* (HostOf(w) \in lastCollected). A gap -- the host absent last sweep and collected now --
\* is a DELIBERATE reset: the window may have released and reclaimed unseen, so a fresh
\* registration is correct there and the property must not forbid it. Continuity
\* (in-lastCollected) is exactly the condition under which the place in the queue is
\* frozen; outside it, the reset is allowed.
\*
\* RegistrationStableStep and RegWitnessedStableStep below also require knownHosts' = knownHosts, so a
\* RETIREMENT -- which clears the retired host's registration and witness deliberately -- is excluded
\* just as a gap is; a fleet change legitimately resets both, and a sweep with an unchanged fleet still
\* exercises the properties in full.
\*
\* The consequent also allows a change when the host has left knownHosts -- a RETIREMENT. Retiring a
\* host clears the per-host registration state for windows on it while removing it from both
\* lastCollected and knownHosts, so "HostOf(w) \notin knownHosts'" is the deliberate-forget escape,
\* distinct from a sweep manufacturing a departure (which leaves the host in knownHosts and would
\* still be caught). A partial sweep that rewrote an uncollected but still-known host's registration
\* satisfies neither disjunct and is refuted, exactly as before.
NoPhantomDepartureStep ==
    [][ \A w \in Windows :
          (regTime'[w] # regTime[w] \/ regPr'[w] # regPr[w] \/ regFleet'[w] # regFleet[w]
             \/ regWitnessed'[w] # regWitnessed[w])
            => (HostOf(w) \in lastCollected' \/ HostOf(w) \notin knownHosts') ]_vars

RegistrationStableStep ==
    [][ \A w \in Windows :
          (/\ w \in live /\ w \in live'
           /\ claims[w] # NoPr /\ claims'[w] = claims[w]
           /\ epoch'[HostOf(w)] = epoch[HostOf(w)]
           /\ knownHosts' = knownHosts
           /\ HostOf(w) \in lastCollected
           /\ Registered(w) # NoTime)
             => regTime'[w] = regTime[w] ]_vars

\* Invariant 13: a registration's witness is as stable as its time. While one claim
\* continues under one server ACROSS CONTINUOUS OBSERVATION, regWitnessed must not move --
\* this is the temporal half of the fleet-expansion fix. The registration stability step
\* keeps the PLACE in the queue fixed; this keeps the TRUST fixed, so a later sweep that
\* finally sees the whole fleet cannot flip a first look's witness from FALSE to TRUE
\* while it keeps claiming. The antecedent is RegistrationStableStep's exactly -- the same
\* continuity condition (HostOf(w) \in lastCollected) -- because the two facts travel
\* together: a registration whose time may not drift is one whose witness may not drift
\* either, and a gap resets both. A deliberate mutation recomputing regWitnessed from the
\* current sweep's coverage while the same claim continues under continuous observation --
\* the laundering NoOwnerFromUnwitnessedRegistration rules out at a state -- is refuted
\* here as a step; but a gap-return, where continuity is broken, is permitted to reset it.
\*
\* The antecedent also requires the known fleet to be unchanged (knownHosts' = knownHosts), which
\* excludes a RETIREMENT: retiring the window's host clears its registration and its witness on
\* purpose, so a fleet change is a legitimate reset just as a gap is. A sweep that leaves the fleet
\* unchanged still exercises the property in full.
RegWitnessedStableStep ==
    [][ \A w \in Windows :
          (/\ w \in live /\ w \in live'
           /\ claims[w] # NoPr /\ claims'[w] = claims[w]
           /\ epoch'[HostOf(w)] = epoch[HostOf(w)]
           /\ knownHosts' = knownHosts
           /\ HostOf(w) \in lastCollected
           /\ Registered(w) # NoTime)
             => regWitnessed'[w] = regWitnessed[w] ]_vars

\* Invariant 9: if nothing observable changes, ownership does not change. Stated over
\* a Sweep specifically, because that is the step that rewrites memory: sweeping an
\* unchanged fleet must be idempotent with respect to who owns what.
\* Also requires the known fleet to be unchanged, and that is not a weakening. Learning
\* that the fleet is larger than it thought legitimately invalidates an ordering: an
\* order established before the tool knew a host existed was never an order over the
\* windows on it. TLC refuted the version without this condition as soon as sweeps could
\* cover host subsets -- the fourth time this exercise has caught a property rather than
\* a design.
OwnerStableAcrossSweepStep ==
    [][ (/\ live' = live /\ claims' = claims /\ epoch' = epoch
         /\ knownHosts' = knownHosts /\ lastCollected' = lastCollected)
          => \A p \in PRs : (Claimants(p) # {} => Owner(p)' = Owner(p)) ]_vars

====
