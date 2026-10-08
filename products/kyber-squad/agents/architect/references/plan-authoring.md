# Plan Authoring

The plan file is durable state. A live agent instance is disposable.

## Start or resume

1. Resolve the plans directory and **<plan-index>** through Config Reg, then read the index.
2. Use a prompt-supplied `PLAN_FILE` only when its canonical path remains beneath the plans directory. If none is supplied, create a date-prefixed Draft plan and its index row before broad discovery. On harnesses where file editing requires an existing destination file (such as Devin), initialize the file via shell (for example, `touch <file>`) before editing.
3. Reconcile body status, frontmatter lifecycle, and index status in the same save. The body status is authoritative when they disagree.
4. Reconcile conductor-supplied answers against the decision ledger before more discovery.
5. Save after every discovery batch and before every status handoff.

Open only active Draft, Ready, In progress, or Blocked plans. Draft supports planning only. Review required, Completed, Superseded, and archived artifacts are not implementation authority.

## Required plan contract

Every plan contains:

- governed frontmatter including `development-mode: test-first | standard`;
- problem and goal;
- a permanent Decisions section recording all material choices, alternatives, questions, and approvals;
- investigation findings;
- the mode-specific Test contract or verification contract;
- dispatchable tasks with objective, exact files or symbols, acceptance criteria, dependencies, and required skills;
- a dependency graph and `MAX_CONCURRENCY` audit;
- risks, out-of-scope boundaries, verification gates, review, and `docs-dev` closeout.

Name required skills, not owning agents. The conductor maps skills to the live specialist inventory. Declare a dependency only when a task consumes another task's concrete output or their file scopes cannot safely overlap. Component labels and table order do not create scheduling barriers.

## Decisions

The `## Decisions` section is permanent state, preserved intact across all lifecycle transitions (Draft -> Ready -> execution -> closeout -> archive). It must NEVER be deleted upon finalization or archival.

Record all decisions in a structured table:

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |

Persist each question with a stable id (for example, D1, D2 or Q1, Q2), the question or architectural choice, the selected answer or decision, mitigating and supporting context (alternatives considered, trade-offs, constraints), and status/approval provenance.

While in Draft, unapproved questions have status `OPEN` or `RECOMMENDED`. Return every currently independent question, up to four, in one handoff:

```text
STATUS: NEEDS_DECISION
PLAN_FILE: <saved path>
QUESTION: [Qn] <decision>
OPTIONS: <a> / <b> / ...
RECOMMENDED: <option> — <reason>
```

Do not place an unapproved recommendation in the approved answer column until approved. When answered or approved, update the row in place: record the chosen answer, document any mitigating considerations or constraints, and record the explicit approval provenance (for example, `Approved by Hal <date>` or `User decision <date>`).

If no interactive questions were needed during planning, the plan must still carry the permanent `## Decisions` table documenting the baseline choices, assumptions, and constraints that govern the implementation, and `PLAN_READY` includes `NO_QUESTIONS` with the concrete reason the request was already decision-complete. `NO_QUESTIONS` does not bypass authoring the permanent `## Decisions` table.

## Ready and finalization

Return `PLAN_READY` only when the saved Draft has no open decisions (all rows in `## Decisions` have status `ANSWERED` or approved), contains the selected mode and matching approved contract, has a schedulable dependency graph, and both documentation checks pass:

```text
STATUS: PLAN_READY
PLAN_FILE: <saved path>
OPEN_DECISIONS: none
MAX_CONCURRENCY: <n>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

The conductor presents **approve and execute**. On a `FINALIZE` invocation carrying explicit approval, record that approval in the decision rows, preserve the permanent `## Decisions` section intact (do NOT delete it), set the plan and index to Ready, rerun both documentation checks, and return:

```text
STATUS: PLAN_FINALIZED
PLAN_FILE: <saved path>
DOCS_VALIDATE: pass
DOCS_DRIFT: pass
```

A failed write or check returns `STATUS: PLAN_WRITE_ERROR` with the intended path and exact error. Never report a later lifecycle from unsaved state.
