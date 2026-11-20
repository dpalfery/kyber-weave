---
id: archive/specs/kyberdash-relational-store/index
title: KyberDash relational store specification
doc-type: index
status: archived
owner: dpalfery
last-reviewed: 2026-11-20
documentation-impact: extends-component
impact-components: [KyberDash]
---

# KyberDash relational store specification

**Status:** Archived, 2026-11-20. Superseded by and fully delivered through [the relational-store plan](../../plans/2026-11-19-kyberdash-relational-store.md), whose tasks T1–T56 shipped every requirement, decision, and task below. Original draft state (2026-11-18): Requirements approved; design revised at design review (AP-D1,
AP-D2) and approved for specification drafting only (AP-D3); tasks written. **Not approved for
execution.** The three blockers are resolved by the owner (BL-1: OTLP-only history loss accepted;
BL-2: the 17 harness ids recorded; BL-3: `windsurf` and `aider` excluded). Autonomy: autonomous.
Development mode for the later implementation:
test-first. This run writes the specification only and implements nothing.

KyberDash's SQLite store (`canon.db`, schema version 24) is redesigned as a relational database in
third normal form with one schema for every harness. Each harness's own format is converted by a
collector into one set of typed DTOs in the repository layer; no column holds JSON; a value a
harness may not report lives in a child table whose missing row means "not reported"; and the
analytics path reads only that schema. Nothing is migrated: the existing database is deleted, no
legacy copy is kept, and every harness's history is re-ingested from its own files (OD-7). History
that reached the store only over OTLP has no harness file behind it and is lost; the owner accepted
that (BL-1). This run deletes nothing.

The redesign fixes two known faults: every KyberDash MCP analytics call answers HTTP 500 with an
empty body on the real store, and Copilot CLI sessions are stored with `llm.invoke` records only.

| Document | Covers |
|---|---|
| [Requirements](requirements.md) | 12 requirements in EARS form, decisions OD-1 to OD-9, RQ-1 to RQ-12, AP-R, AP-D1, AP-D2, AP-D3 and blockers BL-1, BL-2 and BL-3 (all resolved by the owner before delivery) |
| [Design](design.md) | Schema catalog, the full typed table inventory (record, projection, run, rollup, finding, prediction, store-state and operational tables), the JSON text guard and defined-field list, collector boundary, harness identities and recoverability, Copilot CLI mapping, per-harness support matrix, error handling and test strategy; decisions DD-1 to DD-17 |
| [Tasks](tasks.md) | 18 groups of test-first tasks covering every requirement criterion, every candidate harness with an evidenced format, Codex tools on both paths, and all eight analytics operations. Task 16.2 (deletion) is blocked on BL-1; tasks 10.11 and 10.12 on BL-3; release acceptance on BL-2. Not approved for execution |

## Decisions

