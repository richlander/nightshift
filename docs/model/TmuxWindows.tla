---- MODULE TmuxWindows ----
(* Model of identity over tmux window state, where several agents write to one shared
   namespace with no access control. The tool only READS this namespace. The earlier
   `octoshift waiting --rename` once made the tool a writer here too, but it was removed
   (nightshift issues #170-#172): a persistent window name cannot be atomically validated
   against the GitHub state, active-pane selection and wall-clock activity its suffix
   encoded, so a name that was true when written could silently go false. With the tool no
   longer writing, the feedback loop this model once also checked -- whether the tool's own
   writing makes things worse -- is closed by construction; what remains, and is live, is
   which identity channels a reader can trust.

   The bug class: a tmux command without an explicit target applies to whichever window
   is CURRENT, which is somebody else's. Observed live, twice and in two forms -- four
   windows on one host carrying a fifth's @agent_state, and a window named for a PR it
   was not working on while its own state said otherwise. Both are the same mistake at
   different call sites, and neither is visible from inside the agent that made it.

   The question this model exists to answer is not "do agents make that mistake" -- they
   demonstrably do -- but "GIVEN that they do, which identity channels can still be
   trusted."

   Scope: names, options and pane text are abstract PR identifiers, not strings. Parsing
   is not modelled -- Waiting.tla's header explains why that boundary is deliberate, and
   the same reasoning applies here. What IS modelled is who can write what, and to whose
   window.

   Companion to Waiting.tla, which covers memory and ownership over time. This one covers
   attribution at a single instant, and the two are kept apart because mixing "who owns
   this PR" with "which window is this even" blurs both. *)

EXTENDS Integers, FiniteSets

CONSTANTS
    AgentWindows,  \* one window per agent, identified by the agent that owns it
    PRs

ASSUME /\ AgentWindows \subseteq (Nat \ {0})
       /\ PRs \subseteq (Nat \ {0})
       /\ AgentWindows # {}
       /\ PRs # {}

None == 0

VARIABLES
    worksOn,   \* window -> the PR its own agent is actually working on. Ground truth,
               \* and unobservable: it exists only inside the agent.
    stateOpt,  \* window -> PR in its @agent_state. Writable by ANY agent.
    nameOpt,   \* window -> PR encoded in its name. Writable by any agent; the tool only reads it.
    paneText,  \* window -> PR its agent printed. Writable ONLY by its own agent, because
               \* a process writes to its own terminal and nowhere else.
    current    \* the window an untargeted command lands on

vars == << worksOn, stateOpt, nameOpt, paneText, current >>

TypeOK ==
    /\ worksOn  \in [AgentWindows -> PRs \union {None}]
    /\ stateOpt \in [AgentWindows -> PRs \union {None}]
    /\ nameOpt  \in [AgentWindows -> PRs \union {None}]
    /\ paneText \in [AgentWindows -> PRs \union {None}]
    /\ current  \in AgentWindows

(* ---- What the tool concludes ---------------------------------------------------- *)

\* A name shared by two windows belongs to neither: a duplicate is evidence that a rename
\* -- an agent's own, targeted or not -- landed somewhere it did not belong.
NameIsUnique(w) ==
    /\ nameOpt[w] # None
    /\ \A o \in AgentWindows \ {w} : nameOpt[o] # nameOpt[w]

\* The rule as implemented: prefer the published state, fall back to an unambiguous name.
Attributed(w) ==
    IF stateOpt[w] # None THEN stateOpt[w]
    ELSE IF NameIsUnique(w) THEN nameOpt[w]
    ELSE None

\* The rule with pane text used as corroboration rather than as identity. Pane text is
\* the ONE channel no other agent can write, because a process writes to its own terminal
\* and nowhere else -- which makes it useless for durability (it scrolls away) and uniquely
\* sound for checking whether a durable channel has been clobbered.
AttributedCorroborated(w) ==
    IF paneText[w] # None
    THEN (IF Attributed(w) = paneText[w] THEN Attributed(w) ELSE None)
    ELSE Attributed(w)

(* ---- Actions ---- *)

Init ==
    /\ worksOn \in [AgentWindows -> PRs]
    /\ stateOpt = [w \in AgentWindows |-> None]
    /\ nameOpt = [w \in AgentWindows |-> None]
    /\ paneText = [w \in AgentWindows |-> None]
    /\ current \in AgentWindows

\* The operator moves between windows, which is what makes an untargeted write land
\* somewhere surprising rather than harmlessly on itself.
SwitchCurrent ==
    /\ \E w \in AgentWindows : current' = w
    /\ UNCHANGED << worksOn, stateOpt, nameOpt, paneText >>

\* An agent publishes correctly: -t "$TMUX_PANE", so it writes its own window. It prints
\* its report at the same time, which is what the round flow already requires -- so for a
\* correctly behaving agent the durable channels and its own output move together.
PublishTargeted ==
    /\ \E w \in AgentWindows :
         /\ stateOpt' = [stateOpt EXCEPT ![w] = worksOn[w]]
         /\ nameOpt' = [nameOpt EXCEPT ![w] = worksOn[w]]
         /\ paneText' = [paneText EXCEPT ![w] = worksOn[w]]
    /\ UNCHANGED << worksOn, current >>

\* An agent publishes with no target, or with an empty one. Both options land on the
\* CURRENT window -- and they land together, since the real command sets them in one
\* chain, so the two durable channels are clobbered consistently and agree with each
\* other about a PR neither window is working on.
PublishUntargeted ==
    /\ \E w \in AgentWindows :
         /\ stateOpt' = [stateOpt EXCEPT ![current] = worksOn[w]]
         /\ nameOpt' = [nameOpt EXCEPT ![current] = worksOn[w]]
    /\ UNCHANGED << worksOn, paneText, current >>

\* An agent names its OWN window -- targeted, so it writes its own window -- for the PR it
\* is working on, without publishing @agent_state at the same time. This is how a window
\* comes to carry a PR-encoding name with no state yet: a window named at creation, or by
\* the coordinator at branch push, before the agent has published anything. It is the one
\* action that writes nameOpt independently of stateOpt, and so the one that exercises the
\* reader's name fallback and its ambiguity rule -- two agents on one PR that both name
\* their windows collide, and a shared name identifies neither.
\*
\* The tool used to be a writer here too (`octoshift waiting --rename`), and this action was
\* its rename. It is not any longer: a persistent window name cannot be atomically validated
\* against the volatile state its suffix encoded, so the rename was removed and the tool now
\* only reads this namespace (issues #170-#172). What remains is an ordinary agent write.
NameWindow ==
    /\ \E w \in AgentWindows :
         /\ nameOpt' = [nameOpt EXCEPT ![w] = worksOn[w]]
    /\ UNCHANGED << worksOn, stateOpt, paneText, current >>

\* An agent moves on to different work.
Reassign ==
    /\ \E w \in AgentWindows : \E p \in PRs :
         /\ p # worksOn[w]
         /\ worksOn' = [worksOn EXCEPT ![w] = p]
    /\ UNCHANGED << stateOpt, nameOpt, paneText, current >>

Next == SwitchCurrent \/ PublishTargeted \/ PublishUntargeted \/ NameWindow \/ Reassign

Spec == Init /\ [][Next]_vars

(* ---- Invariants ---- *)

\* The property everything else is judged against: when the tool names a PR for a window,
\* that window's agent is working on it.
\*
\* This is EXPECTED TO FAIL, and its counterexample is the finding. An untargeted publish
\* writes another window's state, and state is the authoritative channel -- so the tool
\* believes it, with no defect to notice, because a clobbered window's two durable
\* channels agree with each other. Nothing in the reader can recover from this: the
\* guidance's -t "$TMUX_PANE" is load-bearing, not hygiene.
\* Soundness is stated against what a window's OWN agent last said about itself, which is
\* exactly what pane text holds, and not against worksOn. worksOn is unobservable -- it
\* exists only inside the agent -- so comparing to it would fail on ordinary staleness (an
\* agent that has changed work and not yet republished) and drown the misdirection this
\* model is about.
IdentityIsSound ==
    \A w \in AgentWindows :
        (Attributed(w) # None /\ paneText[w] # None) => Attributed(w) = paneText[w]

\* The same question asked of the corroborated rule. Pane text cannot be misdirected, so
\* a window whose durable channels were clobbered disagrees with its own output and is
\* attributed nothing rather than attributed wrongly.
CorroboratedIdentityIsSound ==
    \A w \in AgentWindows :
        (AttributedCorroborated(w) # None /\ paneText[w] # None)
            => AttributedCorroborated(w) = paneText[w]

\* A name two windows share identifies neither.
AmbiguousNameIdentifiesNothing ==
    \A w \in AgentWindows :
        (stateOpt[w] = None /\ ~NameIsUnique(w)) => Attributed(w) = None

\* Anti-degenerate: a rule that attributes nothing satisfies every soundness property
\* above. A window whose own agent published correctly, and which nobody has since
\* clobbered, must be identified.
CorrectlyPublishedIsIdentified ==
    \A w \in AgentWindows :
        (stateOpt[w] # None /\ stateOpt[w] = paneText[w])
            => AttributedCorroborated(w) = stateOpt[w]

\* The reason corroboration works: pane text is the one channel no other agent can write.
\* When the durable channels disagree with a window's own output, that window was written
\* by somebody else, and it is attributed nothing rather than attributed wrongly.
CorroborationCatchesAClobber ==
    \A w \in AgentWindows :
        (stateOpt[w] # None /\ paneText[w] # None /\ stateOpt[w] # paneText[w])
            => AttributedCorroborated(w) = None

====
