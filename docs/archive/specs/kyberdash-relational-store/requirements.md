---
id: archive/specs/kyberdash-relational-store/requirements
title: KyberDash relational store requirements
doc-type: requirements
status: archived
owner: dpalfery
last-reviewed: 2026-11-20
component: KyberDash
documentation-impact: extends-component
impact-components: [KyberDash]
keywords:
  - kyberdash
  - canon.db
  - sqlite
  - relational schema
  - third normal form
  - collector
  - harness mapping
  - analytics
  - copilot
---

# Requirements Document

**Status:** Archived, 2026-11-20. Superseded by and fully delivered through [the relational-store plan](../../plans/2026-11-19-kyberdash-relational-store.md); every requirement below shipped as plan tasks T1–T56.


## Introduction

KyberDash keeps every harness's telemetry in one SQLite file, `canon.db`, at schema version 24
(`CanonSchema.SchemaVersion`). The schema stores most of what it knows as JSON text:

- `records` keeps `tokens_json`, `content_json`, `cost_json`, `measurability_json`, `parts_json`
  (compressed) and `raw` (the compressed harness attribute bag).
- `session` keeps `summary_json` and `payload`; `session_turn` keeps `turn_json`.
- `run`, `execution`, `harness_rollup`, `finding` and `prediction` keep `payload`, and also
  `parent_linkage_json`, `measurability_json`, `error_bar_json` and `evidence_links_json`.
- `quarantine` keeps `raw_payload`; `pending_logs` and `quarantined_logs` keep `payload`;
  `metric_point` keeps `attributes_json`, `resource_json` and `scope_json`; `metadata` keeps
  JSON maps such as the projection session-key map as text values.

The store has 22 tables: `records`, `session`, `session_turn`, `run`, `execution`, `token_cache`,
`quarantine`, `pending_logs`, `quarantined_logs`, `enriched_logs`, `problems`, `ingest_log`,
`metadata`, `harness_rollup`, `finding`, `prediction`, `model_context_window_catalog`,
`model_context_window_catalog_vendor`, `metric_point`, `source_checkpoint`, `record_provenance`
and `refresh_run`.

Two faults follow from that design and are the reason for this specification:

1. **Every KyberDash MCP analytics call fails.** The engine answers HTTP 500 with an empty body on
   the real store. Diagnosis (reproduced against a local server, 2026-11-18): the record reader in
   `CanonStoreRepository.ParseRecordRow` deserializes `content_json` as
   `Dictionary<string, string>`, but legacy rows hold numbers and booleans in that object, so the
   read throws. `KyberBridgeEngine.ExecuteAnalytics` calls `EnsureProjected` before every
   operation, the projection reads records, and each operation fails the same way while the
   rebuild is retried and fails again. All unit tests pass because they mock `IAnalyticsService`
   rather than run the engine over stored records.
2. **Copilot CLI sessions carry only `llm.invoke` records.** The current engine's file discovery
   (`TranscriptReaderRepository.DiscoverLocalSources`) scans Claude Code and Codex roots only; a
   synthetic ingest with a fake store found Claude records and zero Copilot records. The deleted
   TypeScript Copilot parser (`dash/src/providers/copilot.ts`, removed before `fc80ddf9`) produced
   usage records with no tools, and file synthesis (`CanonSynth.Synthesize`) emits `llm.invoke`
   parents and `tool.invoke` children but never `agent.invoke`.

The feature redesigns the store as a relational database in third normal form with one schema for
every harness, a collector whose only job is to convert each harness's format into that schema, and
an analytics path that reads only that schema. Nothing is migrated: the existing database is deleted
and every harness's history is re-ingested from the harness's own files (OD-7). History that reached
the store only over OTLP has no harness files, so re-ingest cannot recover it; the owner accepted
that loss (BL-1).

**In scope:** the store schema and every table in it; the collector DTOs and per-harness mappers;
local-source discovery for every harness the collector maps, including Copilot CLI; the analytics
path and its eight operations (`status`, `catalog`, `query`, `search-sessions`, `inspect`,
`findings`, `compare`, `outcomes`); the session projection, rollups, findings and predictions as
stored tables; the Copilot session detail page; and the unit tests for all of the above.

**Out of scope:** migrating any existing row; keeping the original harness payload; integration
tests; any change to the MCP contract-v1 envelope shape other than making it succeed; and
implementation in the run that writes this specification.

