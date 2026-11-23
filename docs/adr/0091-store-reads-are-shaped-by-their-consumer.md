---
id: adr/0091-store-reads-are-shaped-by-their-consumer
title: Store reads are shaped by their consumer
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-22
component: KyberDash
decided-by: []
supersedes: []
code-refs:
  - IRelationalStoreReader
  - CanonSessionProjectorEngine
  - KyberBridgeEngine
  - SqliteRelationalStore
  - AllSessionRecordsForMaintenance
  - ProjectIncrementalForReads
  - SessionListFigures
  - TurnContentParts
  - StoredFields
---

# ADR 0091: Store reads are shaped by their consumer

## Status

Accepted, 2026-11-22. Records decisions D1, D3, D4, D5 and D11 of the store-read work of
2026-11-22. The interface restructure in decisions 1 and 2 was the owner's explicit choice
(D1, D5) over the bounded-read fix that was first planned.

## Context

On a 579 MB `canon.db` whose largest session held 485 MB of captured text, the dashboard was
unusable. Measured on that store: `GET /api/kyber/sessions` answered in 10.3 s rising to 26.5 s
and `GET /api/kyber/coverage` in 11.8 s rising to 52.9 s, while `GET /api/kyber/jobs` answered in
5 ms and `GET /api/kyber/settings` in 2 ms. The server was healthy; the reads were not.

Four properties combined into that funnel:

- Every dashboard GET ran a projection pass that read the whole store and hex-encoded the
  content of every record, on a store whose write marker advances every second or two under
  continuous capture.
- The sessions list then re-materialized each session two more times after that pass.
- Concurrent GETs stacked unlocked passes on the same store, compounding the cost.
- The engine's resident set reached 2.15 GB.

The defect was not one slow query. `IRelationalStoreReader` offered broad reads —
`Records(...)`, `GetRecord`, `ContentParts`, `GetSessionAggregate`, `ListSessionAggregates`,
`SessionTurnSummaries` — and each page took the broadest one it could reach, then filtered or
discarded most of the result in C#. Over-fetching was legal at review because no rule said a read
must match its consumer.

## Decision

1. **A store read is shaped to its consumer (D1, D11).** Every dashboard endpoint answers from one
   purpose-named read model whose result is the page's shape: `SessionListFigures`,
   `SessionDetailFigures`, `SessionSummaryFigures`, `TurnFigures`, `SessionRecordsContentFree`,
   `RecordContentFree`, `TurnContentParts`. A read whose result the caller then filters, subsets or
   half-discards is a defect: the query carries the shape, not the caller. A new page gets a new
   named read; it does not widen an existing one.

2. **Content text is read only where it is consumed, and only scoped (D5, D11).** The content
   columns — `content_part_text`, tool-call input text, tool-call result text — are selected by
   exactly three paths: the content view, by coordinate through
   `TurnContentParts(sessionKey, turnIndex, spanId, part, budget, full)`; the stored-fields
   inventory, for the one session named; and the projection rebuild, for the session being
   rebuilt. Selecting a session never materializes its content, and selecting a turn never
   materializes the session.

3. **Maintenance may scan the corpus; a request path may not.** Whole-store and whole-session
   reads exist, under maintenance names that cannot be mistaken for a page read:
   `AllSessionRecordsForMaintenance()`, `SessionRecordsForRebuild(sessionKey)` and
   `SessionAttributions()`. `AllSessionRecordsForMaintenance()` is reserved for `kyber build`, a
   wildcard projection rebuild and doctor.

4. **The broad read is retired, not switched off (D1, D5).** `Records(...)` and the other broad
   members are removed from `IRelationalStoreReader` and from all four implementers. There is no
   `includeContent` parameter: an optional switch still lets a caller forget to set it and
   silently materialize a store, while a purpose-named interface makes the wrong read
   unrepresentable.

5. **Read-path projection is deferred by size and single-flight (D3, D4).** `ProjectIncremental()`
   is the maintenance entry (the scheduler tick, `kyber build`, refresh, clean) and never defers.
   `ProjectIncrementalForReads()` is the request-path entry: it reads per dirty key, skips a dirty
   session whose record count exceeds a budget (default 2,000 records, constructor-injectable),
   leaves that key dirty and reports it as `deferredSessions` in projection health while `status`
   stays `"ok"`. A single-flight guard inside the projector pass entry makes a concurrent caller
   return a skipped report and serve the current projections rather than stack a second pass.

## Alternatives Considered

- **Aggregate every figure in SQL, on the database side.** Rejected: the surface is large (one
  hand-written aggregate per page, per dimension) and parity-hostile against
  `InMemoryRelationalStore`, which would have to reproduce SQL aggregation semantics in C# for
  every unit test. The read models compute over queued rows instead, which the fake can answer
  with the same code the adapter does.
