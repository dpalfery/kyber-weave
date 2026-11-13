---
id: adr/0076-test-first-is-one-worker-per-task
title: Test-first is one worker per task, proved by the task gate's two reports
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: KyberSquad
decided-by: []
supersedes: []
code-refs: []
---

# ADR 0076: Test-first is one worker per task, proved by the task gate's two reports

## Status

Accepted, 2026-11-13. Decided by the owner.

## Context

A `test-first` plan split each behaviour into two tasks: a RED task in which `test-dev` wrote failing
tests, and a GREEN task in which a developer agent made them pass. Machinery carried evidence between
them: a `RED_EVIDENCE:` digest line, the ledger fact `ledger.red-evidence`, Arbiter rule
`KW-ARB-MODE-001` gating each GREEN dispatch on it, a plan warning for tests ordered before the code
they name (`KW-DOC-TASKS-010`), and prose on RED-then-GREEN ordering and stub tasks.

In headless runs the split stopped work, in this order:

1. A compile deadlock: every RED test landed first in one compiled test project, so nothing compiled.
2. The Arbiter blocked scaffolding tasks for lacking RED evidence.
3. A run stopped because the tests could not compile without a production stub the host forbids.
4. A run stopped because the RED gate rejected all three test tasks.

Each fix added rules; none removed the cause, which is that one behaviour's tests and code were
owned by two dispatches.

## Decision

1. **One task, one worker.** A test-first task is one unit of behaviour owned by one specialist
   (`csharp-dev`, `dal-dev`, `react-dev`, `typescript-dev`, …). Its packet names the behaviour, the
   tests that must exist, the production and test files, and the commands. There are no RED-only or
   GREEN-only tasks and no stub tasks whose purpose is to let another task's tests compile.
2. **The loop inside the task.** The worker writes the tests; runs its task gate with `--red` and a
   report path of its own (it passes only on tests that ran and failed, or a build that failed only on
   symbols missing from the task's own files); implements; and runs the task gate again.
3. **The proof is two machine-written reports.** The failing-run report, older than the passing-run
   report, in the task's scratch folder. The digest no longer carries a `RED_EVIDENCE:` line.
   `KW-ARB-MODE-002` checks the two reports on the worker's return, from the folder of the report its
   `TASK_GATE:` line claims: a missing or later failing run escalates; a plan exemption (standard mode,
   a `test-dev` or documentation target, a "Not applicable" contract row) or a claim it cannot read
   allows and is logged ([ADR 0072](0072-the-arbiter-blocks-only-on-machine-facts-and-confident-model-answers.md)).
   A failing-run report is recognised by its `RED accepted` gate summary, not its file name.
4. **`test-dev` takes test-only work**: characterization tests before a refactor, an end-to-end suite,
   test infrastructure, and a defect's failing test in the bug-crusher flow when no developer takes
   the fix. Developer agents write the tests for their own changes.
5. **Retired:** `KW-ARB-MODE-001` with `ledger.red-evidence`, the `red-evidence` ledger marker and
   `<task>=red` attestations; `plan.task.dependents`; `KW-ARB-OWNER-001`'s clause making `test-dev`
   owner of every test-first task; `KW-DOC-TASKS-010`; and the prose on adversarial RED/GREEN
   separation, zero-stash RED proofs, same-role bundling, RED-then-GREEN ordering and stub tasks.
   `KW-ARB-SCOPE-002` reads a task's own tests as part of the task.
6. `development-mode: standard` is unchanged.

## What is given up

An independent author for each behaviour's tests. The tests now come from the worker who wrote the
code, with that worker's blind spots. The review council's `test-adequacy` lens is the check on test
quality: it reads each added test as an outsider and asks whether it fails with the change reverted,
whether it asserts behaviour rather than implementation, and which negative and edge cases are missing.

## Monitoring

Each run's council findings from the `test-adequacy` lens are the measure, read per run alongside
`KW-ARB-MODE-002`'s decisions in the Arbiter decision log. A significant rise in surviving
test-adequacy findings, or in returns with no failing run, reopens this decision.

## Consequences

- A behaviour's tests and code reach one worktree together, so the compile deadlock and the stub
  question cannot arise from the plan's shape.
- The test contract table keeps its columns; its `RED evidence required` and `GREEN acceptance` cells
  now describe the failing and passing runs of one task.
- Plans written for the old model still parse: task ids such as `T1-RED-…` and the contract headers
  are unchanged, and a ledger line carrying `red-evidence` still reads.
