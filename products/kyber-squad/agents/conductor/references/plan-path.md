# Plan Path

Use this path when the user supplies a plan or selects one from the directory named by **<plan-index>**.

## Route by lifecycle

- **Ready, In progress, or Blocked:** treat the approved artifact as execution authority. Resume its ready queue at the first incomplete task whose dependencies and file scope permit work. Do not send it back to `architect` merely because `architect` authored it.
- **Draft:** send the exact `PLAN_FILE` to `architect`. Relay its decision digests and return the user's answers keyed by question id. Continue until the saved Draft is decision-complete and validated.
- **Review required, Completed, Superseded, or archived:** do not execute it. Ask the user for an active artifact or route the request through the intake path.

## Draft and approval protocol

The conductor never edits a plan. Require `architect` to persist every decision and return one of its structured markers:

- `STATUS: NEEDS_DECISION` — relay all independent questions, with the specialist's recommended option first.
- `STATUS: BLOCKED` or `STATUS: PLAN_WRITE_ERROR` — relay the evidence and stop.
- `STATUS: PLAN_READY` — verify the saved plan and index agree on Draft, all decisions are resolved, the selected `development-mode` and its Test or verification contract are present, and documentation validation passed.
- `STATUS: PLAN_FINALIZED` — accept only after the user explicitly chooses **approve and execute** and `architect` records the approval, changes the artifact to Ready, synchronizes the index, and revalidates it.

An approval that says only “approve” is sufficient when it clearly answers the presented **approve and execute** gate. Once `PLAN_FINALIZED` is returned, enter the execution contract immediately.

## Mode contract

If the plan omits `development-mode`, have `architect` record `test-first` as the default before the approval gate. `standard` is valid only when the user explicitly opted out of test-first development. If the requested mode changes after the plan was approved, return the plan to Draft and require reapproval of the affected Test contract or verification contract before resuming implementation.