## Decisions

The owner's nine decisions (OD rows) are binding and were given on 2026-11-18. The RQ rows were
decided autonomously while writing these requirements under `autonomy: autonomous`; each names its
basis, and any marked low-confidence is called out for pull-request review.

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| OD-1 | What is the collector's job? | Only to convert each harness's own format into one collector schema. It maps the harness's dynamic JSON to static, defined DTO types in the repository layer, so the same structure is saved for every harness. | Requirements 1 and 6. | Accepted — owner, 2026-11-18 |
| OD-2 | May the database hold dynamic JSON? | No. No column holds JSON text; every field the collector keeps is a typed column. Today `records` keeps `tokens_json`, `content_json`, `cost_json`, `measurability_json`, `raw` and `parts_json`, and other tables hold JSON payloads; all of them go. | Requirement 2. | Accepted — owner, 2026-11-18 |
| OD-3 | How is the schema shaped? | Relational, in third normal form. A value a harness may not report (tokens, cost, timing and similar) lives in a child table, and "not reported" is the absence of a child row, not an empty column or a status flag. Repeated items (content parts, tool calls, turns) are child tables with one row per item. | Requirement 3. | Accepted — owner, 2026-11-18 |
| OD-4 | Which tables does the redesign cover? | All of them, under one schema: records, sessions, turns, findings, runs, executions, rollups and whatever else the store holds today. | Requirement 4 lists the 22 tables. | Accepted — owner, 2026-11-18 |
| OD-5 | What does the analytics path read? | Only that one schema. It never parses harness formats or JSON. | Requirement 5. | Accepted — owner, 2026-11-18 |
| OD-6 | What happens to a harness field with no place in the schema? | It is mapped to a typed column when it has a shared meaning, or dropped with its reason documented. There is no catch-all column. The original harness payload is not stored; when a mapping changes, the data is re-ingested from the harness's own files. | Requirement 6. | Accepted — owner, 2026-11-18 |
| OD-7 | Is the existing data migrated? | No. The existing database is deleted and every harness's history is re-ingested. | Requirement 7; consequences in RQ-6 and RQ-7. | Accepted — owner, 2026-11-18 |
| OD-8 | How does the owner check what each field holds? | KyberDash's Copilot session detail page shows every field the schema holds for a Copilot session. | Requirement 9. | Accepted — owner, 2026-11-18 |
| OD-9 | How is it tested? | Unit tests over in-memory fakes of the store interfaces, never integration tests; no test reads the database. Each harness mapping is tested with synthetic records whose shapes mirror what that harness really produces (17 harnesses are in today's store; ops `llm.invoke`, `tool.invoke` and `agent.invoke`; kinds `internal` and `client`). Every analytics operation is tested over those records. | Requirements 10 and 11; RQ-1 and RQ-8. | Accepted — owner, 2026-11-18 |
| RQ-1 | Which harnesses get synthetic fixtures, given that the owner's store may not be read to list the 17? | Every harness identity the collector has a mapper for gets fixtures. That set is a superset of the 17 in today's store, so it covers them without reading private data. The design names the set from `CanonPricing.NormalizeHarnessName` and the existing adapters. | Covered by OD-9's "no test reads the database" and the run's ban on private local data. Cheap to extend: a missing harness is one more fixture. | autonomous: recommendation accepted |
| RQ-2 | May a column hold text that is a JSON document, such as a tool input, a tool result or a content part that happens to be one? | No; OD-2 applies literally and there is no exception for content. Each value the collector keeps is tested: if its trimmed whole text, or the native value, is a JSON object or array, it is a JSON document. A JSON document is never stored, decomposed or kept in any form. From it, the collector keeps only (a) the defined, shared-meaning fields on the design's closed defined-field list, each as a typed column, and (b) measurements of the original that a detector uses: sizes, measured tokens and a one-way fingerprint. Everything else in it is dropped, and the field register gives the reason. Text that is not a JSON document, including prose that contains a JSON fragment, is useful plain text and is kept in a `TEXT` column. Content that is not text, such as images and binary blocks, is dropped with a reason. The store adapter and the in-memory fake both refuse to write any `TEXT` value that is a JSON document. | Revised twice on 2026-11-18. At requirements approval, OD-2 was to be checked literally. At design review (AP-D1), the earlier answer was withdrawn: it broke documents into generic path/value leaf rows, which is a catch-all for arbitrary input, conflicts with OD-6, and could rebuild the payload. The values that detectors read today (skill name, exit code, error, test-count text, sizes, argument hash) each get a named typed column. Command lines and file paths inside JSON arguments are lost, and no consumer reads them today. | autonomous: medium; revised at design review (AP-D1), 2026-11-18 |
| RQ-3 | How is cost stored without a status flag? | Only a harness-reported cost is stored, as a child row (amount, currency, model) of the record. A cost priced from published rates is derived from tokens and the rate table, so analytics computes it at read time and never stores it; "no rate", "out of scope" and "not billed" are analytics outputs, not stored values. | Third normal form forbids storing a value derivable from other stored values (OD-3). Expensive to reverse: it fixes where pricing runs. | autonomous: low-confidence, no authoritative source |
| RQ-4 | What replaces the per-record measurability map? | Nothing is stored. Whether a metric was reported is the presence of its child row; coverage figures are computed by analytics from those rows. | Directly follows OD-3 ("not a status flag"). | autonomous: recommendation accepted |
| RQ-5 | Do projections (sessions, runs, executions, rollups, findings, predictions) stay stored? | Yes, as typed tables rebuilt from records, so OD-4's "one schema for all tables" holds and reads stay cheap. A detector's own fields become typed columns in a child table per detector, not a payload. | OD-4 names findings, runs, executions and rollups as tables. | autonomous: recommendation accepted |
| RQ-6 | What happens to operator settings, which today live as `settings.*` keys in `metadata` inside `canon.db`? | They live in a typed settings table in the new store. Deleting the old database resets them to their defaults (refresh cadence 5 minutes, dashboard port 4747, jobs not paused); they are not carried over. | OD-7 forbids migration. Cheap: the operator sets them again. | autonomous: low-confidence, no authoritative source |
| RQ-7 | What happens to history that reached the store only over OTLP and has no harness file behind it? | It is lost when the database is deleted, and that is accepted (BL-1). | Resolved by the owner, 2026-11-18. | RESOLVED BY OWNER — see BL-1 |
| RQ-8 | How is the SQLite adapter verified when no test may read a database? | Its SQL is checked through the existing `sqlite3` process seam with a fake process runner that records the statements and returns canned output; no database file is opened. The schema DDL is checked as text. Correct behaviour against a real SQLite file is confirmed by the owner through re-ingest and the Copilot detail page (OD-8). | Conflicts with ADR 0089's contract suite run against the real adapter in CI. OD-9 is the owner's explicit, later decision for this feature, so it wins here; the conflict is recorded. | autonomous: low-confidence, no authoritative source |
| RQ-9 | Where do the `metadata` key-value pairs and other operational tables go? | Each key with a meaning becomes a typed column or table (store state, projection dirty sessions as rows, session-key map as rows). `quarantine` keeps typed columns and no raw payload; pending and quarantined OTLP logs are decoded into collector DTOs before they are kept; `metric_point` attributes with a shared meaning become typed columns and the rest are dropped with reasons. | Follows OD-2, OD-4 and OD-6. | autonomous: low-confidence, no authoritative source |
| RQ-10 | Does an unreadable or unexpected stored value still fail a whole analytics operation? | No. Because the store holds only typed columns, a type mismatch cannot occur on read; any failure that does occur is answered with a contract-v1 error envelope with a non-empty body and a stable error code, never an empty 500. | Directly fixes the known fault. | autonomous: recommendation accepted |
| RQ-11 | Does this run implement anything? | No. The owner forbids implementation in this run; the specification ends at tasks and a draft pull request. Implementation later follows test-first. | Owner's request, 2026-11-18. | autonomous: recommendation accepted |
| RQ-12 | What does each harness's synthetic fixture contain, given OD-9's ops and kinds? | Each harness's fixtures produce only the ops and kinds that its evidenced native format can produce, and only the fields that the format is shown to carry. The design's per-harness support matrix names the evidence for each. Between them, all fixtures cover every op and kind OD-9 names. No fixture invents a field or an op so that one harness covers all of them. | Required at requirements approval ("account for operation support per harness without inventing fields"). | autonomous: recommendation accepted; added at requirements approval, 2026-11-18 |
| AP-R | Are the requirements approved? | Yes, with the condition that OD-2 is checked literally. RQ-2, Requirement 2.4, 2.6 and 2.7, Requirement 8.3 and 8.4, and Requirement 11.7 were revised to meet it. Execution is not approved. | `AUTONOMOUS_APPROVAL: requirements review, subject to checking owner decision 2 literally`, 2026-11-18. | Approved (autonomous), 2026-11-18 |
| AP-D1 | What did the first design review change? | (1) The generic document/leaf/path/value mechanism was removed. RQ-2 and criteria 2.4, 8.3 and 11.7 now keep only a closed list of defined fields plus measurements, and drop the rest with reasons. (2) The projection inventory, Codex tool support and the harness-identity candidates are completed in the design. (3) A proposal to set the old store aside instead of deleting it was made in that revision and **withdrawn at the second design review** (AP-D2), because it overrode OD-7. | Design review instruction, 2026-11-18: remove the catch-all, complete the inventory, confirm Codex, and do not invent support for the 17 harnesses. | autonomous: (1) medium; (2) recorded; (3) withdrawn, 2026-11-18 |
| AP-D2 | What did the second design review change? | OD-7 is restored as stated. The existing database is deleted unread, no legacy database is kept, nothing is migrated, and every harness's history is re-ingested from harness files. Requirement 7 is restored to match. The conflict between OD-7 and OTLP-only history is recorded as BL-1. The 17 harness ids stay an evidence gap: candidates from code are kept apart from proof, and no route requires the owner to hand over private data. | Design review instruction, 2026-11-18: "do not autonomously override a stated owner decision". | Accepted (autonomous), 2026-11-18 |
| BL-1 | OD-7 deletes the database and says every harness's history is re-ingested, but history that reached the store only over OTLP has no harness file behind it. Which clause gives way? | **(a) The loss is accepted.** Delete the database and re-ingest what the harness files hold; OTLP-only history is gone and nothing tries to recover it. KyberDash history is never a reason to block, ask or keep a legacy copy. | The owner, 2026-11-18: "We can blow the data away and re-ingest. There's no need to worry about migrations or saving the data", and "Don't care about history. Stop asking me." | RESOLVED BY OWNER |
| AP-D3 | Was the design approved, and for what? | Design approved autonomously **for specification drafting only**. This is not approval to execute, and it does not decide BL-1. The approval required tasks to cover every evidenced harness and analytics operation, to resolve unsupported source and format coverage by tasks instead of excluding it, and to include Codex tool handling on both paths. Design added DD-16 (no evidenced harness excluded) and DD-17 (Codex `local_shell_call` and OTLP `codex.tool_result`). | Design approval instruction, 2026-11-18. | Accepted (autonomous) for drafting, 2026-11-18; execution NOT approved |
| BL-2 | Which harness ids does the owner's store hold? | The 17 ids, with their record counts by op, are: `antigravity`, `antigravity-cli`, `claude-code`, `claude-desktop`, `claude-unclassified`, `codex-desktop`, `commandcode`, `copilot`, `copilot-agent`, `copilot-cli`, `cursor`, `cursor-agent`, `droid`, `kilo-shared-runtime`, `opencode`, `pi`, `zcode`. Ops seen: `llm.invoke`, `tool.invoke`, `agent.invoke`; kinds `internal` and `client`. `copilot-cli` holds only `llm.invoke` records (1,464), which Requirement 8 fixes. Re-ingest acceptance (16.2, 18.1) is measured against this list. | Supplied by the owner's session from a read-only query of the owner's store (`select harness, op, kind, count(*) from records group by 1,2,3`), 2026-11-18. No agent, test or code reads the store. | RESOLVED BY OWNER |
| BL-3 | Where do `windsurf` and `aider` keep history? | **Both are excluded.** `aider` is not a supported harness, and `windsurf` has been replaced by Devin. Neither gets a reader, a mapper or a fixture, and neither has records in today's store. Their names may still normalize, but discovery reports nothing for them. | The owner, 2026-11-18: "I haven't used Aider yet, not one of our supported harnesses. Windsurf has basically been replaced by Devin." | RESOLVED BY OWNER |

## Requirements

### Requirement 1 — One collector schema, mapped in the repository layer

**User Story:** As the KyberDash owner, I want each harness's own format converted into one
collector schema of static DTO types, so that the same structure is saved for every harness.

#### Acceptance Criteria

1.1. WHEN the collector reads a harness's native session data THEN the repository layer SHALL map it
into the collector DTO types and SHALL return only those DTO types to the layers above it.

1.2. WHEN two harnesses report the same concept (for example a model call, a tool call, an agent
invocation, a token count or a turn) THEN the collector SHALL map both into the same DTO type and
the same DTO property.

1.3. IF a harness mapper needs a value that the DTO types do not define THEN the change SHALL add a
typed DTO property and a typed column, and SHALL NOT add a dictionary, a JSON element or any
untyped property to a DTO.

1.4. WHEN a collector DTO is defined THEN every property SHALL have a static .NET type (string,
integer, floating point, boolean, timestamp, enumeration, or a list of another collector DTO), and
no property SHALL be `JsonElement`, `JsonNode`, `object`, or a dictionary keyed by a harness field
name.

1.5. WHEN a harness mapper is given native input THEN it SHALL be a pure conversion from that input
to DTOs, so that it can be unit-tested without a file system, a clock or a database.

### Requirement 2 — No JSON in the database

**User Story:** As the KyberDash owner, I want every kept field to be a typed column, so that no
reader can fail on a JSON value of an unexpected shape.

#### Acceptance Criteria

2.1. WHEN the store schema is created THEN no column SHALL hold JSON text, and every column SHALL be
declared with one of the SQLite types `INTEGER`, `REAL` or `TEXT`.

2.2. WHEN the store schema is created THEN it SHALL contain none of today's JSON columns
(`tokens_json`, `content_json`, `cost_json`, `measurability_json`, `parts_json`, `raw`,
`summary_json`, `turn_json`, `payload`, `parent_linkage_json`, `error_bar_json`,
`evidence_links_json`, `raw_payload`, `attributes_json`, `resource_json`, `scope_json`) nor any
replacement column that holds serialized structure.

2.3. WHEN the store adapter writes or reads a row THEN it SHALL NOT serialize or deserialize JSON
for any column value.

2.4. WHEN a harness value the collector reads (a message, a content part, a tool input, a tool
result or any other field) is a JSON document, either a native object or array or a text whose
trimmed whole value parses as one, THEN the collector SHALL keep only the fields on the design's
closed defined-field list, as typed columns, and the measurements of the original (sizes, measured
tokens, fingerprint). It SHALL drop the rest of the document with the reason recorded in the field
register. It SHALL NOT store the document text, nor any generic path/value decomposition of it, in
any table (RQ-2).

2.5. WHEN the schema DDL changes THEN a unit test over the DDL text SHALL fail if any column is
declared as `BLOB`, is named with a `_json` suffix, or is named `payload` or `raw`.

2.6. WHEN the store adapter or the in-memory fake is asked to write a `TEXT` value that is a JSON
document THEN it SHALL refuse the write with a stable error code, so that no path can store JSON text.

2.7. WHEN a kept text value is not a JSON document, including prose that contains a JSON fragment,
THEN the collector SHALL store it unchanged as plain text, and neither the store nor the analytics
path SHALL parse it.

### Requirement 3 — Third normal form; "not reported" is a missing child row

**User Story:** As the KyberDash owner, I want optional and repeated values in child tables, so that
"not reported" is never confused with zero, empty or a flag.

#### Acceptance Criteria

3.1. WHEN a harness does not report a value it may report (token counts, cost, timing, context
window, reasoning tokens and similar) THEN the store SHALL hold no child row for that value, and
SHALL NOT write a null, a zero, an empty string or a status flag in its place.

3.2. WHEN a harness reports such a value THEN the store SHALL hold exactly one child row for it,
keyed to its parent, with every column of that row non-null.

3.3. WHEN a record has repeated items (content parts, tool calls, turns, evidence links, per-model
amounts) THEN the store SHALL hold one row per item in a child table, keyed to the parent and
ordered by a typed position column.

3.4. WHEN the schema is reviewed THEN every non-key column SHALL depend on the whole key of its own
table and on nothing but that key, and no table SHALL store a value derivable from other stored
values, except the projection tables named in Requirement 4.3.

3.5. WHEN a child table references a parent THEN the schema SHALL declare a foreign key with
`ON DELETE CASCADE`, so that deleting a session or record removes its children.

3.6. WHEN a cost is stored THEN it SHALL be a harness-reported amount only, and a cost priced from
published rates SHALL be computed by analytics at read time (RQ-3).

3.7. WHEN analytics needs to know whether a metric was measured for a record THEN it SHALL decide
from the presence of the metric's child row, and the store SHALL hold no measurability map (RQ-4).

### Requirement 4 — One schema for every table

**User Story:** As the KyberDash owner, I want every table the store holds today brought under the
one relational schema, so that no corner of the store keeps the old shape.

#### Acceptance Criteria

4.1. WHEN the design is written THEN it SHALL account for each of today's 22 tables (`records`,
`session`, `session_turn`, `run`, `execution`, `token_cache`, `quarantine`, `pending_logs`,
`quarantined_logs`, `enriched_logs`, `problems`, `ingest_log`, `metadata`, `harness_rollup`,
`finding`, `prediction`, `model_context_window_catalog`, `model_context_window_catalog_vendor`,
`metric_point`, `source_checkpoint`, `record_provenance`, `refresh_run`) by naming the table or
tables that replace it, or by removing it with its reason.

