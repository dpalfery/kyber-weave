---
id: adr/0090-kyberdash-relational-store
title: KyberDash relational store replaces the v24 canonical JSON store
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-20
component: KyberDash
decided-by: []
supersedes: []
code-refs:
  - RelationalSchemaCatalog
  - CollectedBatch
  - IRelationalStoreWriter
  - IRelationalStoreReader
  - IProjectionStore
  - IOperationalStore
  - HarnessSourceRegistry
  - SqliteRelationalStore
  - ExecuteSqliteCore
  - LegacyTableReplacementMap
---

# ADR 0090: KyberDash relational store replaces the v24 canonical JSON store

## Status

Accepted, 2026-11-20. Records the owner decisions (OD-1..OD-9, BL-1..BL-3),
requirements decisions (RQ-1..RQ-12), design decisions (DD-1..DD-17), and plan
decisions (D2, D3, D5, D11, D13, D14, D15) of the relational-store plan
(`docs/plans/2026-11-19-kyberdash-relational-store.md`) and its specification
(`docs/specs/kyberdash-relational-store/`), both archived at closeout.

## Context

The v24 canonical store (`CanonSchema`, `CanonStoreRepository`,
`SCHEMA_VERSION = 24`) kept harness data as JSON payloads: `records` held
`tokens_json`, `content_json`, `cost_json`, `measurability_json`, `raw` and
`parts_json`, and projection, operational, and quarantine tables held further
payload columns. Analytics parsed harness formats and JSON at read time, a
type mismatch could fail a whole operation with an empty HTTP 500, and the
owner could not inspect what any single field held.

The owner decided (2026-11-18) to replace it with a third-normal-form
relational SQLite schema driven by one static catalog, mapped from one
collector DTO set, with the old database deleted unread and history
re-ingested from harness files. The owner's store held 17 harness ids
(`antigravity`, `antigravity-cli`, `claude-code`, `claude-desktop`,
`claude-unclassified`, `codex-desktop`, `commandcode`, `copilot`,
`copilot-agent`, `copilot-cli`, `cursor`, `cursor-agent`, `droid`,
`kilo-shared-runtime`, `opencode`, `pi`, `zcode`; BL-2) with ops
`llm.invoke` / `tool.invoke` / `agent.invoke` and kinds `internal` /
`client`.

## Decision

1. **The collector converts each harness format into one repository-layer DTO set (OD-1, DD-12, D11).**
   `CollectedBatch` and its DTOs (`CollectedSession`, `CollectedRecord`,
   `CollectedToolCall`, `CollectedContentPart`, `CollectedAgentInvocation`,
   `CollectedModelUsage`, `CollectedCost`, `CollectedError`,
   `CollectedTokenCount`, and related types) are the only values the store
   accepts. They are declared in `KyberDash.Domain/Collector/CollectedTypes.cs`
   (assembly `KyberDash.Domain`) while keeping
   `namespace KyberDash.Data.Collector;` (D11), so `IRelationalStoreWriter` in
   `KyberDash.Contracts` references `CollectedBatch` without depending on
   `KyberDash.Data`. DD-12's original `KyberDash.Data` placement is superseded
   by D11. `LegacyTableReplacementEntry` is unsealed to satisfy the
   no-sealed-types architecture rule.

2. **No column holds a JSON document, in any form (OD-2, RQ-2, DD-3).**
   `JsonDocumentText` / `ToolContentMeasurer` reject any `TEXT` value whose
   trimmed whole text parses as a JSON object or array, in both
   `SqliteRelationalStore` and the in-memory fake (`STORE_JSON_TEXT_REFUSED`).
   From a JSON document the mapper keeps only (a) fields on the closed
   15-entry `DefinedFieldList`, each as a typed column, and (b) measurements
   of the original a detector uses (UTF-8 bytes, characters, measured tokens,
   one-way SHA-256 fingerprint). Everything else is dropped with its reason in
   the field register. Plain prose that merely contains a JSON fragment stays
   plain text; non-text content (images, binary) is dropped with a reason.
   The earlier generic path/value leaf decomposition was rejected at design
   review as a catch-all that conflicts with OD-6 and could rebuild the
   payload.

