# Plan Authoring

The plan file is durable state. A live agent instance is disposable.

## Start or resume

1. Resolve the plans directory and **<plan-index>** through Config Reg, then read the index.
2. Use a prompt-supplied `PLAN_FILE` only when its canonical path remains beneath the plans directory. If none is supplied, create a date-prefixed Draft plan and its index row before broad discovery. Every new plan file name starts with today's date as `yyyy-MM-dd-`, even when the name was handed to you: if a supplied `PLAN_FILE` names a plan that does not exist yet and lacks the date, add the date in front, create that file, and return the dated path as `PLAN_FILE`. Never rename a plan that already exists. Change an existing file with an in-place edit, never a whole-file rewrite, whenever the harness gives you an edit tool. On harnesses where file editing requires an existing destination file (such as Devin), initialize the file via shell (for example, `touch <file>`) before editing. A note or excuse for why the process was not followed is not a resolution. The process exists to ensure quality; going around it signals bad quality. Do not put such a note in **Active Plans**. A pull request that does will not be approved.
3. Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree.
4. Reconcile conductor-supplied answers against the decision ledger before more discovery.
5. Save after every discovery batch and before every status handoff. Write each material choice into the Decisions section in the same save as the batch that settled it, and do not reopen a decided row unless new evidence contradicts it: a choice held only in your reasoning is argued again every turn and lost when context is compacted.

## Working order

A plan is written, not thought through and then typed. Reasoning you do not write into the plan is paid for again on every later turn and lost when context is compacted.

1. Send one discovery batch: the code, standards and documents the request lets you name, together.
2. Write the Draft next, from the skeleton below: frontmatter, problem and goal, and a Decisions row for every choice that batch settled. Never open another plan to copy its shape; the skeleton is the shape.
3. Every later turn either sends a batch that answers named open questions or writes answers into the plan. Decide each choice once, in its Decisions row.
4. Size the plan as you write it, and never rewrite a saved section only to shorten it. A Decisions cell is one or two sentences. A finding is a fact no task already states. A risk is one line with its mitigation. Cut any sentence a worker or reviewer would not act on.

Open only active Draft, Ready, In progress, or Blocked plans. Draft supports planning only. Review required, Completed, Superseded, and archived artifacts are not implementation authority.

## Status vocabulary

Frontmatter `status` is `draft` while the plan is Draft and `current` from Ready on; `docs validate` names the closed set when a value is outside it. The bold body status line and the index row carry Draft, Ready and later states. The plan index holds only the plan's table row: write no sentence that restates a plan's status (such as "One active plan: X (Draft)") and never set the index's own frontmatter `status` from a plan's lifecycle. The finalize and archive tools edit the row and nothing else, so any other restatement goes stale and fails `KW-DOC-LIFECYCLE-006`.

## Required plan contract

Every plan contains:

- governed frontmatter including `development-mode: test-first | standard`, `documentation-impact: new-component | extends-component | no-design-change`, and `impact-components` (catalog component names; required and non-empty for `new-component` and `extends-component`);
- a `## Request` section quoting the user's request verbatim, as the dispatch carried it, so every later dispatch and the plan review read it from the plan;
- problem and goal;
- a `## Request trace` table mapping every explicit instruction in the request to the plan section, task or out-of-scope boundary that covers it;
- a permanent Decisions section recording all material choices, alternatives, questions, and approvals;
- the declared scale (`documentation-impact` plus `impact-components`) and a `Design harvest` table keyed by Target component and Target doc type (one row per durable design, contract, interface, behaviour, or operations section; a `no-design-change` scale carries `Documentation waiver: <reason>` instead of rows);
- investigation findings;
- the mode-specific Test contract or verification contract; where an existing test class or fixture already tests the same area, its row names it (one line, "extend `<class>`" or "copy `<class>`") so the worker starts from that example instead of searching for one;
- dispatchable tasks with objective, acceptance criteria, dependencies, capabilities, and its `session` group, naming every file it touches by path plus symbol, the full test class names its runner must match, and exact commands (the rules are below);
- a dependency graph;
- risks, out-of-scope boundaries, verification gates, review, and `docs-dev` closeout.

### Plan skeleton

Fill this in; keep the headings and the table columns exactly.