4.2. WHEN a table in the new schema replaces one of today's tables THEN it SHALL satisfy
Requirements 2 and 3.

4.3. WHEN sessions, runs, executions, harness rollups, findings or predictions are projected THEN the
projections SHALL be stored in typed tables rebuilt from the record tables, and a detector's own
fields SHALL be typed columns in a child table for that detector (RQ-5).

4.4. WHEN an operational value is kept today as a `metadata` key (settings, schema version, write
marker, projection state, clean stamp) THEN the new schema SHALL keep it as a typed column or typed
rows, and SHALL NOT keep a key-value table whose values carry serialized structure (RQ-6, RQ-9).

4.5. WHEN a span is quarantined or an OTLP log is held for enrichment THEN the store SHALL keep only
typed columns for it and SHALL NOT keep the raw span or log payload (RQ-9).

### Requirement 5 — Analytics reads only the schema, and every operation answers

**User Story:** As an agent calling the KyberDash MCP server, I want every analytics operation to
answer from the relational store, so that analytics no longer fails with an empty HTTP 500.

#### Acceptance Criteria

5.1. WHEN any analytics operation runs THEN the analytics path SHALL read only the relational
schema through the store interfaces, and SHALL NOT parse a harness format or JSON from the store.

5.2. WHEN each of the eight operations (`status`, `catalog`, `query`, `search-sessions`, `inspect`,
`findings`, `compare`, `outcomes`) is called over a store holding records of every mapped harness
THEN the engine SHALL answer HTTP 200 with a contract-v1 envelope.

