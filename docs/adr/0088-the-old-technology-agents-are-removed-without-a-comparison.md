---
id: adr/0088-the-old-technology-agents-are-removed-without-a-comparison
title: The old technology agents are removed without a comparison
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-17
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - WorkerRosters
  - PlanTaskAgentPick
  - RemovedAgentMap
  - TechnologyAgentMap
  - PlanTaskFileValidator
---

# ADR 0088: The old technology agents are removed without a comparison

## Status

Accepted, 2026-11-17. Decided by the owner. It settles the removal question the
[role-based worker agents specification](../archive/specs/role-based-worker-agents/README.md) left open: whether the
fourteen old agents leave once a comparison of the two rosters has run.

## Context

The specification kept the per-technology roster beside the tiered one and planned a comparison (tasks 5.1
and 5.2 of its task list) to decide when — and whether — the fourteen agents go. The comparison would run one
approved plan once per roster, from one base commit on one harness, and record tasks completed, attempts,
gate failures, review findings, tokens and wall time.

Three attempts to gather that evidence did not produce a comparison:

- **Two reduced comparison passes on a scratch project both failed for product defects.** Each pass stopped
  on a defect in the product rather than reaching a result about the rosters, and each defect was fixed
  afterwards. A pass that ends on a product bug measures the product, not the roster.
- **Three acceptance runs on the Hotshot Logistics demo on GitHub Copilot CLI, on 2026-11-17, started,
  planned and stopped before dispatching any developer.** Each stopped on a defect, and each defect was fixed
  afterwards. None reached a dispatch, so none produced a task outcome to compare.

Meanwhile keeping both rosters had a cost the specification itself states: every phase had to stay additive,
two sets of agents had to be rendered and tested, and the roster switch had to be honoured by every reader of
a task file.

## Decision

1. **The fourteen old agents are removed now.** They are the ten per-technology developers, `sql-database-architect`,
   `test-dev`, `azure-reader` and `github-devops`. The tiered roster is the only roster.

2. **The comparison was not used to decide.** The two reduced passes and the three acceptance runs above are
   recorded as evidence of the product's defects, not as evidence about the rosters. The owner's decision does
   not rest on them, and no later reader should treat those runs as a comparison result.

3. **Anyone who wants the old agents installs an older version.** There is no switch, no compatibility package
   and no second roster to restore. A host that needs a per-technology developer downloads the release that
   still shipped it.

4. **The roster switch is gone.** In host configuration, `worker-roster: technology` is a configuration error
   whose message names the change, because there is nothing left for it to select. A task file written before
   the removal that records `technology` is read as the one roster, with `KW-DOC-TASKS-019` saying so. An agent
   that declares no `worker-roster` key serves the one roster.

5. **The migration path stays as the specification wrote it.** An agent-keyed `review.task-gates` entry keeps
   resolving through the shipped technology map for two minor releases, `docs validate` warns about an entry
   for a removed agent, and `kyber-weave review migrate-task-gates` rewrites the host's entries as
   `review.technology-gates`. A plan or task file naming a removed agent resolves to a developer tier by the
   task's difficulty, or to `architect`, `platform-researcher` or `devops`, and reports the substitution.

## Consequences

- One roster ships, so the agents, the task gates and the documents describe one set of workers. Nothing in
  the product has to answer "which roster is this run on?" any more.
- A host that wanted the old agents has to move back to an older release, which also moves back everything
  else that release shipped. That is the accepted cost of removing them rather than keeping both.
- The removal is not evidence that the tiered roster works better. The evidence for it is the specification's
  own reasoning — three agents that differ only in model strength, chosen by a difficulty rating — and the
  acceptance runs are still to come.

## Related

- [ADR 0085](0085-tasks-carry-a-difficulty-and-one-technology-and-a-hook-inserts-the-task-prompt.md) — the tiers, the difficulty rating and the task prompt the roster dispatches with
- [Kyber-Squad architecture](../kyber-squad/architecture.md#12-the-worker-roster-and-developer-tiers) — the agents of the one roster, and where a removed name goes
- [Configuration](../configuration.md) — `worker-roster` and the technology-keyed task gates
