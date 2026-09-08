# Plan Authoring

The plan file is durable state. A live agent instance is disposable.

## Start or resume

1. Resolve the plans directory and **<plan-index>** through Config Reg, then read the index.
2. Use a prompt-supplied `PLAN_FILE` only when its canonical path remains beneath the plans directory. If none is supplied, create a date-prefixed Draft plan and its index row before broad discovery.
3. Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree.
4. Reconcile conductor-supplied answers against the decision ledger before more discovery.
5. Save after every discovery batch and before every status handoff.

Open only active Draft, Ready, In progress, or Blocked plans. Draft supports planning only. Review required, Completed, Superseded, and archived artifacts are not implementation authority.

## Required plan contract

Every plan contains:

- governed frontmatter including `development-mode: test-first | standard`;
- problem and goal;
- explicitly approved decisions with approval provenance;
- a Draft-only decision ledger;
- investigation findings;
- the mode-specific Test contract or verification contract;
- dispatchable tasks with objective, exact files or symbols, acceptance criteria, dependencies, and required skills;
- a dependency graph and `MAX_CONCURRENCY` audit;
- risks, out-of-scope boundaries, verification gates, review, and `docs-dev` closeout.

Name required skills, not owning agents. The conductor maps skills to the live specialist inventory. Declare a dependency only when a task consumes another task's concrete output or their file scopes cannot safely overlap. Component labels and table order do not create scheduling barriers.

## Decisions

Persist each question with a stable id, options, recommendation, dependency, and `OPEN` or `ANSWERED` status. Return every currently independent question, up to four, in one handoff:

```text
STATUS: NEEDS_DECISION
PLAN_FILE: <saved path>
QUESTION: [Qn] <decision>
OPTIONS: <a> / <b> / ...
RECOMMENDED: <option> — <reason>
```

Do not place an unapproved recommendation in the approved-decisions section. If no question was needed, `PLAN_READY` includes `NO_QUESTIONS` with the concrete reason the request was already decision-complete.

## Ready and finalization

Return `PLAN_READY` only when the saved Draft has no open decisions, contains the selected mode and matching approved contract, has a schedulable dependency graph, and both documentation checks pass:

```text
STATUS: PLAN_READY
PLAN_FILE: <saved path>
OPEN_DECISIONS: none
MAX_CONCURRENCY: <n>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

The conductor presents **approve and execute**. On a `FINALIZE` invocation carrying explicit approval, record that approval, remove the Draft ledger, set the plan and index to Ready, rerun both documentation checks, and return:

```text
STATUS: PLAN_FINALIZED
PLAN_FILE: <saved path>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

A failed write or check returns `STATUS: PLAN_WRITE_ERROR` with the intended path and exact error. Never report a later lifecycle from unsaved state.