5.3. IF an operation fails for any reason THEN the engine SHALL answer with a contract-v1 error
envelope that has a non-empty body and a stable error code, and SHALL NOT answer with an empty body
(RQ-10).

5.4. WHEN the projection is brought up to date before an operation and one session cannot be
projected THEN the engine SHALL record a problem for that session and SHALL still answer the
operation from the sessions that were projected.

5.5. WHEN analytics reports cost THEN it SHALL report a harness-reported amount where a child row
exists, a published-rate amount it computed where tokens and a rate exist, and a stated reason
otherwise, and SHALL never blend bases.

### Requirement 6 — Every harness field is mapped or dropped with a reason

**User Story:** As the KyberDash owner, I want a documented decision for every field each harness
produces, so that nothing is silently kept in a catch-all and nothing is silently lost.

#### Acceptance Criteria

6.1. WHEN a harness field has a meaning shared across harnesses THEN the collector SHALL map it to a
typed column of the shared schema.

6.2. WHEN a harness field has no shared meaning THEN the collector SHALL drop it, and the field
register SHALL name the field and the reason it is dropped.

6.3. WHEN the schema is created THEN it SHALL have no catch-all column, attribute bag or
entity-attribute-value table for harness fields.

6.4. WHEN a record is stored THEN the original harness payload SHALL NOT be stored with it.

