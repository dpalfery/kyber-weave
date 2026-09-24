# Lens: intent-alignment

## Applicability

Always applies when the change carries any stated intent — a request, a commit message, a
pull-request description, or a plan or specification under the paths declared as
**<plan-index>** and **<specification-index>** in the root `AGENTS.md` registry.

Skip only when the change arrives with no stated intent of any kind. Say so in the skip: a
change with no account of itself is worth the reviewer knowing about.

## What this lens owns

Whether the diff does what its author says it does — and whether what the author said is
enough to review against. Every other lens asks "is this code correct?"; this one asks
"is this the change that was asked for, and is it one change?"

No other seat on the council reads the narrative. If you do not check it, nobody does.

## What to look for

**Claims with nothing behind them.** The description says a bug is fixed — find the test
that would have failed before this change and passes after. If there is none, the fix is
unverified and that is the finding. The description says something was refactored with no
behaviour change — find the behaviour that changed anyway.

**Described but not implemented.** Walk each concrete claim in the description to the code
that delivers it. A described capability with no implementation is a `critical` finding: it
will be believed by everyone downstream who reads the description instead of the diff.

**Implemented but not described.** The reverse, and more common. Changes the description
does not mention: a bumped dependency, a changed default, a modified error path, a touched
migration. Unannounced scope is how unreviewed change ships inside a reviewed change.

**More than one change.** Count the independent reasons this diff could need to be
reverted. If reverting the bug fix would also revert an unrelated rename, that is two
changes wearing one PR, and each makes the other harder to review and riskier to roll back.
Report it, name the seams, and let the size and risk rules downstream decide what happens.

**A problem statement that cannot be reviewed against.** "Fixes issues", "improves
handling", "various updates" — with no statement of what was wrong, there is no standard
against which correctness can be judged. That is a finding about the change, not a
formatting complaint.

**Plan and specification drift.** Where a plan or specification governs this work, check the
delivered change against it. Silent divergence from an approved design is a finding; a
deliberate, stated divergence is not.

## What this lens must not report

- Wording, grammar, tone, or formatting of the description. You are checking correspondence
  to the code, not prose quality.
- A missing description on a genuinely trivial, self-evident change.
- Whether the change is a good idea. That was decided upstream; you check that what was
  decided is what arrived.
- Correctness of the code itself. Other lenses own that. Your finding is about the gap
  between claim and code, not about the code in isolation.
