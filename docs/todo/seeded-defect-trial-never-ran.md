---
id: todo/seeded-defect-trial-never-ran
title: Run the seeded-defect trial the worker-text trim is gated on
doc-type: todo
status: draft
component: KyberSquad
owner: dpalfery
last-reviewed: 2026-11-08
---

# Run the seeded-defect trial the worker-text trim is gated on

The trim's design makes the seeded-defect trial the gate on deleting worker text: nine planted defects,
run against the old text and the new text, and a defect the new arm catches later — with no rule-map row
naming the later check — means that rule's text stays. **The trial has not run.** It needs real worker
runs on two deployments, and nothing in this environment can start one. The removals were applied on the
rule map's homes instead, and the rule map records that gap in [what T52 did with this page](../kyber-squad/worker-rule-map.md#what-t52-did-with-this-page).

## What to run

Per decision D37 of the plan that closed this work: a scratch clone, one Squad deployment per arm, the
same harness and the same model on both arms, and one fixed packet per worker role. Only the worker text
differs, so the "new" arm deploys the candidate text — the tree at the closeout commit — and the "old"
arm deploys the text from before the removals.

The nine defects, each planted in its own packet:

1. A compiler warning.
2. A nullable error.
3. A `var` where the standard requires an explicit type.
4. An unused `using`.
5. Bad formatting.
6. An analyzer violation.
7. A suppression with no reason.
8. A test that only asserts nothing throws.
9. A test that reads the real home folder.

## What to compare

For each defect, the stage each arm catches it at: the task gate, the worker's own rework loop, the
end-of-run review, or not at all. Record the nine-by-two table in
[the worker rule map](../kyber-squad/worker-rule-map.md), one row per defect. A new arm that catches a
defect at the same stage, or later with a rule-map row already naming the later check, passes; any other
later catch means restoring that rule's text where the worker read it.

The harness and the model are recorded with the table, because a stage result from one model says
nothing about another.