6.5. WHEN a mapping changes THEN the affected history SHALL be re-ingested from the harness's own
files, and the store SHALL NOT be patched from stored payloads.

6.6. WHEN a harness mapper is added or changed THEN the field register for that harness SHALL be
updated in the same change, and it SHALL list every native field the mapper reads or drops.

### Requirement 7 — No migration: delete and re-ingest

**User Story:** As the KyberDash owner, I want the old database deleted and every harness's history
re-ingested, so that no legacy row with an old shape can reach the new readers.

#### Acceptance Criteria

7.1. WHEN the engine opens a store whose schema version is older than the relational schema's
version THEN it SHALL delete the database file and its `-wal` and `-shm` files, keep no copy of
them, create the new schema, and start a full re-ingest of every discovered harness source (OD-7).

7.2. WHEN the old store is deleted THEN the engine SHALL NOT read any row from it first.

7.3. WHEN the engine opens a store whose schema version is newer than the version it understands
THEN it SHALL refuse to open it with the existing refusal message and SHALL NOT delete it.

7.4. WHEN the old store is deleted THEN the engine SHALL log that it deleted the store, the path, the
old and new schema versions, that operator settings were reset to defaults, and which classes of
history re-ingest cannot recover (RQ-6, RQ-7).

7.5. WHEN the full re-ingest runs THEN it SHALL use the same history window the scheduled refresh
uses, and SHALL report per harness how many sessions and records it stored.

