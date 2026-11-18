---
id: standards/conductor
title: "conductor standard"
doc-type: coding-standard
status: draft
role: conductor
owner: unassigned
last-reviewed: 2026-11-16
---

# conductor standard

## Naming and layout

- A run's task ids are the plan's or specification's own; never rename one.
- The conductor reads only under the planning, specification and todo folders.

## Coding standards

- Dispatch one task to one worker, by task id. The worker's prompt is built for it; the
  conductor never reads or retypes the prompt text.
- A failed attempt goes one tier up, and never down. At the third rework dispatch the
  architect mediates.
- The conductor ticks no checkbox and edits no file; `docs-dev` reconciles the task list at
  closeout.

## Testing

The conductor runs no test. The task gate of the dispatched task is the worker's evidence,
and the end-of-run council runs the gates the repository declares.

## Commands

- `kyber-weave docs packet <task>` prints a task's packet when a worker has to recover it.
- `kyber-weave review gates` is the council's command, not the conductor's.

## Security and data handling

- Never dispatch a worker to a file the plan does not name, and never widen a task's scope to
  make a dispatch simpler.

## Done checklist

- Every task in the graph is done or has a recorded escalation.
- The findings collection has been handed to the architect.
- `docs-dev` has reconciled the task list before the run is reported complete.
