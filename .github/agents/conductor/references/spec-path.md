# Spec Path

Use this path when the user supplies a specification or selects one from the directory named by **<specification-index>**. The specification consists of persisted requirements, design, and task artifacts; those files, not a live specialist instance, are its state.

## Route by persisted state

- **Ready:** begin the task artifact immediately under its recorded `development-mode`.
- **Partial or Draft:** invoke `product-owner` for the earliest incomplete or reopened phase. Pass the feature identity, exact artifact paths, current approval state, selected development mode, and only the user feedback relevant to that phase.
- **Completed, Superseded, or archived:** do not execute it. Ask the user for an active artifact or use intake.

## Headless phase protocol

`product-owner` never prompts the user. It writes only specification artifacts and returns a structured digest. The conductor handles each result:

- `STATUS: REQUIREMENTS_GAP` or `STATUS: DESIGN_GAP` — relay `GAPS` and `OPEN_QUESTIONS`, obtain the user's decision, and resume the named earlier phase.
- `STATUS: READY_FOR_REVIEW` — present the artifact summary and its open questions. Ask for approval or concrete revision feedback, then send the answer back as a new phase invocation.
- `STATUS: PHASE_APPROVED` — advance to the next incomplete phase.
- `STATUS: SPEC_READY` — verify all phases are approved, the task artifact persists `development-mode` and the matching Test or verification contract, and the specification index is synchronized as Draft awaiting final approval.
- `STATUS: SPEC_WRITE_ERROR` or `STATUS: BLOCKED` — relay the exact failure and stop.

After `SPEC_READY`, ask the final blocking question as **approve and execute**. On explicit approval, re-invoke `product-owner` to record the specification as Ready and synchronize the index. Begin execution only after it returns `STATUS: SPEC_FINALIZED` with validation evidence.

## Revisions and mode changes

Revision feedback reopens only the affected phase and all downstream phases. Preserve approved upstream artifacts unless a reported gap requires changing them. Omission of `development-mode` means `test-first`; `standard` requires an explicit user opt-out. A post-approval mode change reopens the task artifact and the affected Test contract or verification contract for user approval.
