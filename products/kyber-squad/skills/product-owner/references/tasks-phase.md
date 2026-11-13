# Tasks Phase

Author the implementation tasks for one feature specification from approved requirements and design.

## Inputs and path

Resolve the active specification directory through **<specification-index>**. Read approved `requirements.md` and `design.md`; write `<active-spec-directory>/{feature-name}/tasks.md`.

The conductor supplies `DEVELOPMENT_MODE`. If omitted, record `test-first`. Record `standard` only with the conductor's explicit user opt-out. A mode change after approval reopens this phase and its contract.

## Work

Produce a numbered checkbox task list with at most two hierarchy levels. Group tasks under `###` headings, and use checkbox items only for dispatchable tasks, because every checkbox item needs its own task-file entry. Every task includes a concrete objective, exact files or components, acceptance criteria, dependencies, required skills, and requirement ids. Tasks must be executable by a cold worker without design decisions.

Persist:

```markdown
# Implementation Tasks

**Phase status:** Draft
**Development mode:** test-first
```

### Test-first mode

Each implementation task is one unit of behaviour that one specialist owns, tests and code together; never split it into a test task and an implementation task, or add a stub task so another task's tests compile. Give each task a Test contract row: exact test project or file, runner command, observable behavior, the failing run's expected reason, and passing acceptance. The worker runs the tests failing before implementing, in the same task.

### Standard mode

Include a verification contract for every implementation task: exact automated test surface and command plus any build, analyzer, package, deployment, or read-only integration evidence. Sequence implementation → verification → review. The failing run is not required, but appropriate automated tests and current evidence remain mandatory.

Mark any genuinely no-test task explicitly and name its replacement validation. A contract change after approval requires reapproval.

## Task file

Beside `tasks.md`, the tracked task file `<spec-folder>/tasks.json` is the specification form of the same contract a plan uses, and the conductor writes it with `kyber-weave docs tasks` before finalization (see below). It carries a schema version, the specification's path, the execution digest, and one entry per task: id, role, dependencies, file scope, `session` group and the full prompt, each prompt naming files by path plus symbol, the full test class names and the exact commands.

Only two slots stay unfilled — `{worktree}` and `{prior_results}` — because the conductor fills them at dispatch from what no authoring phase can know.

The conductor writes the file with `kyber-weave docs tasks <spec-folder>/tasks.md --write` before the finalization invocation; it derives every field, the digest and each prompt from `tasks.md`, so `product-owner` never writes, edits or computes `tasks.json`. Put what each prompt needs in the task itself: its `Command:` line (a backticked program and argument) and its `Acceptance criteria:` line, or the test contract row's runner and acceptance cells. Optional `Session:` and `Role:` lines set the worker session group and agent (default: the task id and the first required skill).

Finalization runs in this order:

1. Check that `tasks.json` exists beside `tasks.md`. If not, return `STATUS: BLOCKED` naming `task file: run kyber-weave docs tasks <spec-folder>/tasks.md --write in <checkout>`. Nothing else is written, so the spec is not left Ready without a task file.
2. Record the approval rows, write the spec Ready line (`**Status:** Ready`) in `tasks.md`, and synchronize the index. A status edit does not move the digest.
3. Return `SPEC_FINALIZED` with `TASK_FILE: <spec-folder>/tasks.json`.

## Gaps and coverage

Do not invent missing design. If design blocks task decomposition, return `STATUS: DESIGN_GAP`; if the cause is a missing requirement, say so in `GAPS`. Every requirement id must be covered by at least one task.

## Final task

End with a task assigned through the conductor to `docs-dev`: verify requirements against implementation and review evidence, harvest the Design harvest declared at authoring time at its declared scale, migrate durable content into canonical documentation, update the specification index, and archive the specification. This closeout depends on all implementation, verification, and final-council work.

## Digest

```text
STATUS: READY_FOR_REVIEW | DESIGN_GAP
PHASE: tasks
ARTIFACT: <resolved tasks path>
SUMMARY: <task count, sequencing, and coverage>
GAPS: <blocking gaps, or none>
OPEN_QUESTIONS: <items for conductor to relay, or none>
DEVELOPMENT_MODE: test-first | standard
CONTRACT: <Test contract | verification contract>
COVERAGE: <all requirement ids covered, or list gaps>
```

On conductor-supplied explicit approval, record `Phase status: Approved` and return `STATUS: PHASE_APPROVED`. When all three phases are approved, synchronize the spec index as Draft awaiting final approval and return `STATUS: SPEC_READY`. Only a later finalization invocation carrying explicit **approve and execute** authorization records Ready and returns `STATUS: SPEC_FINALIZED`. `SPEC_FINALIZED` also returns `TASK_FILE: <spec-folder>/tasks.json`.

## Consolidated review recording

When the conductor presents a consolidated review for a tasks `STATUS: READY_FOR_REVIEW` with `GAPS: none` and `OPEN_QUESTIONS: none`, this phase permits the combined recording: one presentation carries both the tasks approval and the execute approval as two AUTONOMOUS_APPROVAL rows. The conductor supplies each approval explicitly; the owner records them and returns the normal `STATUS: PHASE_APPROVED` / `STATUS: SPEC_FINALIZED` progression. The collapsed invocation that carries both approvals also carries `SPEC_DIGEST`, and the task file is written in that same invocation.
