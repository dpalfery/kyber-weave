---
id: adr/0063-the-task-file-is-tracked-and-the-plan-stays-authoritative
title: The task file is tracked and the plan stays authoritative
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-13
component: KyberSquad
decided-by: []
supersedes: []
code-refs:
  - PlanDocumentParser
  - OpenWorkValidator
---

# ADR 0063: The task file is tracked and the plan stays authoritative

## Status

Accepted, 2026-11-08. Introduces `KW-DOC-TASKS-001` to `-006` and `KW-DOC-LIFECYCLE-005`, and
`KW-ARB-PLANREAD-001` in [ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md).

## Context

The conductor rewrote a 42 KB plan into **128 KB of prompts across 26 dispatches** on a measured
run, and the prompts still carried too little to act on. The authoring contract asked for "exact
files or symbols" and no commands, so no prompt carried a full test class name or an exact command,
and a worker that could not run the right tests reported a green suite it never ran. The conductor
paid to re-derive every prompt from the plan, and each worker that did open the plan paid again.

Three choices were available:

1. **Point workers at the plan** and let each read the tasks it owns. Cheapest for the conductor and
   worst for the read guard: the whole plan reaches every worker, and the guard that stops a worker
   reading a plan is advisory wherever hooks are.
2. **Move the tasks out of the plan** into a file only workers read. That splits the artifact the
   owner approves from the instructions that execute it, and the plan is what the owner approves.
3. **A tracked task file beside the plan**, whose prompts the architect writes and each worker
   fetches unchanged, with the plan still authoritative.

## Decision

The architect saves `docs/plans/<plan>.tasks.json` at finalization, committed with the plan at
once. It carries a schema version, the plan path, an execution digest, and per task an id, role,
dependencies, file scope, session group and the full prompt. Each prompt carries files as path plus
symbol, full test class names, exact commands, acceptance criteria and the standards to fetch.

The conductor dispatches by task id and never pastes or retypes a prompt. The worker's first
action is `kyber-weave docs packet <task id>`, which checks the file against its plan, prints that
task's prompt **unchanged**, and fills only `{worktree}` and `{prior_results}` — the two facts no plan
can know, because a later prompt once needed a cause an earlier worker found.

*Amended 2026-11-13:* the decision first had the conductor paste each prompt into the dispatch. A
conductor that could not read a 33 KB task file retyped prompts and left a worker blocked, so the
machine now prints the packet.

**The plan stays authoritative.** The owner approves it; it holds the Decisions table; the Arbiter
reads task ids, files, dependencies, skills, task text, test rows and out-of-scope items from it
through `PlanDocumentParser`. Only the conductor stops reading it.

The duplication between plan and file is **by design and checked**, not tolerated: the execution
digest covers the parsed tasks, files, dependencies, skills, test rows, out-of-scope paths and mode,
so a status flip or a decisions-row edit does not mark the file stale while any task edit does. A
file that disagrees with its plan fails (`KW-DOC-TASKS-002`), a stale one fails (`-003`), a prompt
without a command or acceptance criteria fails (`-004`), and a prompt that tells a worker to open a
plan fails (`-005`) — the same detection `KW-ARB-PLANREAD-001` applies at dispatch.

**Removal is part of the lifecycle.** `docs-dev` removes the file with `git rm` in the same commit
that archives the plan, so it never reaches `main`, and `KW-DOC-LIFECYCLE-005` fails `--merge-ready`
when one is left behind, looking under the archive too so a moved specification folder is caught.
Nothing durable is lost: the archived plan keeps the decisions, tasks and test contract.

## Consequences

- A worker no longer needs the plan, so the read guard stops being the only thing standing between
  a worker and 42 KB of context it does not need.
- The architect writes more at finalization and the conductor writes almost nothing per dispatch. Conductor-only
  time was 6.6% of the measured run; the saving is fewer reads by workers, not fewer dispatches.
- Two artifacts must agree, and the digest is what makes that checkable rather than a convention.
  A hand-edited task file fails validation instead of silently diverging.
- The file is transient by design. Squash merges mean it would not survive to `main` anyway; the
  rule makes that explicit rather than accidental.
- The prompt is the contract, so a prompt that asks for a class name that does not exist is now a
  validated failure inside the authoring step rather than a worker discovering it mid-run.
- The first plan of a kind cannot carry its own file: the mechanism is not live at its own
  finalization, so `KW-DOC-TASKS-006` warns on it until it archives, and the removal duty starts
  with the plans delivered after it.

## Related

- [ADR 0053](0053-kyber-arbiter-three-step-decision-gates.md) — decision 23 adds `KW-ARB-PLANREAD-001`, and decision 9 takes the changed-path basis this contract depends on
- [Kyber-Squad architecture](../kyber-squad/architecture.md#the-tracked-task-file-and-the-paste-and-fill-dispatch) — the task file, its digest and the packet the machine delivers
- [Kyber-Squad adoption guide](../kyber-squad/onboarding.md#inlined-task-packets-zero-plan-context-hunting) — what the conductor does with the file at dispatch
- [Plan inventory policy](../plans/README.md#plan-decisions-and-closeout-policy) — the authoring and closeout duties in the plan lifecycle
- [Rule reference](../ci-pipelines/rule-reference.md) — every `KW-DOC-TASKS-*` id and `KW-DOC-LIFECYCLE-005`
