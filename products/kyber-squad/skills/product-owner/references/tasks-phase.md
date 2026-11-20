# Tasks Phase

Author the implementation tasks for one feature specification from approved requirements and design.

## Inputs and path

Resolve the active specification directory through **<specification-index>**. Read approved `requirements.md` and `design.md`; write `<active-spec-directory>/{feature-name}/tasks.md`.

The conductor supplies `DEVELOPMENT_MODE`. If omitted, record `test-first`. Record `standard` only with the conductor's explicit user opt-out. A mode change after approval reopens this phase and its contract.

## Work

Produce a numbered checkbox task list with at most two hierarchy levels. Group tasks under `###` headings, and use checkbox items only for dispatchable tasks, because every checkbox item needs its own task-file entry. Every task includes a concrete objective, exact files or components, acceptance criteria, dependencies, a `Capabilities:` line naming in plain words the abilities the work needs (never an agent or skill name), and requirement ids.

Every task that changes files also carries two lines. 

Every file-changing task carries a `Difficulty:` line. The values are still spelled `easy`, `medium` and `hard`; read them as **standard**, **intermediate** and **advanced**. The tier says how much can go wrong that a worker would not see from an ordinary test, not how much work the task is.

**Standard (`easy`) is the default: about eight tasks in ten.** It is idiomatic production code in one technology, however large: endpoints, multi-table queries, UI state, validation, business rules, and any pattern the codebase already has. If it is wrong, a normal test fails or a user sees it at once. Build the simplest clean solution; add no queue, lock, cache or defensive layer the task does not need.

**Intermediate (`medium`): the happy path works, but the code fails at scale, across tenants, or when the other side misbehaves, unless a known defensive pattern is applied.** Ask: what happens when there is a lot of it, many users or tenants share it, or a dependency is slow or flaky? Contrast pairs:
- A paged admin list over a table that rarely changes is standard. An infinite-scroll feed while new rows keep arriving is intermediate: page numbers skip and repeat items, so it needs a cursor.
- Checking a role on an endpoint, like the endpoints beside it, is standard. Making every query return only the caller's own team's or organisation's rows is intermediate.
- Calling an internal service the app already calls is standard. Calling a third-party API that drops requests is intermediate: retries need exponential backoff with jitter, or a recovering server is flattened.
- Sending one email inside the request is standard. A bulk export or mass email is intermediate: it moves to a background job, and a job that keeps failing goes to a dead-letter queue instead of being lost or retried forever.
- Logging an error is standard. Following one user's request through the frontend, API and database logs is intermediate: one correlation ID travels through all of them.

**Advanced (`hard`): a mistake silently corrupts data, leaks access, or charges money, and shows up only under timing, retries, a crash part-way through, or clock and timezone edges, so no ordinary test run reveals it.** Ask: can this go wrong in a way no test will show, and would it cost money, data or trust? Contrast pairs:
- Saving an order is standard. Handling a payment webhook that can arrive twice or out of order is advanced: it needs an idempotency key.
- Updating a counter in one request is standard. Two buyers reserving the last item at the same moment, on different servers, is advanced: it needs locking that survives clock drift and expired locks.
- Writing a row is standard. Writing a row and publishing the event billing depends on is advanced: a crash between the two leaves them out of step, so it needs a transactional outbox.
- A form that saves in one step is standard. A booking across several services that must undo earlier steps when a later one fails is advanced: a saga with compensating steps.
- Reading cached settings is standard. Keeping several cache layers consistent when data changes is advanced: cache busting.
- Adding the authorization attribute the neighbouring endpoints use is standard. Building sign-in with SSO, OAuth or OIDC, or the token lifecycle, is advanced.
- Adding a nullable column is standard. Renaming or reshaping a column on a live table without downtime is advanced: an expand-and-contract migration.
- Showing a date is standard. A recurring schedule across timezones and daylight-saving changes is advanced: store UTC plus the IANA zone rule.

These pairs teach the line; they are not the whole list. Rate a task by which side of the line it falls on, by the same reasoning, even when nothing above names it. When the codebase already has a proven implementation of an intermediate or advanced pattern and the task reuses it as it is, the task is standard.

For an advanced task, the plan does the design before any worker starts: state the failure mode in one line and the mitigation in three to five bullets, in the task itself, so the worker builds what is already decided.

Rate the decisions the worker must make, not the size: the number of files or layers does not by itself raise the rating. Split the work so each task follows one existing pattern the plan names, in one technology, and say which file to copy. When unsure between two tiers, pick the lower, because a failed attempt moves the task one tier up.

`Technology:` names exactly one technology from `ontology.technologies`. Work that spans two unrelated technologies is two tasks with a dependency — an endpoint and its database migration are separate tasks, the migration first — because one prompt carries one technology's rules and gates. A technology built on another (React on TypeScript) stays one task, and the parent's rules arrive with it.

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
