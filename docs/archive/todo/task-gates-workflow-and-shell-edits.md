---
id: todo/task-gates-workflow-and-shell-edits
title: Add task gates that cover workflow YAML, the review config and shell scripts
doc-type: todo
component: ReviewCouncil
status: archived
owner: dpalfery
last-reviewed: 2026-11-08
documentation-impact: extends-component
impact-components: [ReviewCouncil]
---

# Add task gates that cover workflow YAML, the review config and shell scripts

> [!NOTE]
> **Closed 2026-11-08.** Each of the three file kinds now has a gate whose `include` matches it, in
> this repository and in the shipped catalogue: `workflow-lint` (`actionlint`) for
> `.github/workflows/*.yml`, `shellcheck` for `.sh`, and `review-config`
> (`kyber-weave review catalogue .`) for `.kyber-weave/kyber-weave.yml`. `review-config` is
> `docs-dev`'s first task gate and is aliased under `github-devops` and `sql-database-architect`;
> `github-devops` also carries `sql-format` for the migrations `sql-database-architect` edits. The
> catalogue's copies live in the csharp family and are written for the two agents that map to the
> `github-actions` technology, each with `os: unix` and a `sources` install page, so a host that
> declares no such technology receives none of them.
> `TaskGatesRepositoryConfigTests.ATaskThatEditsOnlyThatFileKindSelectsAtLeastOneGate` pins the
> condition stated below by file kind rather than by agent, so the next file kind to appear is a
> failing test instead of a silently unchecked change. The completion condition of this todo — a
> task that edits only these files runs at least one applicable gate — therefore holds, and nothing
> needed to be recorded against it in the [review council
> architecture](../../code-review/architecture.md#task-gates).

During the [test process storm guard plan](../../archive/plans/2026-10-30-test-process-storm-guard.md),
the task gate reported `NONE_APPLICABLE` (every gate skipped) for a task that edited only
`.github/workflows/ci.yml`, `.kyber-weave/kyber-weave.yml` and `scripts/*.sh`, and for the first
run of a .NET test-only task. Nothing deterministic checked those edits at the task boundary.
The only checks were the tests that read those files (for example `CiLeftoverStepTests`,
`ProcGuardScriptTests`, the review-config pin tests) and `shellcheck` at the whole-change level.

The question this todo asked — which deterministic gates the task gate should run for these file
kinds — is answered above. Related: [after the .NET gate set](task-gate-config-review.md)
and [mapping a task to its test project](../../todo/task-gate-test-project-mapping.md).

## Acceptance criteria

- [x] A workflow linter covers `.github/workflows/*.yml` — `workflow-lint` runs
      `actionlint "{changed}"`.
- [x] A schema or load check covers `.kyber-weave/kyber-weave.yml` — `review-config` runs
      `kyber-weave review catalogue .`, which loads the file through the same loader the gates use
      and fails with `KW-CONFIG-001` when it cannot.
- [x] `shellcheck` runs on the changed `*.sh` files at task level, not only at review.
- [x] Each gate has a catalogue entry with its `os` restriction and an install path for the hosts
      the repository supports — `os: unix`, and a `sources` URL in the family file.
- [x] A task that edits only these files runs at least one applicable gate, or the reason none
      should is recorded — the gates exist, so nothing needed recording as an exception.

## Decisions

| ID | Question | Answer / decision | Where it is recorded |
|---|---|---|---|
| TG1 | Which gate covers a workflow YAML change? | `workflow-lint` — `actionlint` over `.github/workflows/*.yml` and `*.yaml`. Declared under `github-devops` in this repository, and shipped in the catalogue for a host that declares the `github-actions` technology. | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) |
| TG2 | Which gate covers a shell script change? | `shellcheck` — over `scripts/*.sh` and `scripts/**/*.sh` in this repository, `**/*.sh` in the catalogue. | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) |
| TG3 | How is the review configuration checked at task level? | `review-config` runs `kyber-weave review catalogue .`. Loading the file *is* the check: a configuration the product cannot read declares gates that never run. | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) |
| TG4 | Does a task that edits only these files now select a gate? | Yes. `include` alone decides selection, and each kind has a glob that matches it; the condition is pinned by file kind, so a new kind fails a test rather than passing unnoticed. | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) |
| TG5 | Do the catalogue copies carry an OS restriction and an install path? | Yes: `os: unix`, and the family's `sources` map carries the download or install page for the tool. A gate skipped outside the restriction is reported (`KW-REVIEW-026`), never a pass. | [Review council architecture: the shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) |

## Design harvest

| ID | Target component | Target doc type | Target doc | Durable item |
|---|---|---|---|---|
| DH2 | ReviewCouncil | architecture | [Review council architecture](../../code-review/architecture.md#task-gates) | `include` alone decides whether a task gate is selected, so a file kind with no matching glob is a change nothing checked; every file kind this repository's agents edit has a gate. |

## Closeout mapping

| ID | Destination | Impact component | Last-reviewed | Status |
|---|---|---|---|---|
| TG1, TG2, TG3, TG4, DH2 | [Review council architecture: task gates](../../code-review/architecture.md#task-gates) | ReviewCouncil | 2026-11-08 | archived |
| TG5 | [Review council architecture: the shipped gate catalogue](../../code-review/architecture.md#the-shipped-gate-catalogue) | ReviewCouncil | 2026-11-08 | archived |
