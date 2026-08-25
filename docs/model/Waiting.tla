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

   Status: parsed with SANY, model-checked with TLC against Waiting.cfg -- 809,393
   states generated, 253,795 distinct, depth 10, zero violations. Validated by
   mutation: reintroducing the real pane-id-across-restart bug (dropping the epoch
   check in Registered) violates NoCrossEpochMemory; allowing two unwitnessed claimants
   to be ordered violates NeverActOnUnwitnessedOrder; making a sole claimant unownable
   violates SoleClaimantIsAlwaysOwner. The corresponding code is covered by
   InvariantTests and WaitingScanTests against the real implementation, not just this
   model. *)

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
    epoch,       \* the tmux server generation; changes on restart
    regEpoch,    \* the epoch the remembered registrations belong to
    regTime,     \* window -> time it was first SEEN claiming its current PR
    regPr,       \* window -> the PR it was seen claiming
    regFleet,    \* window -> the set of hosts known when its registration was made
    sweptAt,      \* last time the tool collected this host in full, or NoTime
    viewComplete, \* DERIVED by each sweep -- see Sweep
    knownHosts,   \* hosts collected at least once before
    lastCollected \* hosts the most recent sweep actually looked at

vars == << now, claims, live, epoch, regEpoch, regTime, regPr, regFleet, sweptAt,
            viewComplete, knownHosts, lastCollected >>

TypeOK ==
    /\ now \in 0..MaxTime
    /\ claims \in [Windows -> PRs \union {NoPr}]
    /\ live \subseteq Windows
    /\ epoch \in Nat
    /\ regEpoch \in Nat
    /\ regTime \in [Windows -> Int]
    /\ regPr \in [Windows -> PRs \union {NoPr}]
    /\ regFleet \in [Windows -> SUBSET Hosts]
    /\ sweptAt \in Int
    /\ viewComplete \in BOOLEAN
    /\ knownHosts \subseteq Hosts
    /\ lastCollected \subseteq Hosts

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
    IF /\ regEpoch = epoch
       /\ regPr[w] = claims[w]
       /\ regTime[w] # NoTime
       /\ regFleet[w] = knownHosts
    THEN regTime[w]
    ELSE NoTime

\* A window with no registration on a host swept before under this epoch must have
\* appeared since that sweep -- so it is newer than everything already recorded.
Placement(w) == IF Registered(w) # NoTime THEN Registered(w) ELSE MaxTime + 1

\* The order is a FACT only when every recorded time is distinct and at most one
\* claimant has no record at all (that one arrived after the last full sweep). Two
\* unrecorded claimants cannot be ordered against each other by anything but a guess.
Observed(p) ==
    LET C == Claimants(p)
        recorded == { w \in C : Registered(w) # NoTime }
        unrecorded == C \ recorded
    IN /\ \A a, b \in recorded : a # b => Registered(a) # Registered(b)
       /\ Cardinality(unrecorded) <= 1
       /\ (unrecorded = {} \/ (sweptAt # NoTime /\ regEpoch = epoch))

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

Init ==
    /\ now = 0
    /\ claims = [w \in Windows |-> NoPr]
    /\ live = {}
    /\ epoch = 0
    /\ regEpoch = 0
    /\ regTime = [w \in Windows |-> NoTime]
    /\ regPr = [w \in Windows |-> NoPr]
    /\ regFleet = [w \in Windows |-> {}]
    /\ sweptAt = NoTime
    /\ viewComplete = FALSE
    /\ knownHosts = {}
    /\ lastCollected = {}

\* Agents open windows, close them, and switch PRs on their own schedule. Exogenous:
\* the tool observes this, it does not cause it.
AgentActs ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ \E w \in Windows :
         \/ /\ w \notin live                        \* a window opens on some PR
            /\ \E p \in PRs :
                 /\ live' = live \union {w}
                 /\ claims' = [claims EXCEPT ![w] = p]
         \/ /\ w \in live                           \* a window closes
            /\ live' = live \ {w}
            /\ claims' = [claims EXCEPT ![w] = NoPr]
         \/ /\ w \in live                           \* a window switches PR
            /\ \E p \in PRs :
                 /\ p # claims[w]
                 /\ live' = live
                 /\ claims' = [claims EXCEPT ![w] = p]
    /\ UNCHANGED << epoch, regEpoch, regTime, regPr, regFleet, sweptAt, viewComplete, knownHosts, lastCollected >>

\* The tmux server restarts: pane ids restart, so every id may now name a different
\* window. Everything live is replaced; what the tool remembers is about the old ones.
ServerRestarts ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ epoch' = epoch + 1
    /\ live' = {}
    /\ claims' = [w \in Windows |-> NoPr]
    /\ UNCHANGED << regEpoch, regTime, regPr, regFleet, sweptAt, viewComplete, knownHosts, lastCollected >>

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
    /\ \E collected \in (SUBSET Hosts) \ {{}} :
         \* Registrations are renewed or dropped only by a sweep that looked at the host.
         \* A window on an uncollected host is unseen, not gone -- forgetting it would
         \* manufacture a departure and re-register it as new on the next full sweep.
         /\ regTime' = [w \in Windows |->
                          IF HostOf(w) \notin collected THEN regTime[w]
                          ELSE IF w \in live /\ claims[w] # NoPr
                               THEN IF regEpoch = epoch /\ regPr[w] = claims[w] /\ regTime[w] # NoTime
                                    THEN regTime[w]
                                    ELSE now
                               ELSE NoTime]
         /\ regPr' = [w \in Windows |->
                        IF HostOf(w) \notin collected THEN regPr[w]
                        ELSE IF w \in live THEN claims[w] ELSE NoPr]
         /\ regFleet' = [w \in Windows |->
                           IF HostOf(w) \notin collected THEN regFleet[w]
                           ELSE knownHosts \union collected]
         \* A run covering fewer hosts than it has already collected is looking at less
         \* of the fleet than it has seen. It cannot tell that from its arguments -- a
         \* host it was not told about is indistinguishable from one that does not
         \* exist -- only from this memory.
         /\ viewComplete' = (knownHosts \subseteq collected)
         /\ knownHosts' = knownHosts \union collected
         /\ lastCollected' = collected
    /\ regEpoch' = epoch
    /\ sweptAt' = now
    /\ UNCHANGED << claims, live, epoch >>

Next == AgentActs \/ ServerRestarts \/ Sweep

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
\* queue -- and labels the result observed, which is what gates action.
NoCrossEpochMemory ==
    regEpoch # epoch => \A w \in live : Registered(w) = NoTime

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

(* ---- Step properties ---- *)

\* Invariant 12: a window's registration is stable while it keeps claiming the same PR.
\* An owner whose place in the queue drifts is an owner that can change identity.
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
NoPhantomDepartureStep ==
    [][ \A w \in Windows :
          (regTime'[w] # regTime[w] \/ regPr'[w] # regPr[w] \/ regFleet'[w] # regFleet[w])
            => HostOf(w) \in lastCollected' ]_vars

RegistrationStableStep ==
    [][ \A w \in Windows :
          (/\ w \in live /\ w \in live'
           /\ claims[w] # NoPr /\ claims'[w] = claims[w]
           /\ epoch' = epoch
           /\ Registered(w) # NoTime)
             => regTime'[w] = regTime[w] ]_vars

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
