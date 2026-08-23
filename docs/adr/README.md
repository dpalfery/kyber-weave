---
id: adr/index
title: Architecture decision records
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-08-22
---

# Architecture decision records

One record per architectural decision: what was decided, the alternatives that were rejected,
and why. An ADR is never edited to say something else — a decision that changes is recorded in
a new ADR that supersedes the old one, and the old one keeps its `id` so the documents that
cite it still resolve.

## Inventory

| Record | Title | Status | Date | Summary |
|---|---|---|---|---|
| [0001](0001-coding-standards-and-configuration-registry.md) | [Decoupled Coding Standards and Derived Configuration Registry](0001-coding-standards-and-configuration-registry.md) | Accepted | 2026-08-22 | Decouple project-specific coding standards from portable agents via `coding-standard` doc-type and derived root `AGENTS.md` Configuration Registry. |
| [0002](0002-three-layer-review-council-verdict-engine.md) | [Three-Layer Code Review Council with Deterministic Verdict Engine](0002-three-layer-review-council-verdict-engine.md) | Accepted | 2026-08-22 | Replace serial review prompt with deterministic gate scripts (`review.gates`), parallel specialist review lenses, and a deterministic unit-tested verdict engine (`KW-REVIEW-*`). |
| [0003](0003-cross-file-duplication-and-prior-art-lenses.md) | [Cross-File Duplication Detection and Prior-Art Retrieval in Code Review](0003-cross-file-duplication-and-prior-art-lenses.md) | Accepted | 2026-08-22 | Promote InspectCode redundancies to warnings, introduce `prior-art` lens for CodeGraph pre-lookup, and implement `review duplicates` normalized statement clustering for `duplicate-implementation` lens. |
| [0004](0004-solution-level-static-analysis-and-noise-suppression.md) | [Solution-Level Static Analysis Configuration and Clean Code Policy](0004-solution-level-static-analysis-and-noise-suppression.md) | Accepted | 2026-08-22 | Eliminate source `#pragma` clutter via root `KyberWeave.sln.DotSettings`, maintain `TreatWarningsAsErrors`, and enforce modern C# 12/13 idioms without `var` collection expressions. |
| [0005](0005-task-level-fast-review.md) | [Task-Level Fast Review Ahead of the Council](0005-task-level-fast-review.md) | Accepted | 2026-08-22 | Run each task up a three-pass ladder — two `task-reviewer` fast passes returning PASS/FAIL, then one `code-reviewer` pass — and reserve the council for once per objective before commit. |

## Writing one

Frontmatter is `doc-type: adr`, which requires only the base keys. Cite it from the documents
it decided with `decided-by: [<id>]`, and supersede a previous record with
`supersedes: [<id>]` — both are validated, so a reference to a record that does not exist
fails `KW-DOC-SPEC-006`.

Superseded records move to `archive/adrs/`, which is outside the corpus and never returned as
current guidance.
