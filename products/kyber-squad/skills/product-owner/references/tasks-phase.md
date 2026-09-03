# Tasks Phase

Author the implementation tasks for one feature specification from approved requirements and design.

## Inputs and path

Resolve the active specification directory through **<specification-index>**. Read approved `requirements.md` and `design.md`; write `<active-spec-directory>/{feature-name}/tasks.md`.

The conductor supplies `DEVELOPMENT_MODE`. If omitted, record `test-first`. Record `standard` only with the conductor's explicit user opt-out. A mode change after approval reopens this phase and its contract.

## Work

Produce a numbered checkbox task list with at most two hierarchy levels. Every task includes a concrete objective, exact files or components, acceptance criteria, dependencies, required skills, and requirement ids. Tasks must be executable by a cold worker without design decisions.

Persist:

```markdown
# Implementation Tasks

**Phase status:** Draft
**Development mode:** test-first
```

### Test-first mode

Include a Test contract for every implementation task: exact test project or file, runner command, observable behavior, expected RED reason, and GREEN acceptance. Sequence RED → GREEN → REFACTOR; implementation cannot start before valid RED evidence exists.

### Standard mode

Include a verification contract for every implementation task: exact automated test surface and command plus any build, analyzer, package, deployment, or read-only integration evidence. Sequence implementation → verification → review. Historical RED evidence is not required, but appropriate automated tests and current evidence remain mandatory.

Mark any genuinely no-test task explicitly and name its replacement validation. A contract change after approval requires reapproval.

## Gaps and coverage

Do not invent missing design. If design blocks task decomposition, return `STATUS: DESIGN_GAP`; if the cause is a missing requirement, say so in `GAPS`. Every requirement id must be covered by at least one task.

## Final task

End with a task assigned through the conductor to `docs-dev`: verify requirements against implementation and review evidence, migrate durable content into canonical documentation, update the specification index, and archive the specification. This closeout depends on all implementation, verification, and final-council work.

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

On conductor-supplied explicit approval, record `Phase status: Approved` and return `STATUS: PHASE_APPROVED`. When all three phases are approved, synchronize the spec index as Draft awaiting final approval and return `STATUS: SPEC_READY`. Only a later finalization invocation carrying explicit **approve and execute** authorization records Ready and returns `STATUS: SPEC_FINALIZED`.
