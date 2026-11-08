---
id: todo/arbiter-gate-select-empty-changed-set
title: The gate-select rule reads an empty changed set as not applicable
doc-type: todo
component: KyberArbiter
status: draft
owner: dpalfery
last-reviewed: 2026-11-08
---

# The gate-select rule reads an empty changed set as not applicable

`KW-ARB-GATE-001` (`gate.select`) decides a gate's applicability from `review.changed-paths`:

```yaml
- when: { fact: review.changed-paths, intersects: gate.applies-when.paths }
  answer: applies
- when: { fact: review.changed-paths, exists: true }
  answer: not-applicable
```

That is the same vacuous shape slice 5 repaired for the two lenses. An empty list intersects nothing,
and the second branch asks only whether the fact **exists** — which it does, as an empty list — so a
run whose changed-path set is empty marks **every** applicable-path gate `not-applicable`. A gate
reported not-applicable is never executed and never counted as passed or failed, so the run's verdict
would silently lose gates it should have run.

The repair slice 5 made (`KW-ARB-LENS-001` now requires the set to be non-empty *and* positively
Markdown-only before it skips) was scoped to the two lenses, so this rule is untouched and unfixed.

**Why it can bite now.** Since slice 5, `review.changed-paths` is committed plus dirty against the
review base, so an empty set is rarer than it was — but it is exactly the state an unreadable or
unreachable base produces, and that is the state where a missing gate matters most: the run cannot
read the tree *and* reports nothing to review.

**Work.** Add a non-empty condition to the second branch, mirroring `KW-ARB-LENS-001`: `not-applicable`
only when the changed-path set positively shows no path can match. An empty or unreadable set must
fall through rather than answer, so the gate runs or the outcome is `undecidable` and escalates per
the trigger family. Ids are permanent: this changes `KW-ARB-GATE-001`'s decision table, not its id.

**Acceptance.** A test in `tests/KyberWeave.Tests/Arbiter/ArbiterEvaluatorTests.cs` or a new
`ArbiterGateSelectTests` shows an empty `review.changed-paths` no longer answers `not-applicable`, and
a positively Markdown-only set still does for a path-scoped gate. The article in ADR 0053 or the
Arbiter architecture that states the lens basis gains the same sentence for gate selection.

Found by slice 5 of the conductor run-speed programme (T27), recorded at its closeout. Its RED test
was not written there: the slice's repair was scoped to the lenses, and this rule was outside it.
