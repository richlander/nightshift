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
    PRs,        \* abstract PR numbers
    MaxTime     \* bound the model so the state space is finite

ASSUME /\ MaxTime \in Nat
       /\ Windows \subseteq (Nat \ {0})
       /\ PRs \subseteq (Nat \ {0})
       /\ Windows # {}
       /\ PRs # {}

\* Windows and PRs are positive integers so that ties can be broken on a fixed order;
\* 0 is the "claiming nothing" / "no window" sentinel, and -1 means never recorded.
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
    sweptAt      \* last time the tool collected this host in full, or NoTime

vars == << now, claims, live, epoch, regEpoch, regTime, regPr, sweptAt >>

TypeOK ==
    /\ now \in 0..MaxTime
    /\ claims \in [Windows -> PRs \union {NoPr}]
    /\ live \subseteq Windows
    /\ epoch \in Nat
    /\ regEpoch \in Nat
    /\ regTime \in [Windows -> Int]
    /\ regPr \in [Windows -> PRs \union {NoPr}]
    /\ sweptAt \in Int

(* ---- What the tool derives from what it remembers ---------------------------------

   Ownership among the windows claiming one PR. A window's registration counts only if
   it was recorded under the CURRENT epoch and for the PR the window still claims. *)

Claimants(p) == { w \in live : claims[w] = p }

Registered(w) ==
    IF regEpoch = epoch /\ regPr[w] = claims[w] /\ regTime[w] # NoTime
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
OwnsClaim(w) ==
    /\ w \in live
    /\ claims[w] # NoPr
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
    /\ sweptAt = NoTime

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
    /\ UNCHANGED << epoch, regEpoch, regTime, regPr, sweptAt >>

\* The tmux server restarts: pane ids restart, so every id may now name a different
\* window. Everything live is replaced; what the tool remembers is about the old ones.
ServerRestarts ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ epoch' = epoch + 1
    /\ live' = {}
    /\ claims' = [w \in Windows |-> NoPr]
    /\ UNCHANGED << regEpoch, regTime, regPr, sweptAt >>

\* A sweep: record every live window's current claim, and mark the host swept. A window
\* keeps its registration for as long as it keeps claiming the same PR; switching is a
\* fresh registration.
Sweep ==
    /\ now < MaxTime
    /\ now' = now + 1
    /\ regTime' = [w \in Windows |->
                     IF w \in live /\ claims[w] # NoPr
                     THEN IF regEpoch = epoch /\ regPr[w] = claims[w] /\ regTime[w] # NoTime
                          THEN regTime[w]              \* unchanged claim keeps its place
                          ELSE now                     \* new or switched claim registers now
                     ELSE NoTime]
    /\ regPr' = [w \in Windows |-> IF w \in live THEN claims[w] ELSE NoPr]
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
SoleClaimantIsAlwaysOwner ==
    \A w \in live : (claims[w] # NoPr /\ Claimants(claims[w]) = {w}) => OwnsClaim(w)

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
OwnerStableAcrossSweepStep ==
    [][ (live' = live /\ claims' = claims /\ epoch' = epoch)
          => \A p \in PRs : (Claimants(p) # {} => Owner(p)' = Owner(p)) ]_vars

====
