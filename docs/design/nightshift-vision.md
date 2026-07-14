# Nightshift

Nightshift is a coordination system that enables users to scale AI as far as possible while retaining their humanity, literally and figuratively. The key problem is that making AIs scale can take a lot of human attention on pure mechanics, which is a poor use of time and the opposite of intent. Nightshift transitions the AIs to structured work while you spend during your time creatively evaluating the growing shape of your product and its future. You are on the day shift.

## Inspiration

I tried to scale AIs on the `dotnet-inspect` project with burndown lists and quality ladders.

- [Decompiler improvement roadmap](https://github.com/richlander/dotnet-inspect/issues/998)
- [Analysis product quality ladder: staged signal/identity bring-up](https://github.com/richlander/dotnet-inspect/issues/1623)
- [Decompiler product ladder modern syntax blockers](https://github.com/richlander/dotnet-inspect/issues/1643)
- [Burndown list rollup tracker](https://github.com/richlander/dotnet-inspect/issues/1568)

GitHub isn't an agent coordination platform. These issue-based approaches were reasonably effective, but required a lot of care and feeding resulting in massive opportunity and attention cost. Very much "human in the loop" gone wrong. I spent a lot of time developing this "clever" coordination technique, where I kept their tea and coffee cups full.

I'd prompt a new agent with:

> You are a burndown runner; wait 5mins

There was enough context on "burndown runner" and which issues to start with from `AGENTS.md`. The time to read the burndown lists, make a decision, read/write/update the markdown in the issue was about 5 mins. The process was surprisingly slow. I'd start three or four agents in a row, one with no delay, one with 5 mins and the next with 10mins. If I had two or more burndown lists, I might start double that many agents and direct the agents at different lists. This aproach largely worked, but sometimes agents still picked up and delivered a solution for the same burndown issue. Duplicate work further wasted my time.

This approach had two major problems:

- Incredibly distracting for me as the maintainer.
- I basically defaced my own project as an AI-dominated mess that is impossible (even for me) to reason about.

This is not way.

## Character of a solution

The basis of the the burndown lists was claims. It also included ordered slices, some of which impose a sequence and others that offer parallel execution. That could be because B builds on A or A and B are strong merge conflict candidates. We need to keep that.

GitHub as an agent reporting and consistency platform didn't work. We need to move that all to a local database. We're not trying to enable consistency with agents in Seattle and Toronto, but agents on one or two co-located machines (like a desktop and a laptop).

Everything was posted as me. That's not good and quite literally "ruins my name". That doesn't mean using AI to do work is bad. This is in essense the dayshift vs nightshift split.

I can choose for dayshift agents (the ones I am directly controlling) to post on GitHub as me or not. That's a convenience choice and an extension of my effort.

Nightshift agents should not post to GitHub as me. They should either just produce local branches for consideration or post as a GitHub App. They are producing a result without my consideration. I only interact with them at merge time.

GitHub is a public forum. I want to represent myself as doing product and engineering work, not as the center of an uninterpretable storm of AI-generated Github "contributions".

## Splitting day and night shifts

I've had some recent experience where I spend 0.5-2 days working through a design problem with one or more agents that usually either generizes a targeted design to a more general one or that scopes a untidy and wondering idea into pragmatic execution. In those cases, the deliverable is a doc, a first example implementation, and then a backlog of other places we can apply or adjust to the new approach. That's day shift and a good leverage.

Now, we need to bootstrap night shift.

The backlog should be:

- Optional: A set of GitHub issues, possibly broken into slices
- A JSON file that describes the issues, slices, and sequencing.

The JSON file describes the GitHub state for nightshift deployment. A strong goal is to create a separation between agents and GitHub, which also make them more efficient. A coordinator agent registers the work with the `nightshift` tool. After that, new agents can be added to a rotation to start work as shift workers. These agents can be top-level Copilot sessions, subagents, or something else. It doesn't matter. They just need to get the work done.

## Joining the Nightshift

The primary gesture is `nightshift next`. This command is a request for new work, like an Uber driver waiting for their next ride. This command blocks until work is available or a timeout expired. When command returns (successfully), the agent has an exclusive claim with all the instructions on how to proceed without need to go to GitHub. Start the work.

We need to think through the remaining part of the lifecycle. We likely need a command where the agent runs `nightshift check` (or similar) to ask for more guidance on their work. This could result in adversarial review or some other activity.

`nightshift check` would signal to a supervisor to provide next steps. This could be an agent or another tool. This interaction model could enable extensive workflows.

- `nightshift decline` would signal to return the work back to the work pool.
- `nightshift done` would signal that the agent believes that they have completed all their work.

Note: all command names subject to change. These are rough ideas of useful workflows. We'll need to experiment and adapt.

## Extending capability

The philosophy of the Nightshift project is that safety can be delivered by paritioning. The base toolset does very little. It enables fundamental coordination capability, with no concept of permissions, sandboxing, integration with GitHub, resource govenors, or anything else interesting. `nightshift` is intended to be the opposite of a god-mode tool.

The idea is that larger workflows can be built on top, making `nightshift` a building block. `nightshift` doesn't know about other tools nor does it have an add-in model. We need to ensure we have sufficient building blocks in place.

I've been much more successful with developing high-quality code when new code has to survive two clean adversarial reviews. That can easily be instituted within this system as another phase.

A "full service" workflow could enable full automonous development with automerge. That's well outside the `nightshift` threat model. The tool is intended to both be very narrow in terms of integration but also has enough hooks that very capable systems can be built on top.

## Development

We should bring up sufficient nightshift functionality such that we can use it as part of development. This approach will both be excellent dogfooding and accelerate development.
