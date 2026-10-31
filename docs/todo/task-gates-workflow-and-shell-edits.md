---
id: todo/task-gates-workflow-and-shell-edits
title: Add task gates that cover workflow YAML, the review config and shell scripts
doc-type: todo
component: ReviewCouncil
status: draft
owner: dpalfery
last-reviewed: 2026-10-30
---

# Add task gates that cover workflow YAML, the review config and shell scripts

During the [test process storm guard plan](../plans/2026-10-30-test-process-storm-guard.md),
the task gate reported `NONE_APPLICABLE` (every gate skipped) for a task that edited only
`.github/workflows/ci.yml`, `.kyber-weave/kyber-weave.yml` and `scripts/*.sh`, and for the first
run of a .NET test-only task. Nothing deterministic checked those edits at the task boundary.
The only checks were the tests that read those files (for example `CiLeftoverStepTests`,
`ProcGuardScriptTests`, the review-config pin tests) and `shellcheck` at the whole-change level.

Decide which deterministic gates the task gate should run for these file kinds, for example:

- a workflow linter (such as `actionlint`) for `.github/workflows/*.yml`;
- a schema or load check for `.kyber-weave/kyber-weave.yml` (the reserved review config);
- `shellcheck` on the changed `*.sh` files at task level, not only at review.

Each gate needs a catalogue entry with its `os` restriction and an install path for the hosts
the repository supports. Related: [task-gate-config-review.md](task-gate-config-review.md) and
[task-gate-test-project-mapping.md](task-gate-test-project-mapping.md).

This todo is complete when a task that edits only these files runs at least one applicable gate,
or the reason none should is recorded in `docs/code-review/architecture.md`.