- **Read figures only from precomputed projection rows.** Rejected: it contradicts the relational
  store's read-time dual-spend design (ADR 0090 decisions 3 and 10), where a published cost is
  priced at read time against current rates. Storing figures would freeze today's rate into
  yesterday's row.
- **Keep the broad read and add an `includeContent` switch.** Rejected by the owner (D1, D5). It
  treats over-fetching as the caller's responsibility, keeps the broad read alive for every page
  that does not flip the switch, and leaves the defect's class in place. It was the first plan;
  the full restructure replaced it.
- **Page-shape only the four session paths, or ship the bounded-read fix alone.** Rejected (D1):
  both leave a broad, over-fetching read behind every unmigrated page, which is the same class of
  defect one page later.

## Consequences

- **The read-model register** in
  [the KyberDash architecture](../dash/architecture.md#read-path-invariants-and-the-read-model-register)
  lists every dashboard endpoint, the one read that answers it, and whether it reads content text.
  It is the contract a new page is written against.
- **The maintenance-read boundary** is enforced by
  `ArchitectureBoundaryTests.RequestPathsCannotReachMaintenanceReads`, which scans `dash/server`
  and fails, naming the file, when anything outside the contract, the SQLite adapter, the
  projector, the CLI entry point and the doctor names a maintenance read.
- **The interface shape** is enforced by
  `StoreInterfaceShapeTests.TheReaderInterfaceCarriesNoBroadReads`, which fails if a broad read
  returns to `IRelationalStoreReader`.
- **The endpoint sweep** `AnalyticsReadPathTests.EveryOtherPathLeavesContentUnread` covers 21
  endpoints over an instrumented fake and fails on the first materialized content byte: a path
  test measured 1,048,607 bytes before the list, detail and summary reads were shaped and zero
  after.
- **Read-path deferral** bounds GET latency by construction rather than by the store being small.
  Deferred sessions refresh at the maintenance cadence
  (`jobs.refresh_cadence_minutes`, default 5 minutes) and are visible to the Context Doctor through
  projection health.
- **Single-flight passes** serve slightly stale projections to a concurrent caller, which the
  polling UI already tolerates by design; waiting instead would make the second GET as slow as the
  first.

### Sanctioned exceptions

Three reads stay heavier than the page they serve, and each is named so a reader does not mistake
it for a defect:

- **`StoredFields(sessionKey)`** is a raw per-session inventory: it reads the named session's
  stored rows including content columns, and that inventory is the view. Scoping it to the session
  is the contract; making it cheap would change what it answers.
- **The maintenance reads** are whole-store or whole-session by definition, and
  `RequestPathsCannotReachMaintenanceReads` keeps every request path off them.
- **The projection rebuild** reads content for the sessions it rebuilds, because token counting,
  timeline derivation and findings consume the part text.

Nothing else on a request path may read a content-text column. Widening one is a change to this
record, not a local decision.

### Costs and limits, stated honestly

- A deferred session's figures lag capture by up to one maintenance tick. It is surfaced as
  `deferredSessions`, not hidden.
- The session-detail payload no longer carries request prompt previews, because the detail read is
  content-free. The prompt text is available from the scoped content endpoint, so restoring the
  preview is a display-layer change, not a store read.
- Some aggregate-only figures (context-bucket bounds and the unused-schema range) lost their last
  assertion when the broad aggregate was retired; no page renders them.

## Related

- [KyberDash architecture](../dash/architecture.md#read-path-invariants-and-the-read-model-register) — the read-model register, the maintenance-read boundary, deferral and single-flight, and the worked example of the rules
- [KyberDash runbook](../dash/runbook.md#11-slow-dashboard-responses-on-a-large-store) — what an operator sees on an oversized store, including deferred sessions
- [SQLite standard](../standards/sqlite/sqlite.standards.md#store-reads-are-shaped-to-their-consumer) — `KW-STD-SQL-001` to `KW-STD-SQL-003`
- [Test standard](../standards/test/test.standards.md#read-path-tests-assert-the-shape-of-the-reads) — `KW-STD-TEST-001` and `KW-STD-TEST-002`
- [`REVIEW.md`](../../REVIEW.md) — `R17`, the two questions a reviewer answers about a read path by reading the diff
- [ADR 0090](0090-kyberdash-relational-store.md) — the relational store whose read-time figures this record shapes, and the dual-spend design that rejected precomputed-only reads
- [ADR 0089](0089-every-behaviour-is-testable-through-a-seam.md) — prose enforcement through guidance and review, which the shape assertions extend into tests
