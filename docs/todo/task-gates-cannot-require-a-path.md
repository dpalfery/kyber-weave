---
id: todo/task-gates-cannot-require-a-path
title: Task gates cannot declare a required path, so a missing dependency reads as a failing gate
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-11-07
---

# Task gates cannot declare a required path, so a missing dependency reads as a failing gate

A `review.gates` entry can declare `requires-paths`, and this repository's four dash council gates
use it to name `dash/node_modules`: when the dependency is absent the gate does not start and
reports `KW-REVIEW-033` with the install that unblocks it, so nobody reads it as a code failure.
A `review.task-gates` entry cannot. `requires-paths` is not in the task-gate reader's key list, so
writing it on a task gate loads and is reported as an unknown key (`KW-CONFIG-002`), and the record
the task-gate runner executes carries no field for it.

The cost is the failure the council key exists to prevent, one layer lower. The `react-dev`,
`typescript-dev` and `tauri-dev` task gates run dash commands, and a fresh worktree has no
`dash/node_modules`, so a developer agent sees a failing gate and debugs it as its own change.

The files that would carry the key are:

- `src/KyberWeave.Core/Configuration/ReviewConfig.cs` — the task gate record a planned gate is
  built from.
- `src/KyberWeave.Core/Configuration/ReviewConfigLoader.cs` — the unknown-key list for
  `review.task-gates` entries, and the validation that list sits beside.
- `src/KyberWeave.Core/Review/TaskGateSelection.cs` — the planned gate, and the `{changed}` and
  `{task}` expansion that produces it.

The task gates for `react-dev`, `typescript-dev` and `tauri-dev` would then declare
`dash/node_modules`, as their council counterparts do.

**Work.** Carry the key through those three files, sharing the validation the council key already
has — no empty entry, no absolute path, no `..` escape — so one rule covers both. Declare
`dash/node_modules` on the task gates that run dash commands.

**Acceptance.** A task gate that declares an absent `requires-paths` entry does not start, reports
`KW-REVIEW-033` with the install hint, and fails the run when it is blocking, exactly as the
council path does; a gate that declares no entry is unchanged. The `react-dev`, `typescript-dev` and
`tauri-dev` gates declare `dash/node_modules`, and a test pins each.
