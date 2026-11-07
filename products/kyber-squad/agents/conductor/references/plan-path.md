# Plan Path

Use this path when the user supplies a plan or selects one from the directory named by **<plan-index>**.

## Run start

The run fetches and branches before any planning dispatch, so the plan — and everything built from it — lands on a named branch over a current base.

- `kyber-weave run start --harness {{harness}} --type <type> --slug <slug>` when the run has no branch yet. The `--type` is the conventional type the work will be committed as, and the `--slug` is the work in lowercase hyphen-separated words, so the branch `[<owner>.<agent>.]<harness>/<type>/<slug>` says what the plan is for.
- `--resume <branch>` when a plan already owns a branch and this run continues on it. The command reports whether that branch is ahead of or behind the base instead of creating a second branch for the same work. A numeric pull request reference is not accepted today; pass the branch the pull request is from, as a local branch or `origin/<branch>`.
- Where the harness gives you no shell, dispatch `github-devops` with the exact command and `RUN-START: true`, which the Arbiter classifies as an `investigate` dispatch.

The base is the branch `run.base-branch` declares, or `origin/HEAD` when the repository declares none — never an assumed `main`. Record the printed `base`, `branch`, `where`, `revision` and versions in the run record, and dispatch planning work into `where`: a planner reading a checkout the operator left on another branch plans against history the run is not building on.

## Route by lifecycle

- **Ready, In progress, or Blocked:** treat the approved artifact as execution authority. Resume its ready queue at the first incomplete task whose dependencies and file scope permit work. Do not send it back to `architect` merely because `architect` authored it.
- **Draft:** send the exact `PLAN_FILE` to `architect`. Relay its decision digests and return the user's answers keyed by question id. Continue until the saved Draft is decision-complete and validated.
- **Review required, Completed, Superseded, or archived:** do not execute it. Ask the user for an active artifact or route the request through the intake path.

## Draft and approval protocol

The conductor never edits a plan. Require `architect` to persist every decision and return one of its structured markers:

- `STATUS: NEEDS_DECISION` — relay all independent questions, with the specialist's recommended option first. In autonomous mode apply the autonomy contract: accept the recommendation, including on a `hard_block` that carries one; only a hard block with `RECOMMENDED: none` reaches the user.
- `STATUS: BLOCKED` naming a live Azure state question `architect` could not delegate — put that exact question to `azure-reader`, return its answer to `architect` against the same `PLAN_FILE`, and continue. This happens on harnesses that give subagents no way to invoke another agent. Any other `STATUS: BLOCKED`, or one that recurs after the answer, is handled as below.
- `STATUS: BLOCKED` or `STATUS: PLAN_WRITE_ERROR` — relay the evidence and stop. If `architect` reports a write error on a nonexistent file because the host harness withholds file creation from subagents, pre-create the empty destination file before redispatching as a degradation fallback.
- `STATUS: PLAN_READY` — verify the saved plan and index agree on Draft, all decisions are resolved in the permanent `## Decisions` table (with complete fields: ID, Question, Answer / decision, Mitigating and supporting information, Status / approval provenance), the selected `development-mode` and its Test or verification contract are present, the declared scale (`documentation-impact` with `impact-components`) and its `Design harvest` are verified, and documentation validation passed.
- `STATUS: PLAN_FINALIZED` — accept only after the user explicitly chooses **approve and execute** (the gate states the declared scale and components for owner confirmation) and `architect` records the approval in the permanent `## Decisions` table (preserving the section intact), changes the artifact to Ready, synchronizes the index, and revalidates it.

In autonomous mode the conductor never writes the row: it re-invokes `architect` with an explicit `AUTONOMOUS_APPROVAL: <gate id>` input (provenance `autonomous: recommendation accepted`), and `architect` records it in `## Decisions` exactly as it records a human approval. An approval that says only “approve” is sufficient when it clearly answers the presented **approve and execute** gate. Once `PLAN_FINALIZED` is returned, enter the execution contract immediately.

## Planner dispatches

Unless you hold `arbiter_evaluate_agent_dispatch_or_return` (see Arbiter fallback), planner dispatches to `architect` carry `KYBER-ARBITER: true` and their marker, and never `TASK:`. Draft authoring and resume carry a `PLAN_FILE:` header; finalization carries the `PLAN_FILE:` header followed by the `FINALIZE` line; the findings drain carries `FINDINGS:`; an escalation carries the `STATUS: ARBITER_ESCALATION` envelope itself as its marker.

## Mode contract

If the plan omits `development-mode`, have `architect` record `test-first` as the default before the approval gate. `standard` is valid only when the user explicitly opted out of test-first development. If the requested mode changes after the plan was approved, return the plan to Draft and require reapproval of the affected Test contract or verification contract before resuming implementation. A change of `autonomy` is not a mode change and reopens nothing.
