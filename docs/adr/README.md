---
id: adr/index
title: Architecture decision records
doc-type: index
status: current
owner: dpalfery
last-reviewed: 2026-09-05
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
| [0005](0005-task-level-fast-review.md) | [Deterministic Fixes and Task-Level Review Ahead of the Council](0005-task-level-fast-review.md) | Accepted | 2026-08-22 | Fix mechanical defects deterministically in the worker completion gate (`dotnet format`, analyzer fixes, `cleanupcode --include`), give single tasks to `task-reviewer` for up to three PASS/FAIL passes, and run `code-reviewer` once per run — never per task unless a human asks. |
| [0006](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) | [KyberDash as a TypeScript Soft Fork with a Merge Zone and an Embedded OTLP Receiver](0006-kyberdash-soft-fork-merge-zone-and-embedded-receiver.md) | Accepted | 2026-08-29 | Vendor `codeburn` under `dash/` with merge zone isolation in `dash/kyber/`, embed native OTLP receiver on port 4318, and standardize on single canonical span model. |
| [0007](0007-kyberdash-agent-session-analysis-integration.md) | [KyberDash Agent Session Analysis Integration, Dual-Database Architecture, and Navigation Topology](0007-kyberdash-agent-session-analysis-integration.md) | Accepted | 2026-09-03 | Embed deep agent session analysis into Context Explorer, streamline top navigation to 5 tabs, establish dual-database SQLite bridge (`~/.kyberdash/canon.db` and fallback `sessions.db`), and implement formal `/api/kyber/*` REST contract. |
| [0008](0008-kyberdash-single-canonical-store.md) | [Single Canonical Store; Supersede ADR 0007 D4](0008-kyberdash-single-canonical-store.md) | Accepted | 2026-09-03 | Restore `canon.db` as the only store with derived cached sessions, retire the `sessions.db` fallback, and store content once as compressed parts with the flat map derived on read. |
| [0009](0009-multi-signal-ingestion-span-shaped-record.md) | [Multi-Signal Ingestion into a Span-Shaped Canonical Record](0009-multi-signal-ingestion-span-shaped-record.md) | Accepted | 2026-09-04 | Serve `/v1/logs` as an enrichment path onto one span-shaped record, quarantine uncorrelated logs and non-model spans, and fix OTel/dot-folder source precedence at one turn. |
| [0010](0010-keywords-prefix-coverage-and-oov-idf.md) | [Keywords Identity, Compound Prefix Coverage, and Calibrated OOV IDF](0010-keywords-prefix-coverage-and-oov-idf.md) | Accepted | 2026-09-03 | Optional `keywords`/`aliases` identity, exact and partial keyword weights, camelCase and ≥3-character prefix coverage, and OOV IDF calibrated to a 20% corpus-share baseline. |
| [0011](0011-asad-only-context-view-and-payload-contract.md) | [ASAD as the Only Context View and as the Canonical Session Contract](0011-asad-only-context-view-and-payload-contract.md) | Accepted | 2026-09-04 | Context renders only the ASAD dashboard; `canon.db` emits the ASAD payload directly; every named harness is in scope with `not_measurable` reasons rather than zeros. |
| [0012](0012-progressive-disclosure-6-level-diagnostic-spine.md) | [Progressive Disclosure Six-Level Diagnostic Spine and Independent Dimension Vectors](0012-progressive-disclosure-6-level-diagnostic-spine.md) | Accepted | 2026-09-05 | Six-level diagnostic hierarchy (Attention to ContextItem), first-class Run and AgentExecution entities with derived grouping, six independent dimension vectors, and phase-aligned comparison. |
| [0013](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md) | [Telemetry-Grounded Finding Contracts, Waste Ranking, and Relocation Discipline](0013-telemetry-grounded-finding-contracts-and-waste-ranking.md) | Accepted | 2026-09-05 | Full finding contracts with ≥2 evidence rows and outcome-risk caveats, waste ranking formula, relocation over deletion discipline, and evidence of use classification vocabulary. |
| [0014](0014-unclipped-turn-inspection-and-copy-out-protocol.md) | [Unclipped Turn Inspection, Content Retention Window, and Copy-Out Protocol](0014-unclipped-turn-inspection-and-copy-out-protocol.md) | Accepted | 2026-09-05 | Progressive-disclosure Context Inspector with full plain-text inspection and multi-part copy out, bounded by a 14-day rolling retention window and CLI purge command. |
| [0015](0015-opt-in-llm-context-review-seam.md) | [Opt-In LLM Context Review Seam and Finding Isolation](0015-opt-in-llm-context-review-seam.md) | Accepted | 2026-09-05 | On-demand opt-in LLM review seam requiring discrete per-invocation user action, prompt-enforced relocation and risk constraints, and strict isolation from canonical finding tables. |

## Writing one

Frontmatter is `doc-type: adr`, which requires only the base keys. Cite it from the documents
it decided with `decided-by: [<id>]`, and supersede a previous record with
`supersedes: [<id>]` — both are validated, so a reference to a record that does not exist
fails `KW-DOC-SPEC-006`.

Superseded records move to `archive/adrs/`, which is outside the corpus and never returned as
current guidance.
