# Tasks Phase

Author the implementation tasks for one feature specification from approved requirements and design.

## Inputs and path

Resolve the active specification directory through **<specification-index>**. Read approved `requirements.md` and `design.md`; write `<active-spec-directory>/{feature-name}/tasks.md`.

The conductor supplies `DEVELOPMENT_MODE`. If omitted, record `test-first`. Record `standard` only with the conductor's explicit user opt-out. A mode change after approval reopens this phase and its contract.

## Work

Produce a numbered checkbox task list with at most two hierarchy levels. Group tasks under `###` headings, and use checkbox items only for dispatchable tasks, because every checkbox item needs its own task-file entry. Every task includes a concrete objective, exact files or components, acceptance criteria, dependencies, a `Capabilities:` line naming in plain words the abilities the work needs (never an agent or skill name), and requirement ids.

Every task that changes files also carries two lines. `Difficulty:` rates the task. `easy` means the worker copies a pattern the plan names. Every change is stated in the plan and an existing pattern covers it, in however many files: a repository query like its neighbours, a controller action like the one beside it, an authorization attribute the neighbouring endpoints already use, a component like an existing card. `medium` means the worker makes a local design choice the plan leaves open, in code that has patterns to follow, and an ordinary test in one process, with fakes for the network or clock, shows whether the choice is right. Anchors: cursor-based pagination, so a live feed neither skips nor repeats items inserted mid-scroll; role-based access control enforced in the query, so users see and edit only their own team's or organisation's rows; retries with exponential backoff and jitter, so a recovering server is not hit by synchronised retries; a background job with a dead-letter queue, so a job that keeps failing is held for inspection instead of lost or retried forever; end-to-end request tracing with one correlation ID through the frontend, API and database logs; a screen's state handling; a new validation rule. `hard` means correctness depends on what happens under retries, concurrent requests or a crash part-way through, across processes or machines, which an ordinary test cannot show; or the worker designs a security mechanism rather than applying one. Anchors: an idempotent webhook or event handler, so a duplicate or out-of-order retry never charges twice; preventing two simultaneous requests from overwriting shared data when clocks drift and locks expire mid-operation; an atomic dual write (a transactional outbox), so a crash never leaves the database and the message queue out of step; a saga with compensating steps; cache invalidation (cache busting); a new authentication or trust-boundary scheme, secrets handling or cryptography; a migration of existing data. Applying an existing pattern of any of these is `easy` or `medium`, never `hard`. A task with no line is `medium`. Ask one question to rate a task: could an ordinary test in one process prove the change right? If yes, it is `easy` or `medium`; only when the answer depends on timing, retries or partial failure, or on a security design, is it `hard`. Rate the decisions the worker must make, not the size: the number of files or layers does not by itself raise the rating. When unsure between two ratings, pick the lower, because a failed attempt moves the task one tier up. Most tasks in a typical feature are `easy`: split the work so each task follows one existing pattern the plan names, and say which file to copy. `Technology:` names exactly one technology from `ontology.technologies`. Work that spans two unrelated technologies is two tasks with a dependency — an endpoint and its database migration are separate tasks, the migration first — because one prompt carries one technology's rules and gates. A technology built on another (React on TypeScript) stays one task, and the parent's rules arrive with it.

Read a technology's standard through `docs_get_coding_standard_for_technology`, for the one technology a task names. Read a standards file directly only when that tool is missing or reports a failure, and say so. Tasks must be executable by a cold worker without design decisions.

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

Beside `tasks.md`, the tracked task file `<spec-folder>/tasks.yml` is the specification form of the same contract a plan uses, and the conductor writes it with `kyber-weave docs tasks` before finalization (see below). It carries a schema version, the specification's path, the execution digest, and one entry per task: id, role, dependencies, file scope, `session` group and the full prompt, each prompt naming files by path plus symbol, the full test class names and the exact commands.

Only two slots stay unfilled — `{worktree}` and `{prior_results}` — because the conductor fills them at dispatch from what no authoring phase can know.

The conductor writes the file with `kyber-weave docs tasks <spec-folder>/tasks.md --write` before the finalization invocation; it derives every field, the digest and each prompt from `tasks.md`, so `product-owner` never writes, edits or computes `tasks.yml`. Put what each prompt needs in the task itself: its `Command:` line (a backticked program and argument) and its `Acceptance criteria:` line, or the test contract row's runner and acceptance cells. An optional `Session:` line sets the worker session group (default: the task id); add `Role: <agent>` only when the owner assigned the task to a named agent.

Finalization runs in this order:

1. Check that `tasks.yml` exists beside `tasks.md`. If not, return `STATUS: BLOCKED` naming `task file: run kyber-weave docs tasks <spec-folder>/tasks.md --write in <checkout>`. Nothing else is written, so the spec is not left Ready without a task file.
2. Record the approval rows, write the spec Ready line (`**Status:** Ready`) in `tasks.md`, and synchronize the index. A status edit does not move the digest.
3. Return `SPEC_FINALIZED` with `TASK_FILE: <spec-folder>/tasks.yml`.

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

On conductor-supplied explicit approval, record `Phase status: Approved` and return `STATUS: PHASE_APPROVED`. When all three phases are approved, synchronize the spec index as Draft awaiting final approval and return `STATUS: SPEC_READY`. Only a later finalization invocation carrying explicit **approve and execute** authorization records Ready and returns `STATUS: SPEC_FINALIZED`. `SPEC_FINALIZED` also returns `TASK_FILE: <spec-folder>/tasks.yml`.

## Consolidated review recording

When the conductor presents a consolidated review for a tasks `STATUS: READY_FOR_REVIEW` with `GAPS: none` and `OPEN_QUESTIONS: none`, this phase permits the combined recording: one presentation carries both the tasks approval and the execute approval as two AUTONOMOUS_APPROVAL rows. The conductor supplies each approval explicitly; the owner records them and returns the normal `STATUS: PHASE_APPROVED` / `STATUS: SPEC_FINALIZED` progression. The collapsed invocation that carries both approvals also carries `SPEC_DIGEST`, and the task file is written in that same invocation.