3. **The schema is third normal form; absence means not reported (OD-3, DD-2, DD-4, DD-5, DD-13, DD-14, DD-15, RQ-4).**
   Every column is `NOT NULL`; a value a harness may not report (tokens, cost,
   timing) lives in a child table keyed by the parent key, and repeated items
   (content parts, tool calls, turns) are one row per item. Token counts are
   one row per disjoint class (`input`, `cache_read`, `cache_write`,
   `output`, `reasoning`) with harness inclusive totals in a separate
   `record_reported_total` child; timing is `record.started_at` plus an
   optional `record_end` row with read-time duration; a projection verdict of
   "not measurable, because …" is an exclusive `_unmeasurable` reason row; a
   harness value simply not reported has no row at all. Projection tables hold
   only outputs not derivable by plain aggregate (context pressure, ranking,
   timeline marks, run/execution linkage, rollup metrics, findings,
   predictions); plain aggregates (token totals, counts, model/tool/request
   lists, reconciliation, priced cost) are computed at read time by
   `IRelationalStoreReader`, reached through the dictionary-free
   `IModelPricingLookup` seam (D13). Tool definitions keep `(name, server,
   schema_tokens)` with tokens counted once at ingest; the JSON-schema text is
   dropped. The per-record measurability map is gone: presence of the child
   row is the signal (RQ-4).

4. **One static catalog describes all 22 tables and generates the DDL (OD-4, DD-1, DD-9, DD-10, RQ-5, RQ-9).**
   `RelationalSchemaCatalog` is the single source for record, session,
   projection, operational, and catalog tables; the detail view and structure
   tests read it, so hand-written DDL cannot drift. Sessions, runs,
   executions, rollups, findings, and predictions are typed tables rebuilt from
   records (RQ-5). OTLP log tables (`pending_logs`, `quarantined_logs`,
   `enriched_logs`) are removed; an OTLP log is mapped to collector DTOs on
   receipt and an unmappable log counts in `ingest_problem` (DD-10, DD-17b).
   Operational `metadata` key-values become typed tables and rows
   (`store_state`, projection dirty sessions, session-key map); `quarantine`
   keeps typed columns with no raw payload (RQ-9). The store stamps schema
   version 25 (`> 24`, D14); a newer version is refused, and `DetectorVersion`
   and `WalSizeLimitBytes` live on `SqliteRelationalStore` without a
   `CanonSchema` dependency.

5. **Analytics reads only the schema and never fails an operation empty (OD-5, RQ-10).**
   The analytics path (`AnalyticsService`, `KyberBridgeEngine`) depends only
   on `IRelationalStoreReader` / `IProjectionStore`. Because the store holds
   typed columns, read-time type mismatches cannot occur; any remaining
   failure is answered with a contract-v1 error envelope with a non-empty
   body and stable code, never an empty 500.

6. **Every harness field is mapped or dropped with a reason; no catch-all (OD-6, DD-16, DD-17, BL-3, D2, D3).**
   The per-harness field register (`docs/dash/field-register.md`) maps each
   native field to `table.column` or records why it was dropped. Every
   evidenced pre-`fc80ddf9` reader is ported test-first from the deleted
   TypeScript providers; a field no deleted provider read and no public format
   document shows is not mapped. Codex covers both `local_shell_call` /
   `local_shell_call_output` file shapes and the OTLP `codex.tool_result`
   event mapped on receipt using only documented attributes (DD-17). Copilot
   CLI is read from `events.jsonl` using only documented event shapes
   (DD-7); a sub-agent parent link is taken only from a proven
   `subagent.started.toolCallId` spawn and fixture-proven `agentId` join, never
   a time window (DD-8). `zcode` and any unknown id with no evidenced format
   are typed as `SOURCE_FORMAT_UNEVIDENCED` ingest problems, never silent
   zero rows (D2); `opencode`, `copilot`, and `copilot-agent` are likewise
   non-recoverable over local files. `windsurf` and `aider` are excluded by
   the owner (BL-3): no reader, mapper, or fixture, and their names are
   pruned from `CleanService.KnownHarnesses` and
   `CanonRollupsEngine.CanonicalRegisteredHarnesses` while
   `NormalizeHarnessName` alias folding (`claude-desktop -> claude-code`,
   `cursor-agent -> cursor`) is preserved (D2, D3). `antigravity-cli`
   discovery also reads `cache/antigravity-statusline.jsonl` (D2).

7. **The old database is deleted unread and history is re-ingested (OD-7, BL-1, DD-9, D14, D15).**
   Opening a store whose version predates 25 reads zero data rows, deletes
   `canon.db`, `-wal`, and `-shm` without copy or backup (tolerating absent
   `-wal`/`-shm`), logs the path, old and new versions, settings reset, and
   non-recoverable classes, then creates the fresh schema, stamps v25, resets
   operator settings to defaults, and triggers full re-ingest via
   `onRebuiltFromOlderSchema` / `TryTakePendingRebuildReingest()`. OTLP-only
   history with no harness file behind it is lost and that loss is accepted
   without a legacy copy (BL-1, RQ-7). The file-system and rebuild trigger go
   through the narrow `IRelationalStoreFileSystem` seam (create/delete only,
   no read/copy/move) with an injectable log action, so both paths run over
   fakes (D15).

