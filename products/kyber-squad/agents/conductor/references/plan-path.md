# Plan Path

Use this path when the user supplies a plan or selects one from the directory named by **<plan-index>**.

## Route by lifecycle

- **Ready, In progress, or Blocked:** treat the approved artifact as execution authority. Resume its ready queue at the first incomplete task whose dependencies and file scope permit work. Do not send it back to `architect` merely because `architect` authored it.
- **Draft:** send the exact `PLAN_FILE` to `architect`. Relay its decision digests and return the user's answers keyed by question id. Continue until the saved Draft is decision-complete and validated.
- **Review required, Completed, Superseded, or archived:** do not execute it. Ask the user for an active artifact or route the request through the intake path.

## Draft and approval protocol

The conductor never edits a plan. Require `architect` to persist every decision and return one of its structured markers:

- `STATUS: NEEDS_DECISION` — relay all independent questions, with the specialist's recommended option first. In autonomous mode apply the autonomy contract: accept the recommendation, including on a `hard_block` that carries one; only a hard block with `RECOMMENDED: none` reaches the user.
- `STATUS: BLOCKED` naming a live Azure state question `architect` could not delegate — put that exact question to `azure-reader`, return its answer to `architect` against the same `PLAN_FILE`, and continue. This happens on harnesses that give subagents no way to invoke another agent. Any other `STATUS: BLOCKED`, or one that recurs after the answer, is handled as below.
- `STATUS: BLOCKED` naming "server root differs: run the docs checks in the worktree" — the architect raises it itself when its runtime withholds the shell (the profile allows it on every target, so this is the exception, and there is no pre-plan signal, so the first `BLOCKED` of a run is the signal: add the line `architect shell: withheld` to the run record (the same record that names the run-start route), and from then on run the CLI gates yourself after each plan save and pass them as `DOCS_CHECKS` on the next invocation; results are valid only for the plan revision they ran against, so re-run them after any further plan edit. The gates still run once per plan save, as they always did for a shell-less architect; what the line saves is the extra `BLOCKED` round trip); the `kw-docs` server is bound to the canonical checkout, so it cannot see or validate a plan written into a worktree. Do not plan again yourself and do not send the architect back without the checks: have `kyber-weave docs validate . --merge-ready` and `kyber-weave docs drift .` run in that worktree (the main session's shell, or dispatch `github-devops` when the conductor holds no shell), then re-invoke `architect` on the same `PLAN_FILE` with `DOCS_CHECKS: <the results>`. Its `PLAN_READY` then reports `DOCS_VALIDATE: pass (cli, <path>)` rather than an MCP result.
- `STATUS: BLOCKED` naming "task file" — the architect holds no shell, so it cannot run the command that writes the task file. Run `kyber-weave docs tasks <plan file> --write` in the checkout holding the plan (the main session's shell, or dispatch `github-devops` when the conductor holds none), then re-invoke `architect` on the same `PLAN_FILE`. If the command reports a gap in a plan task, relay the finding as the architect's revision feedback instead; never write the file by hand.
- `STATUS: BLOCKED` or `STATUS: PLAN_WRITE_ERROR` — relay the evidence and stop. If `architect` reports a write error on a nonexistent file because the host harness withholds file creation from subagents, pre-create the empty destination file before redispatching as a degradation fallback.
- `STATUS: PLAN_READY` — verify the saved plan and index agree on Draft, all decisions are resolved in the permanent `## Decisions` table (with complete fields: ID, Question, Answer / decision, Mitigating and supporting information, Status / approval provenance), the selected `development-mode` and its Test or verification contract are present, the declared scale (`documentation-impact` with `impact-components`) and its `Design harvest` are verified, and documentation validation passed.
- `STATUS: PLAN_FINALIZED` — accept only after the user explicitly chooses **approve and execute** (the gate states the declared scale and components for owner confirmation) and `architect` records the approval in the permanent `## Decisions` table (preserving the section intact), changes the artifact to Ready, synchronizes the index, and revalidates it. It is accepted only when the plan's validation reports no `KW-DOC-TASKS-*` finding.

In autonomous mode the conductor never writes the row: it re-invokes `architect` with an explicit `AUTONOMOUS_APPROVAL: <gate id>` input (provenance `autonomous: recommendation accepted`), and `architect` records it in `## Decisions` exactly as it records a human approval. An approval that says only “approve” is sufficient when it clearly answers the presented **approve and execute** gate. Once `PLAN_FINALIZED` is returned, load the `delivery-execution` skill and enter the ready queue immediately: that skill, not this path, holds the dispatch, verification, digest, findings and closeout rules.

`PLAN_FINALIZED` also returns `TASK_FILE:`, the path of the plan's `<plan>.tasks.json`. Accept it only when it sits beside the saved plan, validate it before the first dispatch, and dispatch each worker by task id: the worker prints its own packet from that file, and the plan is the approved record, not the dispatch source.

## Planner dispatches

Unless you hold `arbiter_evaluate_agent_dispatch_or_return` (see Arbiter fallback), planner dispatches to `architect` carry `KYBER-ARBITER: true` and their marker, and never `TASK:`. Draft authoring and resume carry a `PLAN_FILE:` header; finalization carries the `PLAN_FILE:` header followed by the `FINALIZE` line; the findings drain carries `FINDINGS:`; an escalation carries the `STATUS: ARBITER_ESCALATION` envelope itself as its marker.

`PLAN_FILE:` is always the repository-relative path with `/` separators, never the absolute path of a checkout or worktree, even when the run works in a worktree. Sent any other way, the Arbiter cannot read it as a header and falls back to the file on disk or its decision model, which is slower and less certain.

## Review and its command

A plan records who reviews the change and which skill governs the review, and never the review command itself. The end-of-run council derives that command from the `code-review` skill — measured against the change with `--base`, and admitted to the full-suite gates with `--council` — and the skill is the only place it is written down. A command transcribed into a plan goes stale the moment the gate list or one of its flags moves, and the plan then tells the reviewer to run something nobody validated.

The placement is fixed as well as the command. The council runs alone and last, in the foreground, after the documentation-impact review and every edit it causes, with no docs work and no edits in the tree while it runs. A review that shares the tree with other work reports on a change that keeps moving.

## Mode contract

If the plan omits `development-mode`, have `architect` record `test-first` as the default before the approval gate. `standard` is valid only when the user explicitly opted out of test-first development. If the requested mode changes after the plan was approved, return the plan to Draft and require reapproval of the affected Test contract or verification contract before resuming implementation. A change of `autonomy` is not a mode change and reopens nothing.
