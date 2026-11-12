# Spec Path

Use this path when the user supplies a specification or selects one from the directory named by **<specification-index>**. The specification consists of persisted requirements, design, and task artifacts; those files, not a live specialist instance, are its state.

## Route by persisted state

- **Ready:** begin the task artifact immediately under its recorded `development-mode`.
- **Partial or Draft:** invoke `product-owner` for the earliest incomplete or reopened phase. Pass the feature identity, exact artifact paths, current approval state, selected development mode, and only the user feedback relevant to that phase.
- **Completed, Superseded, or archived:** do not execute it. Ask the user for an active artifact or use intake.

## Headless phase protocol

`product-owner` never prompts the user. It writes only specification artifacts and returns a structured digest. The conductor handles each result:

- `STATUS: REQUIREMENTS_GAP` or `STATUS: DESIGN_GAP` — relay `GAPS` and `OPEN_QUESTIONS`, obtain the user's decision, and resume the named earlier phase. In autonomous mode apply the autonomy contract: accept recommended options and relay only hard blocks with `RECOMMENDED: none`.
- `STATUS: READY_FOR_REVIEW` — present the artifact summary and its open questions. Ask for approval or concrete revision feedback, then send the answer back as a new phase invocation. In autonomous mode approve it by re-invoking `product-owner` with an explicit `AUTONOMOUS_APPROVAL: <gate id>` input (provenance `autonomous: recommendation accepted`); `product-owner` records it, the conductor never writes the row.
- `STATUS: PHASE_APPROVED` — advance to the next incomplete phase.
- `STATUS: SPEC_READY` — verify all phases are approved, the task artifact persists `development-mode` and the matching Test or verification contract, the declared scale and its `Design harvest` (with target components) are verified, and the specification index is synchronized as Draft awaiting final approval.
- `STATUS: SPEC_WRITE_ERROR` or `STATUS: BLOCKED` — relay the exact failure and stop. `STATUS: BLOCKED` naming "task-file digest" is not relayed: run the named command and re-invoke `product-owner` on the same specification with `SPEC_DIGEST`; the value is valid only for the `tasks.md` revision it ran against. If `product-owner` reports a write error on a nonexistent file because the host harness withholds file creation from subagents, pre-create the empty phase file before redispatching as a degradation fallback.

After `SPEC_READY`, ask the final blocking question as **approve and execute**, stating the declared scale and components so the owner confirms the impact before execution (in autonomous mode, re-invoke `product-owner` with `AUTONOMOUS_APPROVAL: <gate id>` and let it record the approval). On explicit approval, re-invoke `product-owner` to record the specification as Ready and synchronize the index. Begin execution only after it returns `STATUS: SPEC_FINALIZED` with validation evidence, and load the `delivery-execution` skill when it does: that skill, not this path, holds the dispatch, verification, digest, findings and closeout rules.

The specification form of the task file is `<spec-folder>/tasks.json`, beside its `tasks.md`. Validate it before the first dispatch and dispatch each worker's prompt from it unchanged: `tasks.md` is the approved record, not the dispatch source. Before the finalization invocation, run `kyber-weave arbiter plan <spec-folder>/tasks.md -f json` in the checkout holding the specification (the main session's shell, or dispatch `github-devops` when the conductor holds none) and pass `SPEC_DIGEST: <metrics.execution-digest>`. If the metric is absent (no parsed tasks, or a dropped dependency token, `KW-ARB-PARSE-001` / `KW-ARB-PLAN-001`), do not invoke finalization: reopen the tasks phase under the revision rules below: re-invoke `product-owner` with `PHASE: tasks` and the arbiter findings as revision feedback, then run the producer again on the revised `tasks.md`. Accept `SPEC_FINALIZED` only with its `TASK_FILE:` beside `tasks.md` and no `KW-DOC-TASKS-*` error from validation.

## Gate collapse in autonomous mode

When a tasks-phase digest returns `STATUS: READY_FOR_REVIEW` with `GAPS: none` and `OPEN_QUESTIONS: none`, the conductor may present one consolidated review covering both tasks approval and the final approve and execute question, instead of two separate gates. The consolidated review records two AUTONOMOUS_APPROVAL rows (tasks plus execute) from one presentation within max 2 gates, then follows the normal finalization path: it still ends at `STATUS: SPEC_FINALIZED` with validation evidence before execution begins. The consolidated invocation carries `SPEC_DIGEST`, computed after the last `tasks.md` edit.

Any gap or open question disables the collapse: when `GAPS` names a blocking gap or `OPEN_QUESTIONS` is non-empty, fall back to separate gates so each question gets its own answer.

Supervised mode never collapses: Supervised runs always present separate gates, even when there is nothing to ask.

## Revisions and mode changes

Revision feedback reopens only the affected phase and all downstream phases. Preserve approved upstream artifacts unless a reported gap requires changing them. Omission of `development-mode` means `test-first`; `standard` requires an explicit user opt-out. A post-approval mode change reopens the task artifact and the affected Test contract or verification contract for user approval.

## Planner dispatches

Unless you hold `arbiter_evaluate_agent_dispatch_or_return` (see Arbiter fallback), planner dispatches to `product-owner` carry `KYBER-ARBITER: true` and their marker, and never `TASK:`. Each spec phase carries `FEATURE:` with `PHASE:` (requirements, design, tasks, phase-approval, or finalization) as the first two non-blank lines in either order.
