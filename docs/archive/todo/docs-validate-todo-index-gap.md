---
id: archive/todo/docs-validate-todo-index-gap
title: docs validate does not require todo files to be listed in the todo index
doc-type: todo
component: DocGraph
owner: dpalfery
last-reviewed: 2026-09-24
status: superseded
---

# docs validate does not require todo files to be listed in the todo index

**Status:** Superseded and archived
**Archive Date:** 2026-09-24

Fixed by `KW-DOC-LIFECYCLE-002` (`TodoInventoryValidator`) in PR #106. Kept as the provenance of the rule.

---

This is a **bug record for a documentation gate, not a design** — what the gate did before
the fix, the live instance, and the asymmetry with plans. The resolution is recorded below;
the narrative is kept as the provenance of the rule.

## Why this exists

Raised by the user on 2026-09-23, during the delivery of the
[KyberDash compaction-hazard window plan](../plans/2026-09-23-kyberdash-compaction-hazard-window.md):
`docs validate` passes with zero findings even when a todo file sits under `docs/todo/`
without a row in the todo inventory of [`README.md`](../../todo/README.md). The gate that is supposed to
keep the governed corpus consistent does not notice one of its own artifacts going unlisted.

## What is known

- The todo index declares required frontmatter for every todo file in this directory, but
  nothing enforces that a todo file is *listed*. A `doc-type: todo` document can exist under
  `docs/todo/` with no inventory row and `docs validate` reports nothing.
- Live instance: [`harness-specific-instruction-inserts.md`](harness-specific-instruction-inserts.md)
  has been on disk since 2026-08-25 with no index row; `docs validate .` and `docs drift .`
  both reported zero findings on 2026-09-23.
- Plans are enforced. `docs/plans/README.md` requires every plan under `docs/plans/` to stay
  reachable from the plan index and `docs validate` reports one that is not as
  `KW-DOC-LIFECYCLE-001`, implemented in
  `src/KyberWeave.Core/Docs/Validation/PlanInventoryValidator.cs`. Todos have no equivalent —
  that asymmetry is the gap.
- Expected behaviour: an unindexed todo file should produce a finding telling the author to
  add the index row, or remove the file. The inventory is how a reader discovers deferred
  work; the reasoning `PlanInventoryValidator` records for plans — unlisted work is invisible
  to exactly the reader the index serves — applies to todos equally.

## Resolution

Captured 2026-09-23, fixed in the same cycle by `KW-DOC-LIFECYCLE-002` (Severity Error),
emitted by `TodoInventoryValidator` in PR #106 (branch
`feat/kyberdash-compaction-hazard-window`): a `todo` document under `docs/todo/` that this
inventory does not link to is now an error, hinting the author to add the index row or move
the file to `docs/archive/todo/`. The live instance,
[`harness-specific-instruction-inserts.md`](harness-specific-instruction-inserts.md), was
indexed in the same change. The rule id is permanent — suppressions and baselines key on it —
so this record stays as the provenance of why the rule exists, not as open work.