7.6. WHEN the deletion in 7.1 runs THEN history that reached the store only over OTLP SHALL be lost
without recovery, warning or a legacy copy; the owner accepted that loss (BL-1).

### Requirement 8 — Copilot CLI sessions carry model, tool and agent records

**User Story:** As the KyberDash owner, I want Copilot CLI sessions stored with their tool calls and
agent invocations, so that Copilot analytics is as complete as Claude's and Codex's.

#### Acceptance Criteria

8.1. WHEN local sources are discovered THEN discovery SHALL include Copilot CLI session files
alongside Claude Code and Codex, and every other harness the collector maps from local files.

8.2. WHEN a Copilot CLI session records a model call THEN the collector SHALL produce an
`llm.invoke` record for it.

8.3. WHEN a Copilot CLI session records a tool call THEN the collector SHALL produce a `tool.invoke`
record whose parent is the model call that requested it. The record SHALL carry the tool name, the
call identifier, the outcome and the start and end times the session reports. Its input and result
SHALL be mapped under Requirement 2.4 or 2.7.

8.4. WHEN a Copilot CLI session records a sub-agent invocation THEN the collector SHALL produce an
`agent.invoke` record with the agent name, whose parent is the tool call that spawned it. The
sub-agent's own records SHALL be linked to that record wherever the journal supplies a join key, and
where it does not, they SHALL carry no link rather than an inferred one.

