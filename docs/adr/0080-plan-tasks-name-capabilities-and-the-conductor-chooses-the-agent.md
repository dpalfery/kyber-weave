---
id: adr/0080-plan-tasks-name-capabilities-and-the-conductor-chooses-the-agent
title: Plan tasks name capabilities and the conductor chooses the agent
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: DocGraph
decided-by: []
supersedes: []
code-refs:
  - PlanDocumentParser
  - PlanTaskFileGenerator
  - PlanTaskPacket
  - PacketHostFacts
---

# ADR 0080: Plan tasks name capabilities and the conductor chooses the agent

## Status

Accepted, 2026-11-13. Decided by the owner. It replaces the task file's derived `role` described in
[ADR 0063](0063-the-task-file-is-tracked-and-the-plan-stays-authoritative.md); the rest of ADR 0063 stands.

## Context

The architect wrote `Skills: <name>` on each plan task, and `docs tasks --write` set the task's `role`
to the first of those names. The conductor dispatched the agent named by `role`, and the worker opened
the same names as skill files. That works only where an agent and a skill share a name. In a live run
the architect wrote `react-dev`, which is an agent with no skill of that name, and the worker tried to
open a file that does not exist. The architect was in effect choosing agents, so adding an agent meant
changing the architect's instructions.

## Decision

1. **A plan task names capabilities.** Each task carries a `Capabilities:` line: the abilities the
   work needs, in plain words ("React and TypeScript UI with Playwright tests", "documentation"). The
   architect and product-owner prose name no agent and no skill for routing. An older plan's `Skills`
   line is still read, as the capability text, so existing plans stay valid.
2. **The task file carries the text.** Each entry has a `capabilities` field. `role` is written only
   when the plan task has an explicit `Role:`, `Agent:` or `Specialist:` line: the owner's override.
   A task with neither is a `KW-DOC-TASKS-004` gap the plan author fixes. The execution digest is
   unchanged, because the task text it already covers holds the line.
3. **The conductor chooses the agent.** The finalize tool's ready queue and `docs tasks --list` show
   each task's capabilities (`needs:`), and the conductor dispatches the agent available to it whose
   description best fits them. The match is the conductor model's judgement: there is no lookup
   table, fixed capability vocabulary or validator that maps capabilities to agents.
4. **The worker loads its own skills.** Its agent file names them; the packet no longer lists skills
   to open. The worker passes its own name to `docs packet --agent`, so the Host facts list its task
   gates.
5. **The Arbiter takes the agent from the dispatch.** No rule read the task file's `role`.
   `KW-ARB-OWNER-001` reads skill names from the plan, so a current plan answers `no-mapping`, which
   allows. `KW-ARB-OWNER-002` still asks the model which roster agent owns the task, but every answer
   now allows and is logged: under [ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)
   a task with no recorded owner leaves nothing to hold the dispatch to. File-scope rules are per
   task and unchanged.

## Consequences

- A new agent with an accurate `description` is dispatchable without touching the architect.
- Agent descriptions are now routing input: a vague one gets the wrong work.
- A host that wants the Arbiter to stop a dispatch it judges to the wrong agent sets
  `effects: { other: escalate }` on `KW-ARB-OWNER-002`.
