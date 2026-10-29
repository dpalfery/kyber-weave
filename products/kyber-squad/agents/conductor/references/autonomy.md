# Autonomy

`autonomy` is `supervised` or `autonomous`. It decides who answers the gates the other contracts define. Supervised behaviour is unchanged: every gate in the plan, spec, intake, and execution contracts is relayed to the user exactly as written there.

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
- **Gates:** approve each phase `READY_FOR_REVIEW`, the final **approve and execute**, and each mode-change re-approval. The conductor never writes the row: it re-invokes the owning specialist (`architect` for a plan, `product-owner` for a specification) with an explicit `AUTONOMOUS_APPROVAL: <gate id>` input, and that specialist records the approval in `## Decisions` with provenance `autonomous: recommendation accepted`, exactly as it records a human approval.
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

Both modes deliver the same way. When the work is ready to leave the machine (with the applicable closeout done), the conductor commits to a feature branch (never the default branch), pushes it, and opens a **draft** pull request. The draft pull request is pushed before the review council runs, so the council reviews a pull request that already exists. Delivery never waits for the council.

The draft is marked ready once required CI passes. The `create-pull-request` skill, run by `github-devops`, waits at most 60 minutes for CI. If CI fails or is not done in that time, the pull request stays draft and the report says what it is waiting for. No review or human approval is needed before ready.

The council verdict goes in the PR description. It does not gate ready: required CI does. Human and bot review happens after the pull request is ready. A reviewer asking for changes at that point is ordinary PR review (`kilo-review-loop` or `pr-review-fix-comments`), not a conductor gate.

Delivery requires DOCS_REVIEW: rows and, for plan/spec-backed work, CLOSEOUT_COMPLETE evidence; the draft PR carries both. Assign the PR to the invoking user, and request reviewers when the repository names them, so it is notified. CI minutes triggered by the push are expected cost, not the "spend" hard stop. Never merge.

In supervised mode the conductor runs the same push, draft, and CI-only ready step, then stops and asks the user how to proceed.

## Hard stops

These stay human in autonomous mode:

- a hard-block escalation with `RECOMMENDED: none`;
- merge, publish, delete, or spend (committing to a feature branch, pushing it, and opening a draft pull request are allowed, as above);
- an unresolvable `STATUS: BLOCKED` or write error (`PLAN_WRITE_ERROR`, `SPEC_WRITE_ERROR`), such as missing credentials or access.

## NEEDS_HUMAN and circuit-breakers are non-fatal

In autonomous mode `NEEDS_HUMAN` never blocks or parks anything. That covers a `NEEDS_HUMAN` from `code-reviewer`, the protected-path escalation (`KW-REVIEW-008`), and the size escalation (`KW-REVIEW-009`). Each is reported as a finding: delivery continues, the item is listed in the PR description and in the final report as pending human review, and the draft pull request is the human review.

A circuit-breaker that `architect` mediation cannot resolve keeps rework on that cluster halted. It is listed in the PR as an open finding, and delivery continues. A failing cluster normally keeps required CI red, so the PR stays draft.

Finish every unaffected queued task and the closeout, then deliver as above. "Park" applies only to an individual remediation-loop finding: it means stop reworking that finding and list its id in the PR. It never holds back delivery.

In supervised mode `NEEDS_HUMAN` is terminal, and a circuit-breaker escalates to the user.

## Final report

Add to the completion report:

- every `autonomous: low-confidence` decision: ID, decision, and why no source answered, so the user can overturn it without opening the plan;
- the count of auto-approved gates;
- every `NEEDS_HUMAN` and open circuit-breaker finding, listed as pending human review;
- the draft pull request link and its CI state (ready, or what it is waiting for).