8.5. IF a Copilot CLI session does not report a value (for example tool timing or per-call cost)
THEN the corresponding child row SHALL be absent (Requirement 3.1).

### Requirement 9 — The Copilot session detail page shows every field

**User Story:** As the KyberDash owner, I want the Copilot session detail page to show every field
the schema holds for a Copilot session, so that I can check what each field contains.

#### Acceptance Criteria

9.1. WHEN the owner opens a Copilot session's detail page THEN it SHALL show every column the
schema holds for that session, its turns, its records, their content parts, tool calls, agent
invocations, tokens, cost and timing.

9.2. WHEN a child row is absent THEN the page SHALL show the field as "not reported" and SHALL NOT
show zero or an empty value in its place.

9.3. WHEN the schema gains or loses a column that a Copilot session can hold THEN the page SHALL be
updated in the same change, and a unit test SHALL fail if a schema field for a Copilot session has
no place on the page.

9.4. WHEN the page renders a field THEN it SHALL label it with the schema's table and column name,
so that the owner can match what is shown to what is stored.

### Requirement 10 — Unit tests over in-memory fakes, never the database

**User Story:** As the KyberDash maintainer, I want every behaviour proven by unit tests over
in-memory fakes, so that the suite is fast and the analytics fault cannot hide behind a mock again.

#### Acceptance Criteria

10.1. WHEN a test exercises the store THEN it SHALL use an in-memory fake of the store interfaces,
and no test SHALL open, read or write a SQLite database file.

10.2. WHEN the test suite runs THEN it SHALL contain no integration test for this feature.

10.3. WHEN analytics behaviour is tested THEN the test SHALL run the real analytics service and
engine over the fake store, and SHALL NOT replace `IAnalyticsService` or the engine with a mock.

