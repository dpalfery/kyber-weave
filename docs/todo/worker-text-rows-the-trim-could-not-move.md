---
id: todo/worker-text-rows-the-trim-could-not-move
title: Move the worker-text rows the trim left behind
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Move the worker-text rows the trim left behind

The worker-text trim removed the duplicated text whose home already existed and left the rest in place.
Each row it left is recorded with its reason in [the worker rule map](../kyber-squad/worker-rule-map.md),
so nothing was lost; what is missing is a way to schedule the three groups, because each is blocked by a
different thing and each needs a different kind of decision.

## Group 1 — three rows blocked by a test pin, not by a missing home

The Discovery order and the discovery header (`D1`, `D2` in the rule map) appear in 20 subagent bodies
and are also in the injected baseline. The agent-body copies cannot go while
`SquadCanonicalContentTests.LoadCodeReadingAgentsDirectIndexedDiscoveryBeforeRawReads` requires each
body to carry a `## Discovery order` section naming `codegraph_explore`, both docs tools and the
read-directly sentence in that order. The pin protects the very section that duplicates the injected
block, so **moving the pin to the render** — assert the injected block instead of each body — is the
unblocking step, and it is a test change before it is a text change.

## Group 2 — five rows blocked by six theories that assert both copies

`G1` to `G5` in the rule map are the conductor's copy of the shared execution contract. Six theories in
`SquadPerformanceContractTests` assert the same phrases in `agents/conductor.md` **and** in
`skills/delivery-execution/SKILL.md`, so removing the conductor copy turns them red. This is the same
shape as group 1: the pin has to name one home first. `G6` and `G7` are already done — they are the two
rows no theory pins in the conductor.

## Group 3 — four rules that name a home which does not carry them

`E4` (the diagnostics baseline procedure), `E5` (the sweep scope), `E6` ("this is not evidence") and
`E7` (every diagnostic counts) each name `skills/task-gate/SKILL.md` as their single home, and that file
does not state any of them. Removing the agent-body copy would delete the rule rather than move it, so
all four stay. The order the rest of the trim followed applies here: **write the procedure into the
skill first, then delete the duplicate** — the order the completion digest and the failure loop already
followed.

## Why this is one todo

Each group is small, none blocks a shipped behaviour, and all three fail the same way if someone starts
at the text: the rule disappears or a pin goes red for a reason that looks like a regression. Read the
rule map's row before touching any of them; it records which files carry the rule and what holds it.
