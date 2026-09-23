---
id: todo/canon-test-fixture-consolidation
title: Canon test suites duplicate their CanonicalRecord fixture helpers
doc-type: todo
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-23
status: draft
---

# Canon test suites duplicate their CanonicalRecord fixture helpers

This is **context for planning the work, not a plan** — what's known, what needs deciding,
and where the seam is. It does not sequence tasks or commit to an implementation.

## Why this exists

The end-of-run review council over the
[compaction-hazard context-window plan](../archive/plans/2026-09-23-kyberdash-compaction-hazard-window.md)
raised a minor duplicate-implementation finding: the new `dash/src/canon/findings.test.ts`
copies the store fixture helpers of `dash/src/canon/sessions.test.ts` instead of sharing
them. The review accepted the duplication for that change — the plan itself directed the new
suite to follow the store fixture pattern of `sessions.test.ts`, and the duplicates report
already carries dozens of comparable fixture clusters — but accepted-for-this-change is not
endorsement-in-general. The user accepted the consolidation as a follow-up, recorded here on
2026-09-23.

## What is known

- The duplication is two helpers, both building `CanonicalRecord` fixtures:
  `tokens` (8 normalized lines, `findings.test.ts:18`) and `turn` (18 normalized lines,
  `findings.test.ts:28`). The duplicates report (`artifacts/duplicates.json`) tracks them as
  clusters `dup-7222c0d2` and `dup-3bfb5437`, and each cluster spans exactly
  `findings.test.ts` and `sessions.test.ts` — no other files.
- `dash/src/canon/measurability.test.ts` also defines a `turn`, but over `ContextTurn`, a
  narrower local type — same name, different shape, and a member of neither reported cluster.
  A consolidation should leave it alone deliberately or state why it joins.
- This is test-fixture hygiene, not a defect: no behaviour is wrong. The cost is that a
  `CanonicalRecord` shape change must be applied in every copy, and the two copies can drift.

## What needs deciding

- Where shared fixtures live — a fixture module beside the canon suites, or whatever
  shared-fixture convention the dash test suite already has, if one exists.
- The scope boundary: only the two reported helpers in the two reported files, or a sweep of
  the canon suites for further fixture duplication.
- The acceptance check is that clusters `dup-7222c0d2` and `dup-3bfb5437` no longer appear
  when `artifacts/duplicates.json` is next regenerated.

## The code seam

- `dash/src/canon/findings.test.ts` and `dash/src/canon/sessions.test.ts` — the duplicated
  `tokens` and `turn` helper pairs.
- `dash/src/canon/types.ts` — the `CanonicalRecord` and `ContentPart` shapes the fixtures
  build; `dash/src/canon/store.ts` — the `CanonStore` they feed.
