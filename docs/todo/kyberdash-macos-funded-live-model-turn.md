---
id: todo/kyberdash-macos-funded-live-model-turn
title: macOS funded live-model turn verification
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-28
---

# macOS funded live-model turn verification

Deferred verification scope from the [harness collection standard](../rules/kyberdash-harness-collection.md).
The standard's deterministic verification (specification plus world on ubuntu CI) is green
without exercising a live model call. One live-model turn on macOS remains deferred.

## Behaviour today

Collection and completeness are verified deterministically: the specification and the
world exercise the collection-mechanism hierarchy (PRIMARY versus backup) and the
completeness predicate without a live model call. No live-model turn has been exercised
on macOS in this checkout.

## Follow-up — funded macOS live-model turn

Run one owner-authorized live-model turn on macOS against the standard's completeness
predicate: confirm a recognized call surfaces as a visible session with measured or
`not_measurable` dimensions per the rule, and that no fallback value is billed as primary.

Constraints:

- The turn requires funded execution on macOS hardware and stays owner-operated; it is
  never a merge gate.
- Deterministic enforcement rationale: CI stays deterministic. The ubuntu CI job pins the
  deterministic specification plus world; a live-model turn is non-deterministic (network,
  timing, externally metered usage) and cannot gate merges without making the suite flaky
  and non-reproducible. It is therefore tracked here, not in the CI workflow.
- No new collection mechanism is introduced by the verification; it exercises the PRIMARY
  path the rule already declares.

## Acceptance criteria

- One live-model turn on macOS projects as a visible session per the completeness predicate.
- Standard-named dimensions render measured where supplied, otherwise `not_measurable`/dash
  with reason; fallback values are not billed as primary.
- `docs/dash/telemetry-inventory.md` owner-gate note is updated to the observed outcome,
  and this todo is archived when done.
