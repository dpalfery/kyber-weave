---
id: specs/kyberdash-relational-store/index
title: KyberDash relational store specification
doc-type: index
status: draft
owner: dpalfery
last-reviewed: 2026-11-18
documentation-impact: extends-component
impact-components: [KyberDash]
---

# KyberDash relational store specification

**Status:** Draft, 2026-11-18. Requirements approved; design revised at design review (AP-D1,
AP-D2) and approved for specification drafting only (AP-D3); tasks written. **Not approved for
execution.** Three blockers are open. **BL-1:** OTLP-only history cannot be re-ingested, and the
owner must resolve that before the deletion is implemented. **BL-2:** the 17 harness ids in today's
store are unproven, which blocks release acceptance. **BL-3:** `windsurf` and `aider` have no
evidenced format. Autonomy: autonomous.
Development mode for the later implementation:
test-first. This run writes the specification only and implements nothing.

KyberDash's SQLite store (`canon.db`, schema version 24) is redesigned as a relational database in
third normal form with one schema for every harness. Each harness's own format is converted by a
collector into one set of typed DTOs in the repository layer; no column holds JSON; a value a
harness may not report lives in a child table whose missing row means "not reported"; and the
analytics path reads only that schema. Nothing is migrated: the existing database is deleted, no
legacy copy is kept, and every harness's history is re-ingested from its own files (OD-7). History
that reached the store only over OTLP has no harness file behind it, so it cannot be re-ingested.
That conflict is the open blocker BL-1, and the owner must decide it. This run deletes nothing.

The redesign fixes two known faults: every KyberDash MCP analytics call answers HTTP 500 with an
empty body on the real store, and Copilot CLI sessions are stored with `llm.invoke` records only.

## Decisions

The permanent decision table is in [requirements](requirements.md#decisions): the owner's nine
decisions (OD-1 to OD-9), the decisions taken autonomously while writing the requirements
(RQ-1 to RQ-12), the requirements approval (AP-R), the two design-review revisions (AP-D1, AP-D2),
the design approval for drafting (AP-D3) and the **open blockers BL-1, BL-2 and BL-3**. The design's
own decisions, DD-1 to DD-17, are in [design](design.md#design-decisions). Low-confidence: DD-6,
DD-8, DD-10 and DD-11. Medium: DD-3 (closed defined-field list; no JSON document kept in any form),
DD-13, DD-14, DD-15, DD-16 (no evidenced harness excluded) and DD-17 (Codex `local_shell_call` and
OTLP tool events). DD-9 holds OD-7 as the owner stated it and records BL-1.

## Design harvest

| ID | Design item | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| H1 | The relational schema, the schema catalog and the old-to-new table map | KyberDash | architecture | no |
| H2 | The per-harness field register (mapped column or dropped, with reason) | KyberDash | reference | no |
| H3 | The collector DTOs, readers, mappers and the repository-layer mapping boundary | KyberDash | architecture | no |
| H4 | Owner decisions OD-1 to OD-9 and the held autonomous decisions | KyberDash | adr | no |
| H5 | The JSON text guard and the closed defined-field list (RQ-2, DD-3); delete-and-re-ingest and the OTLP-only recoverability limit (DD-9, BL-1 and its owner resolution) | KyberDash | adr | no |
| H6 | The per-harness op and kind support matrix and the Copilot CLI journal mapping | KyberDash | reference | no |
| H7 | The analytics error envelope and per-session projection failure | KyberDash | architecture | no |

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 12 requirements in EARS form, decisions OD-1 to OD-9, RQ-1 to RQ-12, AP-R, AP-D1, AP-D2, AP-D3 and open blockers BL-1, BL-2 and BL-3 |
| [Design](design.md) | Schema catalog, the full typed table inventory (record, projection, run, rollup, finding, prediction, store-state and operational tables), the JSON text guard and defined-field list, collector boundary, harness identities and recoverability, Copilot CLI mapping, per-harness support matrix, error handling and test strategy; decisions DD-1 to DD-17 |
| [Tasks](tasks.md) | 18 groups of test-first tasks covering every requirement criterion, every candidate harness with an evidenced format, Codex tools on both paths, and all eight analytics operations. Task 16.2 (deletion) is blocked on BL-1; tasks 10.11 and 10.12 on BL-3; release acceptance on BL-2. Not approved for execution |
