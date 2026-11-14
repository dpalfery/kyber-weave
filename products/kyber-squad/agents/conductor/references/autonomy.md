# Autonomy

`autonomy` is `supervised` or `autonomous`. It decides who answers the gates the other contracts define. Supervised behaviour is unchanged: every gate in the plan, spec and intake paths, and in the `delivery-execution` skill, is relayed to the user exactly as written there.

## Resolution

Resolve once per run, in this order:

1. a prompt value `autonomy: supervised|autonomous`, for this run only.
2. the value declared as **<autonomy>** in the repository root `AGENTS.md` Config Reg.
3. `supervised` when absent.

Persist the resolved `autonomy` in the plan or specification task artifact next to `development-mode`, and pass it in every `architect` and `product-owner` packet. Unlike a `development-mode` change, changing `autonomy` does not reopen approval; it takes effect at the next gate.

## Autonomous behaviour

Ask the user nothing that this section answers. The goal is no human in the loop: a recommendation is presumed right, and a wrong one is caught at pull request review. Record provenance, never silence: each accepted item is a row in the permanent `## Decisions` table with status `autonomous: recommendation accepted`.

- **Intake:** take the architect's `INTAKE_RECOMMENDATION` plan or spec as the choice and record it.
- **Decisions:** accept the specialist's `RECOMMENDED` option on a `STATUS: NEEDS_DECISION`, unless the item carries `ESCALATION: hard_block`.
- **Gates:** approve each phase `READY_FOR_REVIEW`, the final **approve and execute**, and each mode-change re-approval. The conductor re-invokes the owning specialist (`architect` for a plan, `product-owner` for a specification) with an explicit `AUTONOMOUS_APPROVAL: <gate id>` input, and that specialist records the approval in `## Decisions` with provenance `autonomous: recommendation accepted`, exactly as it records a human approval. The plan's final **approve and execute** is the exception: never dispatch `architect` for it. The conductor calls the finalize tool right after `PLAN_READY` (plan path); the tool records the approval and accepts any decision still marked `RECOMMENDED`.
- **Specialist decisions:** a specialist in autonomous mode decides open items itself and records `autonomous: low-confidence, no authoritative source` where no source answered. Accept those rows as written.

## Hard-block escalation

The only item relayed to the user mid-run is `STATUS: NEEDS_DECISION` with `ESCALATION: hard_block`: high risk of being wrong AND the specialist has no option it can recommend. It also carries:

```text
SOURCES_SEARCHED: <what was searched and what it said>
COST_OF_WRONG: <why it is expensive to reverse, or why the run is wasted>
RECOMMENDED: none — <why no option is safe to pick>
```

If a `hard_block` carries a real `RECOMMENDED` option instead of `none`, accept it as `autonomous: low-confidence, no authoritative source`; do not ask the user.

The conductor rejects a hard-block escalation missing any of these fields, as it rejects an unknown circuit-breaker trigger token. The specialist already declined to decide it, so send it back ONCE to be REPAIRED: supply the missing fields, same question, same ids. If the repaired block is still incomplete, or the specialist cannot name sources and the cost of being wrong, it is not a hard block: the specialist then decides it as `autonomous: low-confidence, no authoritative source`. Never ask the user for a malformed block. Batch escalations in the same question-group shape as a supervised `NEEDS_DECISION`, and return the answers keyed by question id.

## Gaps and open questions

Do not relay `GAPS` or `OPEN_QUESTIONS` that carry a recommended option: accept it and record `autonomous: recommendation accepted`. Relay only items that are a hard block, or a missing fact no one can supply (for example a failure that cannot be reproduced after a second attempt, or missing credentials).

## Delivery

