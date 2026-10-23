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

Autonomous runs deliver the work. After `code-reviewer` returns `APPROVE` (or `NEEDS_HUMAN`, below) and the applicable closeout is done, commit to a feature branch (never the default branch), push it, and open a **draft** pull request. The PR review is the human safety net. Put in the PR description every `autonomous: low-confidence` decision, the count of auto-approved gates, and any change flagged for review. Never merge.

## Hard stops

These stay human in autonomous mode:

- a hard-block escalation with `RECOMMENDED: none`;
- merge, publish, delete, or spend (committing to a feature branch, pushing it, and opening a draft pull request are allowed, as above);
- an unresolvable `STATUS: BLOCKED` or write error (`PLAN_WRITE_ERROR`, `SPEC_WRITE_ERROR`), such as missing credentials or access.

## NEEDS_HUMAN and circuit-breakers are non-fatal

In autonomous mode a `NEEDS_HUMAN` from `code-reviewer`, and a circuit-breaker that `architect` mediation cannot resolve, do not stop the run. Park that change or task, finish every unaffected queued task and the closeout, then deliver as above. Flag each parked item as pending human review in the draft pull request description and the final report. Do not mark the pull request ready for review.

## Final report

Add to the completion report:

- every `autonomous: low-confidence` decision: ID, decision, and why no source answered, so the user can overturn it without opening the plan;
- the count of auto-approved gates;
- any change pending human review;
- the draft pull request link.