The permanent decision table is in [requirements](requirements.md#decisions): the owner's nine
decisions (OD-1 to OD-9), the decisions taken autonomously while writing the requirements
(RQ-1 to RQ-12), the requirements approval (AP-R), the two design-review revisions (AP-D1, AP-D2),
the design approval for drafting (AP-D3) and the blockers BL-1, BL-2 and BL-3, resolved by the owner. The design's
own decisions, DD-1 to DD-17, are in [design](design.md#design-decisions). Low-confidence: DD-6,
DD-8, DD-10 and DD-11. Medium: DD-3 (closed defined-field list; no JSON document kept in any form),
DD-13, DD-14, DD-15, DD-16 (no evidenced harness excluded) and DD-17 (Codex `local_shell_call` and
OTLP tool events). DD-9 holds OD-7 as the owner stated it.

The table below traces every decision id for closeout; the records live in [requirements](requirements.md#decisions) and [design](design.md#design-decisions).

| ID | Question | Answer / decision | Status |
|---|---|---|---|
| OD-1, OD-2, OD-3, OD-4, OD-5, OD-6, OD-7, OD-8, OD-9 | Owner decisions OD-1 to OD-9 as delivered: collector DTO set, zero JSON columns, 3NF child tables, all-table coverage, reader-only analytics, dropped-field register, delete-and-re-ingest, stored-fields inspection, in-memory unit tests | Closed |
| RQ-1, RQ-2, RQ-3, RQ-4, RQ-5, RQ-6, RQ-7, RQ-8, RQ-9, RQ-10, RQ-11, RQ-12 | Requirements decisions RQ-1 to RQ-12 as delivered, including the closed defined-field list and read-time pricing | Closed |
| AP-R, AP-D1, AP-D2, AP-D3 | Requirements, design-review, and drafting approvals recorded during specification authoring | Closed |
| BL-1, BL-2, BL-3 | Owner blocker resolutions: OTLP-only loss accepted, 17 harness ids recorded, windsurf/aider excluded | Closed |
| DD-1, DD-2, DD-3, DD-4, DD-5, DD-6, DD-7, DD-8, DD-9, DD-10, DD-11, DD-12, DD-13, DD-14, DD-15, DD-16, DD-17 | Design decisions DD-1 to DD-17 as delivered | Closed |

## Design harvest

| ID | Design item | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| H1 | The relational schema, the schema catalog and the old-to-new table map | KyberDash | architecture | yes |
| H2 | The per-harness field register (mapped column or dropped, with reason) | KyberDash | reference | yes |
| H3 | The collector DTOs, readers, mappers and the repository-layer mapping boundary | KyberDash | architecture | yes |
| H4 | Owner decisions OD-1 to OD-9 and the held autonomous decisions | KyberDash | adr | yes |
| H5 | The JSON text guard and the closed defined-field list (RQ-2, DD-3); delete-and-re-ingest and the OTLP-only recoverability limit (DD-9, BL-1 and its owner resolution) | KyberDash | adr | yes |
| H6 | The per-harness op and kind support matrix and the Copilot CLI journal mapping | KyberDash | reference | yes |
| H7 | The analytics error envelope and per-session projection failure | KyberDash | architecture | yes |


## Outcome

Delivered in full through [the relational-store plan](../../plans/2026-11-19-kyberdash-relational-store.md) (tasks T1–T56, branch evidence at closeout): the v24 JSON store is replaced by the relational schema (schema version 25) driven by `RelationalSchemaCatalog`; every evidenced harness maps through static collector DTOs; analytics reads only the typed store behind a contract-v1 error envelope; the stored-fields endpoint and panel expose every reachable catalog column; and `CanonSchema`/`CanonStoreRepository` are deleted with unread old-store removal and re-ingest.

## Closeout mapping

Every decision and harvest item maps to the living document that now carries it.

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| OD-1, OD-2, OD-3, OD-4, OD-5, OD-6, OD-7, OD-8, OD-9 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md); [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
| RQ-1, RQ-2, RQ-3, RQ-4, RQ-5, RQ-6, RQ-7, RQ-8, RQ-9, RQ-10, RQ-11, RQ-12 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md) | extends-component | archived |
| AP-R, AP-D1, AP-D2, AP-D3 | Documentation waiver — specification authoring approvals consumed during drafting; no surviving behavioral invariant | extends-component | archived |
| BL-1, BL-2, BL-3 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md); [KyberDash architecture](../../../dash/architecture.md); [Field register](../../../dash/field-register.md) | extends-component | archived |
| DD-1, DD-2, DD-3, DD-4, DD-5, DD-6, DD-7, DD-8, DD-9, DD-10, DD-11, DD-12, DD-13, DD-14, DD-15, DD-16, DD-17 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md); [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
| H1 | [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
| H2 | [Field register](../../../dash/field-register.md) | extends-component | archived |
| H3 | [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
| H4 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md) | extends-component | archived |
| H5 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md) | extends-component | archived |
| H6 | [Field register](../../../dash/field-register.md) | extends-component | archived |
| H7 | [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