Both modes deliver the same way. When the work is ready to leave the machine (with the applicable closeout done), the conductor commits to a feature branch (never the default branch), pushes it, and opens a **draft** pull request, all in one call to the deliver tool (the `delivery-execution` skill's Delivery section). The review council reviews the pushed branch, or the local branch when the push fails; it never waits for a pull request, and its verdict is posted on the pull request when one exists. Delivery never waits for the council.

### Autonomous approval of the run's delivery

**Autonomous mode is the user's approval for the run's own delivery.** `autonomy: autonomous`, set in the prompt or in the Config Reg, is the user's explicit approval for the run's commits on its run branch, the push of that branch, and its draft pull request. A host rule that asks for approval before a commit or push (an `AGENTS.md` line, for example) is satisfied by it, so do not stop to ask. It approves nothing outside the run branch: no commit to the default branch, no force-push, no reset, restore, clean or rebase of work the run did not create, and no merge.

The plan, its task file and the plan index are committed together before the first dispatch, in both modes, without asking: the finalize tool does it. Delivery never asks, and a pull request that cannot be opened (no supported remote host, no credentials) is not a broken step that stops the run: finish everything local (commits, the push if a remote accepts it, the final council review, closeout) and report `NOT DELIVERED: pull request — <reason>`.

The draft is marked ready once required CI passes. The deliver tool does not wait for CI and never marks a pull request ready: it returns the checks GitHub reports when it reads the pull request back. The pull request stays draft and the report says what CI is waiting for. Only on the delivery agent fallback does the `create-pull-request` skill, run by `github-devops`, wait at most 60 minutes for CI. No review or human approval is needed before ready.

The council verdict goes in the PR description when there is one. It does not gate ready: required CI does. The council re-reviews after every fix round until the verdict prints `fix round: none`. The loop runs on the pushed branch: delivery never waits for it, and required CI alone decides ready. The loop stops after 5 review cycles, or after 3 cycles in a row in which the count of surviving critical and major findings does not fall; the findings still open are then listed in the pull request as pending human review, and delivery continues. The `delivery-execution` skill owns the loop itself.

Human and bot review happens after the pull request is ready. A reviewer asking for changes at that point is ordinary PR review (`kilo-review-loop` or `pr-review-fix-comments`), not a conductor gate.

Delivery requires DOCS_REVIEW: rows and, for plan/spec-backed work, CLOSEOUT_COMPLETE evidence; the draft PR carries both. Assign the PR to the invoking user, and request reviewers when the repository names them, so it is notified. CI minutes triggered by the push are expected cost, not the "spend" hard stop. Never merge.

### Supervised delivery

In supervised mode the conductor runs the same push, draft, and CI-only ready step, then stops and asks the user how to proceed.

## Hard stops

These stay human in autonomous mode:

- a hard-block escalation with `RECOMMENDED: none`;
- merge, publish, delete, or spend (committing to a feature branch, pushing it, and opening a draft pull request are allowed, as above);
- an unresolvable `STATUS: BLOCKED` or write error (`PLAN_WRITE_ERROR`, `SPEC_WRITE_ERROR`), such as missing credentials or access;
- a tool that cannot repair itself: `STATUS: TOOL_FAILURE`, which is what the run-start `kyber-weave doctor --ensure` returns under `stop-on-tool-failure`, and what a docs or CodeGraph tool answers with when it cannot read the index its joins come from. The run stops before the next worker is dispatched, because the remedy is an install the run may not perform and every worker after it would answer from joins that are silently absent. A repository that sets `stop-on-tool-failure: false` gets a warning line instead, and the run continues. A read-only run never stops on it. This is not a circuit-breaker: no rework clears it. A run stops for a tool failure only on machine evidence: a non-zero `doctor --ensure` exit, or a tool response or worker report carrying the literal line `STATUS: TOOL_FAILURE`. A worker's note that it fell back to direct reads, or a `Discovery:` line with zeros, is not a tool failure: record it in the run report as a warning and continue. If you doubt the tooling, dispatch one `kyber-weave doctor --ensure` and let its exit code decide. In autonomous mode this is never a question to the user.

A check that a missing local dependency stops (a database not running, an unset environment variable) is not a hard stop, even when reported as `STATUS: BLOCKED`: record `NOT VERIFIED: <command> — <reason>` in the run report and the PR description, and continue delivery. The PR stays draft until required CI passes.

## Circuit-breakers are non-fatal

When a developer task trips a circuit-breaker (`ESCALATION: circuit-breaker`), it is reported as a finding and delivery continues — the draft pull request is the human review, so every open circuit-breaker finding is listed in the PR description as pending human review.

A circuit-breaker that `architect` mediation cannot resolve keeps rework on that cluster halted. It is listed in the PR as an open finding, and delivery continues. A failing cluster normally keeps required CI red, so the PR stays draft.

Finish every unaffected queued task and the closeout, then deliver as above. A review finding still open when the review loop stops is handled the same way: it is listed in the PR as pending human review, and it never holds back delivery.

## Circuit-breakers in supervised mode

Supervised behaviour is unchanged: an unresolved circuit-breaker finding escalates to the user and blocks completion until the user resolves it.

## Final report

Add to the completion report:

- every open circuit-breaker finding, and every review finding still open when the review loop stopped, listed as pending human review;
- the draft pull request link and its CI state (ready, or what it is waiting for);
- the todos filed for follow-up work, one line each with its path, and, as the report's last line, one question: whether to start a fresh run (new branch, new pull request) for them. Never start that run yourself, in either mode: the user answers the question.

### Autonomous decisions in the report

- every `autonomous: low-confidence` decision: ID, decision, and why no source answered, so the user can overturn it without opening the plan;
- the count of auto-approved gates.

A run-start `doctor --ensure` failure is not a final-report item: under `stop-on-tool-failure` it ends the run with `STATUS: TOOL_FAILURE` before any delivery, and a repository that sets the setting to `false` carries its warning line in the run record.