````markdown
---
id: plans/<yyyy-MM-dd>-<slug>
title: <title>
doc-type: plan
status: draft
component: <catalog component>
owner: <owner or unassigned>
last-reviewed: <yyyy-MM-dd>
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - <catalog component>
---

# <title>

**Status: Draft.**

## Problem and goal
## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
## Scale
## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|

## Design harvest

| Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|

## Investigation findings
## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|

## Tasks

### T1: <name>

- **Objective:** <one sentence>
- **Capabilities:** <abilities in plain words>
- **Difficulty:** easy | medium | hard
- **Technology:** <one declared technology>
- **Session:** `<group>`
- **Depends on:** none | T<n> (<the output it consumes>)
- **Files:**
  - `<path>`: <type or member>, <the change>
- **Commands:**
  - <exact command>
- **Acceptance:**
  - <checkable outcome>

## Dependency graph
## Risks
## Out-of-scope boundaries
## Verification gates
## Review
## docs-dev closeout
````

Where the host declares task gates, a task's **Commands** are the task-gate runs only, exactly as the worker's packet prints them (test-first: the failing run with `--red`, then the passing run; standard mode: the one passing run): the gate runs the Test contract's runner command, so listing that command as a step of its own makes the worker run every test twice.

**Verification gates use what the host already runs.** The plan proves the request with the host's declared gates and tests, including database-backed tests that start the host's local container or test database through its own script: that is the host's procedure, so the plan runs it as given. Never plan a step an autonomous run cannot do alone: a person's confirmation or reservation, an "exclusive window" on a shared environment, a manual check in a browser, or a run that only happens when someone opts in. Where the request asks for proof a gate cannot give, the plan says so in a `PR note:` under out-of-scope boundaries instead of a step that will be skipped.

Every task that changes files carries a `Difficulty:` line and a `Technology:` line.

`Difficulty:` is the architect's rating, one of three values:

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

`Technology:` names exactly one technology the task is written in, chosen from `ontology.technologies`. A change that spans two unrelated technologies is two tasks with a dependency between them, never one task with two technologies: an endpoint and its database migration are separate tasks, the migration first, because one prompt carries one technology's rules and one set of gates. A technology built on another (React on TypeScript) is still one task in the child; the parent's rules arrive with the child's.