10.4. WHEN the store adapter's SQL is tested THEN the test SHALL check the statements it sends
through a fake of the `sqlite3` process seam, without a database file (RQ-8).

10.5. WHEN the fake store is used THEN it SHALL implement the same store interfaces as the SQLite
adapter and SHALL enforce the same keys, foreign keys and "absent child row" rules, so that a test
cannot store what the real schema would reject.

### Requirement 11 — Synthetic records for every harness, op and kind; every operation tested

**User Story:** As the KyberDash owner, I want each harness mapping and every analytics operation
tested over synthetic records shaped like what each harness really produces, so that real data does
not break what tests pass.

#### Acceptance Criteria

11.1. WHEN a harness mapper is tested THEN its fixtures SHALL be synthetic native records whose
shapes mirror what that harness really produces, and SHALL contain no data copied from a real
session.

11.2. WHEN the fixtures are assembled THEN they SHALL cover every harness identity the collector
maps, which includes the 17 harnesses in today's store (RQ-1).

11.3. WHEN the fixtures are assembled THEN together they SHALL produce records with each op
`llm.invoke`, `tool.invoke` and `agent.invoke`, and each kind `internal` and `client`.

11.4. WHEN the fixtures are assembled THEN they SHALL include the value shapes that broke today's
reader: numbers and booleans where text is expected, missing optional values, and values present
for one harness but not another.

11.5. WHEN each of the eight analytics operations is tested THEN it SHALL run over the records all
harness fixtures produce, and SHALL assert an HTTP 200 contract-v1 envelope and the operation's
expected values.

11.6. WHEN a harness mapper is tested THEN the test SHALL assert every DTO property the mapper
fills and SHALL assert that every unreported optional value produces no child row.

11.7. WHEN a harness's fixtures are written THEN they SHALL contain only the event types, ops,
kinds and fields that the design's per-harness support matrix cites evidence for. Among them SHALL be
tool inputs and results that are JSON documents (asserting that only defined fields and
measurements are stored), JSON documents nested in string values, and plain text that contains a
JSON fragment (RQ-2, RQ-12).

### Requirement 12 — Documentation and closeout

**User Story:** As a maintainer reading KyberDash's documentation later, I want the schema, the
field register and the owner's decisions in canonical documents, so that this specification can be
archived without losing them.

#### Acceptance Criteria

12.1. WHEN the feature is delivered THEN the KyberDash architecture document SHALL describe the
relational schema, the collector boundary and the analytics read path.

12.2. WHEN the feature is delivered THEN a reference document SHALL hold the per-harness field
register (mapped column, or dropped with reason).

12.3. WHEN the feature is delivered THEN an ADR SHALL record decisions OD-1 to OD-9 and the
autonomous decisions that held.

12.4. WHEN every task is done THEN a final `docs-dev` closeout task SHALL harvest the design into
canonical documentation and archive this specification.

## Design harvest

The specification's items H1–H7 are declared in [the specification](README.md#design-harvest) and mapped there. This row traces this document's own content.

| ID | Design item | Target component | Target doc type | Harvested |
|---|---|---|---|---|
| RH-1 | Requirements 1–12 as delivered: typed collector DTOs, no JSON columns, 3NF child tables, full-table coverage, reader-only analytics, dropped-field register, delete-and-re-ingest | KyberDash | architecture | yes |

## Closeout mapping

| ID | Destination | Documentation impact | Status |
|---|---|---|---|
| OD-1, OD-2, OD-3, OD-4, OD-5, OD-6, OD-7, OD-8, OD-9 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md) | extends-component | archived |
| RQ-1, RQ-2, RQ-3, RQ-4, RQ-5, RQ-6, RQ-7, RQ-8, RQ-9, RQ-10, RQ-11, RQ-12 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md) | extends-component | archived |
| AP-R, AP-D1, AP-D2, AP-D3 | Documentation waiver — requirements, design-review, and drafting approvals consumed during specification authoring; no surviving behavioral invariant | extends-component | archived |
| BL-1, BL-2, BL-3 | [ADR 0090](../../../adr/0090-kyberdash-relational-store.md); [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
| RH-1 | [KyberDash architecture](../../../dash/architecture.md) | extends-component | archived |