8. **The Copilot session detail page exposes every stored field (OD-8, D5).**
   `GET /api/kyber/session/{sessionKey}/stored-fields` is served inside
   `/api/kyber` in `DashEndpointMappings` (display-layer rule), backed by a
   read-only `StoredFieldsView` over the catalog, and mounted as
   `StoredFieldsPanel` on both `AgentSessionDashboard` and `RunDetail`.

9. **Operator settings live in typed operational tables and reset on rebuild (RQ-6).**
   Refresh cadence (default 5 minutes), dashboard port (default 4747), and
   pause state are `operator_setting_*` rows; deleting the old database resets
   them to defaults rather than carrying them over. Corrupt non-boolean /
   non-integer SQLite cells fail closed; out-of-range reads are clamped to
   their defaults at the repository boundary.

10. **Cost is stored harness-reported only; published cost is read-time (RQ-3, DD-6).**
    `record_reported_cost(amount, currency)` rows hold what the harness
    reported; published-rate pricing is derived from tokens and the rate table
    at read time and never stored. Copilot's `cost` multiplier and nano-AIU
    are not currency and are dropped with that reason. Only costs with a
    stored basis are stored; "no rate", "out of scope", and "not billed" are
    analytics outputs.

11. **Tests are unit tests over in-memory fakes plus a fake-process SQLite seam — the recorded ADR 0089 exception (OD-9, RQ-1, RQ-8, RQ-11, RQ-12, DD-11).**
    No test opens a SQLite file. Store-interface suites run against
    `InMemoryRelationalStore`; adapter SQL is verified by overriding
    `ExecuteSqliteCore` in a test subclass that records statements and
    returns canned output, with DDL checked as text; real-file behaviour is
    confirmed by the owner through re-ingest and the Copilot detail page
    (OD-8). This conflicts with [ADR 0089](0089-every-behaviour-is-testable-through-a-seam.md)
    decision 4 (one contract suite run against the fake and the real adapter
    in CI): OD-9 is the owner's explicit later decision for this feature, so
    it wins here and the conflict is recorded, not resolved. Every mapped
    harness gets evidenced synthetic fixtures producing only the ops, kinds,
    and fields its native format is shown to carry (RQ-1, RQ-12); between them
    the fixtures cover all three ops and both kinds. This run specified only;
    implementation followed test-first later (RQ-11).

## Alternatives Considered

- **Migrate or copy the v24 database.** Rejected by the owner (OD-7): delete unread and re-ingest.
- **Keep OTLP-only history via a legacy copy or sidecar.** Rejected (BL-1): the loss is accepted outright.
- **Generic document / path-value tables for arbitrary JSON.** Rejected at design review (DD-3): a catch-all for arbitrary input that conflicts with OD-6 and can rebuild the payload.
- **Nullable columns with null meaning "not reported".** Rejected (DD-2): absence of the child row is the signal.
- **Store published-rate cost with a basis flag.** Rejected (RQ-3, DD-6): a derivable value is not stored.
- **Contract suite against a real SQLite file per ADR 0089.** Rejected for this feature (OD-9, RQ-8): the fake-process seam plus owner-verified re-ingest is the check, with the conflict recorded.

## Consequences

- `dash/server/` stores exactly one schema (v25), generated from
  `RelationalSchemaCatalog`; structure tests fail on a `NULL`-able column, a
  JSON/document column, or a catalog/field-register drift.
- The v24 `CanonSchema` / `CanonStoreRepository` / `FakeCanonStoreRepository`
  line is deleted; `LegacyTableReplacementMap` plus a legacy-absence scan
  proves no reference remains.
- Analytics, projections, and the stored-fields view read typed interfaces
  only; no reader parses harness formats or JSON at query time.
- `zcode` stays supported-but-unevidenced until format evidence lands; its
  reader task waits on evidence and is not dropped. `windsurf` and `aider`
  report nothing.
- The ADR 0089 exception is scoped to this store: the fake-process seam is the
  adapter check here, and the conflict stays recorded in RQ-8 rather than
  becoming a general precedent.

## Related

- [KyberDash architecture](../dash/architecture.md) — collector boundary, mapping pipeline, and read path
- [Field register](../dash/field-register.md) — per-harness field dispositions and the `zcode` unevidenced note
- [ADR 0084](0084-kyberdash-csharp-engine.md) — the C# engine and v24 store this record replaces
- [ADR 0089](0089-every-behaviour-is-testable-through-a-seam.md) — the contract-suite rule this store takes an exception to (decision 11)
- [ADR 0008](0008-kyberdash-single-canonical-store.md) — the single-store rule, still in force
- [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md) — display layers stay unchanged; the panel mounts on both session views
