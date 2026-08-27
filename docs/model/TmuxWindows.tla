---- MODULE TmuxWindows ----
(* Model of identity over tmux window state, where several agents write to one shared
   namespace with no access control and one of them is the tool itself.

   The bug class: a tmux command without an explicit target applies to whichever window
   is CURRENT, which is somebody else's. Observed live, twice and in two forms -- four
   windows on one host carrying a fifth's @agent_state, and a window named for a PR it
   was not working on while its own state said otherwise. Both are the same mistake at
   different call sites, and neither is visible from inside the agent that made it.

   The question this model exists to answer is not "do agents make that mistake" -- they
   demonstrably do -- but "GIVEN that they do, which identity channels can still be
   trusted, and does the tool's own writing make things worse."

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
    nameOpt,   \* window -> PR encoded in its name. Writable by any agent AND the tool.
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

\* A name shared by two windows belongs to neither: a duplicate is evidence that a
\* rename landed somewhere it did not belong.
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

\* The tool's own --rename. It is a writer into the same namespace it reads identity
\* from, which is the feedback loop worth checking: renaming must never change what the
\* tool would conclude about any window, including the one it renamed.
\*
\* This action renames to Attributed(w) -- the identity read at the SAME instant the name
\* is written -- so it models a rename that acts on the window's live attribution, not a
\* stale one. That correspondence is load-bearing and it is what the rename guard enforces
\* in the code: a plan is computed from an earlier sweep, but each mutation is guarded, in
\* one tmux client, on the live window name, the live @agent_state, the live server epoch
\* AND the live #{window_activity} all still equalling the scanned values, and aborts
\* (reporting stale) otherwise. The activity stamp was added in round 11: the suffix the
\* code applies also depends on the pane's activity (every suffix is read from a pane that
\* had STOPPED), and tmux advances window_activity on any output, so a pane that resumed
\* between the sweep and the rename no longer matches and the mutation aborts -- closing the
\* gap where an idle pane that started working during the GitHub read could still be renamed
\* -ready. The same round removed fleet-global ownership (owner/follower) from the suffix
\* entirely: it is decided across windows other than the one being renamed, over state no
\* per-window guard can revalidate at mutation time, so it is no longer written into a name
\* at all -- which keeps this single-instant action, over one window's own channels, a
\* faithful abstraction. Round 12 closed the residual same-second window: the activity stamp
\* is only a whole-second-quiescence proof if the pane's last activity is in a second already
\* past when the sweep observed it, so the sweep also reads the TARGET's own second and the
\* code applies a suffix only when the scanned activity strictly predates it -- a pane read in
\* the same second as its last output is deferred, not named, until a later sweep. That makes
\* the single-instant identity Attributed(w) faithful even at a second boundary: the code
\* never writes a name whose activity premise it cannot prove held for a full second. Without
\* the guard the code could write a name computed from an attribution that has since moved --
\* a transition this single-instant action does not contain -- so the guard is precisely what
\* makes ToolRenames a faithful abstraction rather than an optimistic one.
ToolRenames ==
    /\ \E w \in AgentWindows :
         /\ Attributed(w) # None
         /\ nameOpt' = [nameOpt EXCEPT ![w] = Attributed(w)]
    /\ UNCHANGED << worksOn, stateOpt, paneText, current >>

\* An agent moves on to different work.
Reassign ==
    /\ \E w \in AgentWindows : \E p \in PRs :
         /\ p # worksOn[w]
         /\ worksOn' = [worksOn EXCEPT ![w] = p]
    /\ UNCHANGED << stateOpt, nameOpt, paneText, current >>

Next == SwitchCurrent \/ PublishTargeted \/ PublishUntargeted \/ ToolRenames \/ Reassign

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

(* ---- Step properties ---- *)

\* The feedback loop. The tool writes names and reads names, so its own rename must never
\* change what it would conclude -- about the window it renamed or any other, including
\* by creating or resolving a duplicate.
ToolRenameChangesNothingStep ==
    [][ (stateOpt' = stateOpt /\ worksOn' = worksOn /\ paneText' = paneText)
          => \A w \in AgentWindows : Attributed(w)' = Attributed(w) ]_vars

====