Each task carries a `Capabilities:` line: the abilities its work needs, in plain words (for example "React and TypeScript UI with Playwright tests" or "documentation"). Never name an agent or a skill for it: the conductor picks the agent whose description fits. Declare a dependency only when a task consumes another task's concrete output or their file scopes cannot safely overlap. Component labels and table order do not create scheduling barriers. Maximize parallel execution and err on the side of risk: prefer optimistic file scopes and add a dependency only for a real consumed output or a certain file overlap. Size each task for efficient execution by a single worker; do not split work further just to add parallelism. A chain of small tasks in one technology, each consuming the one before (a repository method, then the service and endpoint that call it), is one task: every extra worker pays again to load context, and a dependent task cannot start until the first finishes. Four small file edits belong to one worker; changes of substantial size across independent projects belong to separate workers. Tasks needing different abilities can run at the same time. Split changes across several projects into separate tasks only if each project's change is large enough and decoupled enough to justify it. Respect `review.max-concurrent-workers` (default 4, host-configurable in `.kyber-weave/kyber-weave.yml`); the harness runs ready tasks up to that cap. (Q4 reversal, issue #386: this supersedes the 2026-10-14 "no max concurrency" directive.) Each developer task ends with the worker's own `task-gate` run, and a failing gate goes straight back to the same worker; no agent reviews a single task.

A plan never carries the review command. The end-of-run council derives it from the `code-review` skill — measured against the change with `--base`, and admitted to the full-suite gates with `--council` — so a command copied into a plan, an index row or a task packet goes stale the moment the gate list or one of its flags moves, and the reviewer then runs something nobody validated. Say who reviews and which skill governs it; leave the argv to the reviewer.

## Documented rules over existing code

Before writing a task, fetch the standard for the one technology it names with `docs_get_coding_standard_for_technology`, using the Config Reg's standards list. Never read a standards file directly unless that tool is missing or reports a failure; say which in the plan's findings. The tool returns the technology's rules, skills and standard together, and a file read can miss the rules and the skills: a repository or query file falls under the data-access and SQL standards as well as the language standard. Ask about the pattern the task will copy. When the standard forbids that pattern, the task follows the standard. When the standard names a target the code does not have yet (one a todo tracks), decide it now: record a Decisions row and write the sanctioned exception into the task text, so the worker is never the first to find the conflict.

When existing code contradicts a documented rule or standard (where a kind of file goes, a layering or naming rule), new work follows the documented rule, not the surrounding code: never copy the divergent pattern forward, and name the documented location in the task. This holds for every new file's location even when all of its existing siblings sit elsewhere and following them looks cheaper: the end-of-run review reports a new file placed against the standard as a major finding, which costs a fix round, so the documented location is also the fast choice. It is never a low-confidence decision. Existing code that carries a comment marking it as a deliberate exception is respected. Record each other divergence you find, with the rule and the existing offending paths, in the investigation findings, then decide by the size of the debt: when fixing it is small and touches the same files, add it to a task in this plan; otherwise list it under the plan's out-of-scope boundaries as `PR note: <paths> — <the rule they break>` (and name it in a `RESIDUAL` row of the Closeout mapping when closeout leaves it open); the conductor carries it to the pull request notes (the `change-scope` skill).

## The request's explicit instructions

A value, class name, layout, endpoint shape or check the request states outright is a requirement, not a suggestion. Plan it as written. Where you think it is wrong, keep it and add a risk row saying why; do not replace it with the option you prefer.

Before `PLAN_READY`, trace the request to the plan in a `## Request trace` table: one row for each explicit instruction, naming the plan section, task or out-of-scope boundary that covers it and the evidence that it is met. A row that covers nothing is an instruction you have not planned. A request that asks for proof (for example that values reach the screen from the database) gets a task or verification step that is actually executed, with its command and evidence, not a mocked stand-in.

When an instruction conflicts with a repository rule or standard, record the conflict as a `## Decisions` row before `PLAN_READY`, with the request, the rule, the recommended resolution and the reason. The user's explicit instruction is the recommended winner unless keeping it would break security or a hard repository ban — a rule the repository states it never permits. Record the row even when you believe the rule should win, and never reconcile the conflict silently inside a task or split the instruction into a substitute the owner did not ask for without that row. A plan the conductor has to send back for a missed instruction or an unrecorded conflict costs a planning round.

## Facts that depend on a version, and existing tests

A decision that says a library class, token or API yields a specific value (a colour, a default, a signature) is checked against the version the repository installs: read the package manifest or lock file once and state the version in the decision row. A value true in another major version is a wrong decision that the worker cannot see and a later task has to find.

Do not write "existing tests stay unchanged" as an absolute. New UI or new rows can make an existing test's locator or fixture ambiguous without changing what it means. Write the rule as: an existing test keeps its assertions; the worker may narrow a locator or a fixture query that the new work made ambiguous, and names each such edit in its report. A worker that would have to change what an existing test asserts escalates.

## Task granularity

A task row is also the source of one worker's prompt, and that worker reads nothing else. So the
plan carries what the prompt needs:

- **A path plus a symbol** for every file a task names: the file's repository-relative path together
  with the type, method or setting inside it. A line number is a hint at most, because it goes stale
  the moment an earlier task edits the same file, and a worker that trusts it edits the wrong place.
- **Full test class names**, fully qualified, because a runner filter that matches no class runs
  zero tests and exits success.
- **The exact commands** the task runs, complete enough to paste into a shell.

A task may also carry `Session: <group>` (default: the task id). Add `Role: <agent>` only when the owner assigned the task to a named agent.

## Session groups

Every task declares a `session` group, and the group is the unit of worker continuity: tasks in one
group run in one worker session, so the worker that made a change makes the follow-on fix to it
rather than paying again to rebuild context it already had.

A session group may continue follow-on fixes and later tasks needing the same abilities on the same change.

## Decisions

The `## Decisions` section is permanent state, preserved intact across all lifecycle transitions (Draft -> Ready -> execution -> closeout -> archive). It must NEVER be deleted upon finalization or archival.

Record all decisions in a structured table:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |

Persist each question with a stable id (for example, D1, D2 or Q1, Q2), the question or architectural choice, the selected answer or decision, mitigating and supporting context (alternatives considered, trade-offs, constraints), and status/approval provenance.

Do not place an unapproved recommendation in the approved answer column until approved. When answered or approved, update the row in place: record the chosen answer, document any mitigating considerations or constraints, and record the explicit approval provenance (for example, `Approved by Hal <date>` or `User decision <date>`).

## Open decisions while drafting

While in Draft, unapproved questions have status `OPEN` or `RECOMMENDED`. Return every currently independent question, up to four, in one handoff:

```text
STATUS: NEEDS_DECISION
PLAN_FILE: <saved path>
QUESTION: [Qn] <decision>
OPTIONS: <a> / <b> / ...
RECOMMENDED: <option> — <reason>
```

If no interactive questions were needed during planning, the plan must still carry the permanent `## Decisions` table documenting the baseline choices, assumptions, and constraints that govern the implementation, and `PLAN_READY` includes `NO_QUESTIONS` with the concrete reason the request was already decision-complete. `NO_QUESTIONS` does not bypass authoring the permanent `## Decisions` table.

### Autonomous drafting

Under `autonomy: autonomous`, decide per the autonomous-decisions reference and return only hard-block escalations. An autonomous hard-block escalation uses this NEEDS_DECISION shape PLUS `ESCALATION: hard_block`, `SOURCES_SEARCHED`, `COST_OF_WRONG` (and `RECOMMENDED`); see [autonomy-decisions.md](autonomy-decisions.md) for the exact block.

## Documentation gates after plan writes

After every plan save, obtain the two checks. Leave `mergeReady` false while the plan is in the active plans folder, and never validate with `--merge-ready`: that mode is the pull-request gate and always rejects an open Draft (`KW-DOC-LIFECYCLE-003`). A shell is not permission to implement.

Fix every finding a check reports in the plan — errors and warnings alike — and re-check until the only findings left are the standards warning period below. The standards warning period (`KW-DOC-SPEC-012`, `KW-DOC-SPEC-013`, `KW-DOC-SKILL-005`, `KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`) is outside your write boundary and does not stop the plan: `docs-dev` clears it at closeout with `kyber-weave docs standards migrate`, committed with the closeout. Do not list it as a risk, an accepted warning or out of scope. A finding outside your write boundary that `RUN_BASE` already had (the file it names is unchanged since `RUN_BASE`, by `git diff RUN_BASE -- <file>`) is pre-existing and does not stop the plan either, whatever its code: list it on a `DOCS_PRE_EXISTING:` line and `docs-dev` clears it at closeout. Any other finding outside your write boundary is `STATUS: BLOCKED`, and then name the finding, why you cannot fix it, and who can.

### With a shell

Run `kyber-weave docs validate .` and `kyber-weave docs drift .` in the checkout that holds your plan (the worktree when the run uses one) and report `DOCS_VALIDATE: pass (cli, <path>)`. You may use the kw-docs MCP tools `docs_validate_governed_document_schema` and `docs_detect_drift_between_documents_and_code` instead, only in the canonical checkout and only when each response has `rev=` and `branch=` equal to your `RUN_BASE` and `RUN_BRANCH` and `repo=` equal to `git rev-parse --git-common-dir` in your checkout. `repo=` is the git directory, so a different value is another repository, and a matching one proves nothing alone because every worktree shares it. On any mismatch, or when the result is missing, run the CLI commands.

### With no shell

The MCP result cannot be checked against your checkout, so do not rely on it. Use a conductor-supplied `DOCS_CHECKS: <results>`; with none, return `STATUS: BLOCKED` and name the gap.

## Ready

Return `PLAN_READY` only when the saved Draft has no open decisions (all rows in `## Decisions` have status `ANSWERED` or approved), has a `## Request trace` row for every explicit instruction in the request, contains the selected mode and matching approved contract, declares its scale (`documentation-impact` with `impact-components`) and its `Design harvest` table, has a schedulable dependency graph, and both documentation checks pass (no error; the standards warning period and `DOCS_PRE_EXISTING` findings do not count):

```text
STATUS: PLAN_READY
PLAN_FILE: <saved path>
OPEN_DECISIONS: none
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
DOCS_PRE_EXISTING: <code file, one per finding; or none>
```

The conductor presents **approve and execute**, stating the declared scale and components so the owner confirms scale before execution. Proceed at high confidence only when the scale and harvest targets are unambiguous.

## Finalization

The conductor normally finalizes with `docs_finalize_approved_plan_and_write_task_file` (the CLI twin is `kyber-weave docs finalize`), which records the approval, sets the plan and index to Ready and writes the task file, and refuses, changing nothing, on an open decision or a plan the task file cannot be written from. Its refusal comes back to you as a Draft revision. You finalize only on a `FINALIZE` invocation, the conductor's fallback.

On a `FINALIZE` invocation carrying explicit approval, record that approval in the decision rows, preserve the permanent `## Decisions` section intact (do NOT delete it), set the plan and index to Ready, save the task file beside the plan and return its path, rerun both documentation checks, and return:

```text
STATUS: PLAN_FINALIZED
PLAN_FILE: <saved path>
TASK_FILE: <saved tasks.yml path>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

### Task file

At finalization the architect saves the task file beside the plan, `<plan>.tasks.yml` in the
directory named by **<plan-index>**, returns its path, and the finalize tool commits it with the plan.

It carries a schema version, the plan's path, the execution digest, and one entry per task: id,
capabilities, dependencies, file scope, `session` group and the full prompt. The command writes all of it. The execution digest covers the
parsed tasks, file scopes, dependencies, test rows and mode, so flipping a task's status or
editing a decisions row leaves it valid while editing a task makes it stale.

Two slots stay unfilled — `{worktree}` and `{prior_results}` — because no plan can know the worktree
the run picks or what an earlier worker reported. Everything else in a prompt is final when the file
is written.

Finalization runs in this order:

1. Make every plan edit first: the approval rows, the bold body status line, the frontmatter status (`current`) and the index row.
2. Run `kyber-weave docs tasks <plan file> --write` in the checkout that holds the plan. It derives the whole file from the plan: file scopes, dependencies, capabilities, the execution digest, and each prompt from the task's own text plus its command and acceptance criteria. When it refuses — a task lacks a command, acceptance criteria or capabilities, a `Depends on` token names no task, or a task's Files list names a path under the scratch folder `.agent_scratchpad/` — it names the task and what to add or correct in the plan; fix the plan and run it again. Run `kyber-weave docs tasks <plan file> --check` before `PLAN_READY` and fix every `KW-DOC-TASKS-008` to `-011` warning it prints: each names a task whose packet cannot be understood without the plan, or a worker who cannot open the plan.
3. Run both documentation checks. Any `KW-DOC-TASKS-*` finding is fixed by changing the plan and repeating step 2, never by editing the file.

Never write, edit or compute the task file or its digest by hand, with a script, or through a sibling such as a `.new` or `.tmp` copy. An edit to a task, a test contract row, an out-of-scope path or the development mode after step 2 means repeating step 2; a status or Decisions edit does not. `kyber-weave docs tasks <plan file> --check` exits 1 and says what differs without writing.

An architect with no shell cannot run step 2. It returns `STATUS: BLOCKED` naming `task file: run kyber-weave docs tasks <plan file> --write in <checkout>`, and continues when it is re-invoked.

## Write errors

A failed write or check returns `STATUS: PLAN_WRITE_ERROR` with the intended path and exact error. Never report a later lifecycle from unsaved state.

## Planning inside a worktree

The docs server is bound to the canonical checkout, so it cannot see a plan written into another worktree: its answers would validate the wrong corpus and report a pass for a file it never read. When your packet names `WORKTREE: <absolute path>`, write the plan under the directory named by **<plan-index>** inside that worktree and run `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` there in your own shell; report `DOCS_VALIDATE: pass (cli, <path>)`. Only when your harness gives you no shell, return `STATUS: BLOCKED` naming `server root differs: run the docs checks in the worktree`, so the conductor runs them and re-invokes you with `DOCS_CHECKS: <results>`. With no `WORKTREE` in the packet and a `repo=` that differs, the repository is not yours to plan: return `STATUS: BLOCKED`.
