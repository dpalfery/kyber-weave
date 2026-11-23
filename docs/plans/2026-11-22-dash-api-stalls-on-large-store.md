---
id: plans/2026-11-22-dash-api-stalls-on-large-store
title: Bound KyberDash dashboard read latency on large canon stores
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-22
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberDash
---

# Bound KyberDash dashboard read latency on large canon stores

**Status: Ready, 2026-11-22.**

## Problem and goal

The dashboard stalls on a large store. Against the live engine the investigator measured
`/api/kyber/sessions` at 10.3 s growing to 26.5 s and `/api/kyber/coverage` at 11.8 s growing to
52.9 s, monotonically worsening, while OTLP capture continued (`/api/kyber/jobs` 5 ms and
`/api/kyber/settings` 2 ms stayed fast, so the server itself was healthy). The store is 579 MB,
of which one Antigravity session holds 485 MB of captured text across 42,496 rows.

Three code defects compound (all verified at `b0b0be34`, the merge base — this is not a
regression from the run branch):

1. **Every dashboard GET runs a synchronous projection pass that materializes the whole store.**
   `KyberBridgeEngine.EnsureProjected` (`dash/server/KyberDash.Application/KyberBridgeEngine.cs`)
   finds the projection permanently dirty under continuous capture (write marker advances every
   1-2 s) and calls `CanonSessionProjectorEngine.ProjectIncrementalRelational`, whose first read
   is an **unfiltered** `reader.Records()` — every content part of every session, hex-encoded
   (~1 GB of strings per pass over this store, `Pooling=false` fresh connection per call). The
   pass then skips the clean sessions, having paid for all of them.
2. **The sessions list re-materializes each session twice more.** `GetSessions` calls
   `ReadSessionFigures` per projected row, which runs `GetSessionAggregate` (full
   `Records(sessionKey)` including content text) **and** `SessionTurnSummaries` (a second full
   materialization whose result the list discards).
3. **Concurrent GETs run concurrent passes** with no single-flight guard, so repeated polls
   multiply the cost and the LOH churn (engine RSS reached 2.15 GB).

Goal: the dashboard's read surface is restructured so that **every request-path read is purpose-named and scoped to its page** — one read model per dashboard endpoint, produced by one purpose-named store method whose shape is the view's shape. No request path can reach content text except the content view and the stored-fields inventory, both scoped. Selecting the 10,624-turn Antigravity session after this fix is fast, because nothing on the list, detail, summary, or turn path reads its 485 MB of text — that is an explicit acceptance criterion. The projection pass reads only the sessions it rebuilds, defers oversized dirty sessions off the read path, and is single-flight. `Records(...)` and the other broad reads are retired from `IRelationalStoreReader`, not switched off; a whole-store read survives only under a maintenance name (`kyber build`, wildcard projection rebuild, doctor) that nothing on a request path may call. Every session figure stays byte-for-byte unchanged — parity is pinned on the old broad reads before they are removed.

**The operator stopgap was declined.** The human was offered cleanup of the oversized session and chose to wait for the code fix: no `dash clean`, no deletion, no backup-then-clean. The 579 MB store stays exactly as it is through this work, the dashboard remains slow on it until this fix ships, and every pre-fix measurement in this plan remains valid against that store. Nothing in this plan assumes a cleaned store. This must also be stated plainly in the pull request description.

**What honestly remains whole-store (or whole-session) after this change:** the maintenance-named full-rebuild read (wildcard projection passes, `kyber build`, doctor) — deliberate, off the request path, and named so it cannot be mistaken for a page read — and the per-session `StoredFields` raw inventory, which by design reads the named session's stored rows including content columns. Selecting stored-fields on the 485 MB session therefore remains heavy; that is the inventory's purpose, and the runbook entry says so. Every other path is scoped to a page's key.

**Scope change (2026-11-22, human decision).** The human was offered three depths for the data-access restructuring — page-shaped for the four session paths only, the bounded-read fix as originally planned, or a full interface restructure now — and chose **"Full interface restructure now"**: every `IRelationalStoreReader` read becomes purpose-named and all ~30 call sites migrate in this change. The original plan's conservative read shape (keep `Records(...)` broad, add an `includeContent` switch) is superseded; the revision history is recorded in Decisions D1 and D5.

Out of the direct path but discovered while planning: the relational
`TranscriptIngestionService.PurgeExpiredContent` is a stub that records a purge mark and purges
nothing, while `docs/dash/runbook.md` §4 and ADR 0018 promise a rolling 14-day content purge.
That gap is routed to the pull request notes (D7), not this fix — the human's 485 MB session is
inside the retention window and actively captured, so purge would not touch it, and the stall
is the read path.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| "the dashboard does not load. can you troubleshoot why?" — fix the diagnosed defect | Problem and goal; T1–T8 | Each of the three verified code sites has a task, and the read-shape restructure removes the whole class: the read-model table maps every endpoint to a purpose-named, scoped read; the Test contract proves non-content paths never issue a content read. |
| "it is stalling now, how much more capture do you need" | D3, T1 | Capture volume is the trigger, not the blocker: the read-path pass defers oversized dirty sessions, so GET latency is independent of capture rate; deferred sessions lag by the maintenance cadence and surface in projection health. |
| Plan only, do not implement | This document | No application code was changed by the architect; work happens in the run worktree `factory/fix/dash-api-stalls-on-large-store` only. |
| Turn the diagnosis into a persisted plan; do not re-investigate from scratch | Investigation findings | The handed-over diagnosis is used as established; only the code sites the design needs were re-verified (worktree CodeGraph index at `b0b0be34`). |
| Human chose "Full interface restructure now": every read purpose-named, all ~30 call sites migrate | D1, D5, D11, D12; Read models section; T1–T7 | The interface's 21 members are each disposed (kept purpose-named, superseded by a read model, or retired); the migration is sliced so every task lands contract + adapter + fake + endpoints for one page group and the tree stays compilable; the retirement task removes the broad members when the last caller is gone. |
| Human, immediately after that choice: "also we need to update our standards so slop like this does not happen again" | D13; T9, T10 | Five checkable rules with new permanent ids land in the SQLite and test standards and `REVIEW.md` (T9), and ADR 0091 records the durable decision (T10) — dedicated in-scope tasks, sequenced so the end-of-run reviewer of the implementation slices has them in hand; the investigation-finding gap ("the existing tests cannot see it") is closed by the test-shape rule, not observed and left. |
| A whole-store read, if it survives, is maintenance-named and unreachable from a request path | Read models section (maintenance reads); T1, T7 | `AllSessionRecordsForMaintenance()` (naming final by the worker) is the only whole-store record read, consumed by the projector's wildcard/full pass, `kyber build`, and doctor; T7 adds a boundary check that request-path types cannot reach it. |
| One read model per page, at the page's level; derive the endpoint list from the worktree | Read models section; T2–T6 | The endpoint list was derived from `DashEndpointMappings.cs` in the worktree (30+ routes enumerated in Investigation findings); each row states the model, fields, scoping key, and content policy. |
| Granularity is the page's: selecting a session must not materialize its content; selecting a turn must not materialize the session | Read models section; T2–T4; Test contract | List/detail/summary/turn read models are content-free; the content view reads one turn's or one span's parts by `(session_key, turn/span, position)`; the instrumented-fake tests fail if any non-content path touches a content read. Explicit acceptance: the 10,624-turn Antigravity session's detail path issues zero content-part-text reads. |
| Tests pin the shape, not the clock; keep figure parity on the named seed shapes | Test contract | Every slice's row asserts read-method invocation over the fake (instrumented read log), never wall-clock; parity rows seed a token-less session, a multi-harness session, an empty session, and a session with content parts, comparing new read models against the old broad reads before retirement. |
| The human declined the stopgap; do not plan around a cleaned store | Problem and goal (declined-stopgap paragraphs); D7; T8 | No cleanup step exists anywhere in the plan; the runbook entry documents behaviour on an oversized store, not recovery from one. |
| Caller migration bounded and reviewable; order tasks by latency impact; no task leaves the tree uncompilable | D12; Tasks; Dependency graph | Vertical slices T1–T7 each land a compilable slice; the order is projection pass, sessions list, session detail/summary/turn, content, then remaining endpoints, analytics/report, retirement; implementers named per task (SQLite store, `InMemoryRelationalStore`, both `FaultingSessionReader` copies). |
| Say what this costs and what stays slow | Problem and goal; Out-of-scope boundaries | The dashboard stays slow on the live store until this ships (stated plainly, for the plan and the pull request description); the honest whole-store survivors are named with reasons. |
| Reassess D6 (the human's `EXISTS`→`IN` tweak) under the restructure | D6 | Reassessed: the tweak's target line is superseded by purpose-named queries, so it is likely moot — reasoning stated in the row so the human can decide whether to drop it. |
| Resolve autonomy as autonomous; default development-mode test-first | Decisions D8 | Autonomy and mode recorded; the human's restructure choice and stopgap refusal are recorded as explicit decisions with their provenance. |
| Open item: examine `content_purge_mark` / `content_retention` | D7; Out-of-scope boundaries | Examined: the tables exist and the mark is written, but the relational purge deletes nothing — PR note, not this fix. |
| Open item: runbook has no troubleshooting entry for slow API on a large store | T8 | docs-dev closeout adds the entry to `docs/dash/runbook.md`, written against an oversized store (no cleanup assumed). |
| Open item: should projection read content text at all | D2 | Projection reads content only for sessions it rebuilds; dashboard reads never read content text outside the content view and stored-fields. |
| Constraints: worktree-only writes, no full test suite, .NET via `dotnet`, test-first with RED/GREEN evidence | Tasks, Test contract, Verification gates | Task commands are the task-gate runs over unit projects only; every file-changing task has a Test-contract row with RED evidence. |

## Scale

`documentation-impact: extends-component`; `impact-components: KyberDash`.

**A full interface restructure of the KyberDash read contract.** `IRelationalStoreReader`
currently exposes 21 members (`Harnesses`, `Sources`, `CountRecords`, `GetRecord`, `Records`,
`TokenCounts`, `ReportedTotals`, `ReportedCosts`, `SkillsInvoked`, `ToolCalls`,
`ContentParts`, `ToolDefinitions`, `AgentInvocations`, `RecordSignals`, `GetSession`,
`Sessions`, `SessionModelUsages`, `StoredFields`, `GetSessionAggregate`,
`ListSessionAggregates`, `SessionTurnSummaries`); every one is kept as-is, superseded by a
purpose-named read model, or retired in this change. The ~30 reader call sites in
`dash/server/KyberDash.Application` migrate (concentrated in `KyberBridgeEngine` — 28 calls —
plus `CanonSessionProjectorEngine`, `CleanService`, `DoctorService`, the analytics and report
services; the compiler and `rg "reader\."` enumerate the remainder for each worker). The
implementers that must move with every interface change: `SqliteRelationalStore`
(`dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`),
`InMemoryRelationalStore`
(`dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`), and the two
test-local `FaultingSessionReader` copies
(`dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsReadPathTests.cs:663` and
`AnalyticsOperationsDetailTests.cs:329`).

**No canon schema change.** `RelationalSchemaCatalog` is untouched and
`SqliteRelationalStore.SchemaVersion` stays 25. Bumping it deletes every operator's `canon.db`
unread (the SQLite standard's delete-and-reingest rule) — including the human's 579 MB captured
history, which they have now explicitly declined to lose twice over. The restructure is
contract, adapter, fake, and application logic only: no new tables, columns, or indexes; every
new read is a query over the existing catalog.

## Read models per dashboard endpoint

Derived from `DashEndpointMappings.cs`, `KyberBridgeEngine.cs`, and `DashboardQueryService.cs`
in the worktree. Method names below are the plan's naming; the worker may align them to house
style, but each row's shape, scoping key, and content policy are the contract. Every model is
produced by one purpose-named store method whose result is the view's shape — no assembling
pages from broad queries in the bridge.

| Endpoint(s) | Read model (scoping key) | Fields | Content text |
|---|---|---|---|
| `/api/kyber/sessions` (and `/session` alias) | `SessionListFigures(harness?)` — one pass over all sessions | Per session key: harness id, workspace repo/branch, turn count, request count, token-total availability (input/output present — absent stays distinct from zero), published and reported cost with basis mismatch, model names, problems count. The bridge joins these with `session_projection` rows for label, hierarchy, and timestamps. | No |
| `/api/kyber/session/{id}` | `SessionDetailFigures(sessionKey)` | The detail header's figure set: summary block, problems, tools, servers, requests, reconciliation, subagents, and per-turn figure summaries (`ReadTimeTurnSummary` fields — spans, token classes, durations, dual costs). | No |
| `/api/kyber/session/{id}/summary` | `SessionSummaryFigures(sessionKey)` | The bounded summary the SPA's navigation test pins: harness, label inputs, totals, context status, cost status, problems count. | No |
| `/api/kyber/session/{id}/turn/{index}` | `TurnFigures(sessionKey, turnIndex)` | One turn's spans, tool calls, token facts, cost facts. | No |
| `/api/kyber/session/{id}/turn/{index}/content`, `/api/kyber/session/{id}/content` | `TurnContentParts(sessionKey, turnIndex/spanId, part, budget)` | One turn's or one request's content parts by `(session_key, turn/span, position)`, honouring the response budget and `full` flag exactly as today. | **Yes — the only request-path content-text read, scoped to that turn/span.** |
| `/api/kyber/session/{sessionKey}/stored-fields` | `StoredFields(sessionKey)` (exists; keep) | Raw per-session inventory of persisted table columns, rows, missing parent keys. | Yes — scoped to the named session, by design (an inventory); stays heavy on the 485 MB session and the runbook says so. |
| `/api/kyber/span/{id}/attributes` | `SpanAttributes(spanId)` | One span's metadata: trace/session/harness/source, name, op, kind, timestamp, token classes, cost, declared signals, raw attribute dictionary — plus the captured-prompt **reference**; if the SPA renders prompt text on this panel, the text comes from that single span's designated prompt part only, never at session scope. | No (single-part exception only if the SPA renders it, and only that span's part) |
| `/api/kyber/timeline` | `SessionTimeline(sessionKey?)` | Per-session timeline nodes (op, kind, parent, span ids, timestamps) from content-free record metadata. | No |
| `/api/kyber/coverage`, `/jobs`, `/settings`, `/meta`, `/version`, `/capture`, `/model-catalog`, `/predictions`, `/calibration`, `/review`, `/api/events` (SSE) | No relational-reader read (operational store, projection store, pricing, collector state); unchanged | — | No |
| `/api/kyber/runs`, `/run/{id}`, `/compare`, `/compare/runs` | Projection store only (`ListRuns`, rollups) — unchanged; verified no reader call | — | No |
| `/api/kyber/findings`, `/finding/{id}` | Projection store only (`ListFindings`) — unchanged; verified | — | No |
| `/api/kyber/problems`, `/problem/{id}`, `/quarantine`, `/quarantine/{spanId}` | Operational store (quarantine/problem tables) — unchanged; the quarantine detail's raw packet is the stored quarantine payload, not `content_part_text` | — | No |
| `/api/kyber/harnesses`, `/harness/{id}` | `Harnesses()` (exists; keep — purpose-named) plus rollups | — | No |
| `/api/kyber/report` | ReportService migrates to `SessionListFigures` / `SessionDetailFigures` | The report's figure columns. | No |
| `/api/kyber/analytics/*` | AnalyticsService migrates: session-level operations to `SessionListFigures`-shaped reads, span/turn-level detail operations to the scoped read models above | Per-operation shapes, unchanged output. | No |

**Maintenance reads (named so nobody mistakes them for page reads).**
`AllSessionRecordsForMaintenance()` replaces every whole-store use of `Records(...)`: the
projector's wildcard/full rebuild, `kyber build` (`CanonProjectionService.ProjectAll`), and
doctor. `SessionRecordsForRebuild(sessionKey)` is the projector's per-dirty-key full read (it
needs content). `SessionAttributions()` (distinct session-key/harness pairs with record
counts) feeds identities and skip decisions. `Sessions`, `Sources`, `CountRecords`,
`StoredFields`, `SessionModelUsages` stay: each is already purpose-named, cheap, and scoped.
The child-table query members (`TokenCounts`, `ReportedTotals`, `ReportedCosts`,
`SkillsInvoked`, `ToolCalls`, `ContentParts`, `ToolDefinitions`, `AgentInvocations`,
`RecordSignals`, `GetRecord`) are superseded by the read models or kept only where a named
maintenance consumer remains — T7 retires every member with no remaining purpose, enumerated
by the compiler.

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Fix strategy: page-shaped reads for the session paths, the bounded-read fix as originally planned, or a full interface restructure? | **Full interface restructure now** (the human's explicit choice): every `IRelationalStoreReader` read becomes purpose-named, one read model per dashboard endpoint, all ~30 call sites migrate in this change. **Revision (2026-11-22):** the original D1 (keep `Records(...)` broad, add `includeContent`) and the conservative read shape are superseded by this choice. | The rejected options — page-shaping only the four session paths, or shipping the bounded-read fix — left a broad, over-fetching read behind every unmigrated page, which is the defect's class. The restructure removes the class: over-fetching becomes unrepresentable on a request path, not a switch a caller must remember to flip. | **Human decision 2026-11-22** (offered three options, chose "Full interface restructure now"); recorded by architect |
| D2 | Should the projection pass read content text at all? | Yes, but only for sessions it actually rebuilds: per-dirty-key `SessionRecordsForRebuild(sessionKey)` (full content, needed for token counting, timeline, findings, previews). Session identities and skip decisions come from `SessionAttributions()`, never from materialized records. The wildcard/full path reads the whole store through the maintenance-named `AllSessionRecordsForMaintenance()`. | The projector's `CachedTokenCounter`, `CanonRecordHydration`, and `BuildStoredSessionProjection` consume part text, so rebuilt sessions need full records. `SessionIdentities` and `PriorIdsOf` only need (session key, harness) pairs — one grouped SQL query over `record` joined to `record_session_link`. Survives the scope change unchanged. | autonomous: low-confidence, no authoritative source — follows the existing incremental-pass design (ADR 0090's relational store) |
| D3 | A single huge, permanently dirty session still costs seconds per GET even after dirty-key filtering. Rate-limit, queue, or defer? | Defer by size on the read path only: the read-path pass skips dirty sessions whose record count exceeds a budget (default 2,000, injectable for tests), leaves them dirty, and lists them in the pass report; the background maintenance pass and CLI `kyber build` never defer. | Rate-limiting the sync pass leaves the worst-case GET equal to one full big-session rebuild (still seconds). Queueing reintroduces the stall. Deferral bounds GET latency by construction; deferred sessions refresh at the maintenance cadence (`JobSchedulerService.TimerLoopAsync`, operator-controlled `refreshCadenceMinutes`), and the Context Doctor sees them via projection health. Survives the scope change unchanged. | autonomous: low-confidence, no authoritative source |
| D4 | Concurrent passes? | Single-flight guard inside the projector pass entry (shared by the bridge's `EnsureProjected`, the scheduler's maintenance pass, and the CLI): a second concurrent caller returns a skipped report immediately and serves current projections. | Two unlocked passes over the same store were measured compounding the cost. Waiting (queueing) would make the second GET as slow as the first; skipping serves slightly stale projections, which the dashboard already tolerates by design (polling UI). Survives the scope change unchanged. | autonomous: low-confidence, no authoritative source |
| D5 | Keep `Records(...)` broad with an `includeContent` switch, or restructure? | **Retire the broad read; the interface change is the objective.** `Records(...)` is removed from `IRelationalStoreReader` once its last caller migrates; purpose-named read models replace it per page, and the only whole-store record read survives under a maintenance name unreachable from a request path. **Revision (2026-11-22):** the original D5 (optional `includeContent` parameter, default `true`) treated over-fetching as a switch and is superseded — the human rejected it when offered the choice. | A switch still lets a future caller forget to flip it and silently materialize a store; a purpose-named interface makes the wrong read unrepresentable. All ~30 call sites migrate in this change, sliced so the tree stays compilable (D12). | **Human decision 2026-11-22** (explicitly rejected the bounded-read option); recorded by architect |
| D6 | The human's uncommitted `EXISTS`→`IN` predicate tweak in the main checkout's `SqliteRelationalStore.Reader.cs` — reassessed under the full restructure. | **Likely moot.** The tweak edits the session-key predicate inside `Records(...)` (the `EXISTS` clause at `SqliteRelationalStore.Reader.cs:101`); this change retires `Records(...)` and replaces it with purpose-named queries that carry their own span→session filtering (joins over `record_session_link`), so the line the tweak optimizes is rewritten regardless. Whether the new per-session queries choose `EXISTS` or `IN` is the implementing worker's call. Recommendation to the human: drop the uncommitted tweak — this branch supersedes the code it optimizes; keeping it only buys interim relief on an unshipped path. | The tweak does not conflict semantically (it is a query-plan change, not a behaviour change), and if the human prefers to keep it until this ships, the eventual merge is a textual overlap in `SqliteRelationalStore.Reader.cs` that resolves by taking this branch's version. Stated plainly per the steer so the human can decide. | autonomous: low-confidence, no authoritative source — reassessed 2026-11-22 from the restructure's effect on `Records` |
| D7 | Retention: implement the promised 14-day content purge alongside this fix, or clean the store now? | No purge implementation, and **the stopgap was declined**: the human was offered cleanup of the oversized session (including backup-then-clean) and chose to wait for the code fix. The gap stays a pull request note; the runbook entry documents behaviour on an oversized store, never recovery from one. | The relational `PurgeExpiredContent` writes `content_retention_days` and the purge mark and returns `Purged: 0` — it deletes nothing, while `docs/dash/runbook.md` §4 and ADR 0018 document automatic purging. Fixing it is a separate defect in a flow this change does not touch. It would not help this store anyway: the 485 MB session is inside the window and actively captured. | **Human decision 2026-11-22** (declined "clear the oversized session", chose "No, wait for the code fix"); retention gap itself: autonomous, low-confidence, no authoritative source |
| D8 | Autonomy and development mode | `autonomy: autonomous` (Config Reg and `.kyber-weave/kyber-weave.yml`), `development-mode: test-first` (default; the human did not opt out). | Persisted next to the mode per the autonomous-decisions reference. The 2026-11-22 scope change is a human decision relayed by the conductor, recorded here with its provenance. | autonomous: given |
| D9 | Sessions-list read shape | `GetSessions` answers from one purpose-named `SessionListFigures(harness?)` read model (the list's own shape, content-free), joined with `ListSessionProjections` rows. Survives the scope change, upgraded from "batch the existing `ListSessionAggregates`" to the list's own read model. | One read model per page (D11); parity tests keep the figures identical to the old broad-read aggregates, seeded with the token-less, multi-harness, empty, and content-bearing sessions. | autonomous: low-confidence, no authoritative source |
| D10 | Data-model / contract impact | No schema or migration. `IRelationalStoreReader` is restructured per the read-model table: purpose-named members replace the broad ones; `ProjectionPassReport` gains an optional `DeferredKeys` parameter (default `null`). No new tables, columns, or indexes; `SchemaVersion` stays 25 — a bump deletes the human's 579 MB store unread, which they have explicitly declined to lose. | Ordering of `SessionAttributions()` is load-bearing: attribution-driven `SessionIdentities.Claim` must assign the same session ids as today's sorted-key record iteration, or every stored projection's id churns — pinned by test. | autonomous: low-confidence, no authoritative source |
| D11 | One read model per page, at the page's level | Every dashboard endpoint answers from one purpose-named store method whose shape is the view's shape (the read-model table). Selecting a session never materializes its content; selecting a turn never materializes the session; the content view reads one turn's or one request's parts by `(session_key, turn/span, position)`; only the content view and stored-fields may touch content text, both scoped. | The page-shaped contract is what makes the fix durable: a new page gets a new named read instead of reusing a broad one. Instrumented-fake tests enforce it (a non-content path that issues a content read fails), and T7's boundary check keeps request-path types off the maintenance read. | **Human decision 2026-11-22** (the restructure steer); granularity rules are the human's stated requirements, recorded verbatim in intent |
| D12 | How is a ~30-call-site interface migration kept bounded and reviewable? | Vertical slices: each task lands contract + SQLite adapter + fake + `FaultingSessionReader` copies + the endpoints for one page group, ordered by latency impact (projection pass, sessions list, session detail/summary/turn, content, remaining endpoints, analytics/report, retirement). Broad members are added-to (never removed) until the retirement task, so every slice compiles and its tests are meaningful on their own. | Removing members mid-sequence would leave the tree broken between tasks and force one giant review; additive-then-retire keeps each slice independently reviewable and lets the earlier tasks ship the latency wins first. | autonomous: low-confidence, no authoritative source — follows the repository's one-plan-one-PR slicing rule |
| D13 | The human's instruction: "also we need to update our standards so slop like this does not happen again" — how is it delivered? | In scope, as dedicated tasks (not a closeout judgement, not a PR note): T9 writes five checkable rules with new permanent ids into the SQLite and test standards and `REVIEW.md`; T10 writes ADR 0091 and registers it. Rule ids: `KW-STD-SQL-001` (a read is shaped to its consumer; a superset the caller filters in C# is a defect; whole-corpus scans only behind a maintenance-named method no request path calls; `StoredFields` is the sanctioned exception), `KW-STD-SQL-002` (content text columns only in the content view, the stored-fields inventory, and the projection rebuild, each scoped), `KW-STD-SQL-003` (a read's cost is proportional to its change set: per-dirty-key incremental reads, attribution pairs for skip decisions, bounded list query counts, single-flight or off-path shared refresh), `KW-STD-TEST-001` (a read path's test asserts which store reads it issues, over the fake's read log — the mechanical lever, no guard script or architecture test, per ADR 0089), `KW-STD-TEST-002` (narrowing a read requires parity against the read it replaces, seeded with absent-versus-zero; shape assertions, not timing — a fixture orders of magnitude smaller than production cannot prove latency, and these are in-memory assertions, so the 120-second budget is untouched), and `R17` in `REVIEW.md` class 4 — only the angles class 4 does not already carry (read shaping and single-flight), because its existing bullets already forbid the unbounded-materialisation, per-row-loop, and per-request-rebuild variants. | Every rule is checked against the approved design and carves out what the design keeps: the maintenance whole-store read, the heavy-by-design `StoredFields` inventory, and the projection rebuild's content read are named in the rules as sanctioned, so no rule outlaws the fix. The rules cite the shipped code as the worked example (the read-model register in `docs/dash/architecture.md`, `SessionAttributions`/`AllSessionRecordsForMaintenance` in `SqliteRelationalStore.Reader.cs`, the endpoint sweep and `RequestPathsCannotReachMaintenanceReads` boundary test), which is why T9 follows T7/T8: the citations must resolve when `docs drift` runs. T9/T10 land inside the run, before the end-of-run council — the reviewer of the implementation slices has the rules in hand, and no agent reviews a single task earlier than that. The ADR is mandatory and not waiverable: the decision constrains future work, had real rejected alternatives (DB-side aggregation, precomputed-table-only reads, the `includeContent` switch), and would be expensive to revisit. | **Human instruction 2026-11-22**, verbatim "also we need to update our standards so slop like this does not happen again", relayed by the conductor; homes, ids, carve-outs, and sequencing decided autonomously (low-confidence, no authoritative source — derived from the standards' and `REVIEW.md`'s existing forms) |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute — owner chose "Full interface restructure now", declined the store stopgap, and asked for standards updates "so slop like this does not happen again", 2026-11-22 |

## Design harvest

| ID | Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|---|
| H1 | KyberDash | runbook (`docs/dash/runbook.md`) | Common troubleshooting, new entry 11 ("Slow dashboard responses on a large store") | Slow API responses on a large store: symptom (shell loads, Context Doctor panel spins), cause (write marker ahead of projection state under continuous capture), the bounded-read invariants, operator actions (`refreshCadenceMinutes`, the owner's `dash clean` decision kept open, watching projection health for deferred sessions). |
| H2 | KyberDash | architecture (`docs/dash/architecture.md`) | Read-path invariants and the read-model register (new section) | The purpose-named read contract: one read model per endpoint with its scoping key and content policy (the read-model table), the rule that only the content view and stored-fields may read content text, the maintenance-read boundary (`AllSessionRecordsForMaintenance` unreachable from request paths), read-path deferral of oversized dirty sessions, and single-flight passes. |
| H3 | KyberDash | standards (`docs/standards/sqlite/sqlite.standards.md`, `docs/standards/test/test.standards.md`) and review guidance (`REVIEW.md`) | Store-read rules (new sections / class-4 extension) | The five rules with their permanent ids (D13): `KW-STD-SQL-001..003`, `KW-STD-TEST-001..002`, and `R17` — shaped reads, scoped content columns, change-set-proportional cost, read-shape test assertions, parity on narrowing — each citing the read-model register and the shipped code as the worked example. |
| H4 | KyberDash | ADR (`docs/adr/0091-store-reads-are-shaped-by-their-consumer.md`) | Read-shape contract | The durable decision (mandatory, not waiverable): reads are shaped by their consumer, content text is read only by the view or rebuild that consumes it, maintenance may scan the corpus while request paths may not, and read-path projection is deferred and single-flight — with the rejected alternatives (DB-side aggregation, precomputed-table-only reads, the `includeContent` switch) and the sanctioned carve-outs. |

## Closeout mapping

| ID | Destination | Documentation impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1, D3, D7 | [runbook §11, slow dashboard responses on a large store](../dash/runbook.md#11-slow-dashboard-responses-on-a-large-store) — the symptom, the cause, the read contract that replaced it, deferred sessions in projection health, `stored-fields` heavy by design, and why `dash clean` is the owner's decision rather than a documented remedy | extends-component | KyberDash | 2026-11-22 | harvested |
| H2, D1, D5 | [architecture, read-path invariants and the read-model register](../dash/architecture.md#read-path-invariants-and-the-read-model-register) — the shaped-read contract, the retired broad members, and the boundary test that keeps them out | extends-component | KyberDash | 2026-11-22 | harvested |
| D2, D10 | [architecture, the maintenance-read boundary](../dash/architecture.md#the-maintenance-read-boundary) — the maintenance-named whole-store read, the per-key rebuild read, attributions for identities and budgets, and the unchanged schema 25 (no migration, no new table or index) | extends-component | KyberDash | 2026-11-22 | harvested |
| D4, D11 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) and [read-path deferral and single-flight](../dash/architecture.md#read-path-deferral-and-single-flight) — one read model per page at the page's level, and a concurrent pass that returns instead of stacking | extends-component | KyberDash | 2026-11-22 | harvested |
| D9 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — the `/api/kyber/sessions` row: `SessionListFigures(harnessId?)` joined to the stored projection rows, one batched content-free pass | extends-component | KyberDash | 2026-11-22 | harvested |
| H3, D13 | [SQLite standard, store-read rules](../standards/sqlite/sqlite.standards.md), [test standard, read-shape assertions](../standards/test/test.standards.md) and [review guidance](../../REVIEW.md) — the rules `KW-STD-SQL-001..003`, `KW-STD-TEST-001..002` and `R17`, each citing this register and the shipped code as the worked example. Written by T9, which follows this closeout in the same run | extends-component | KyberDash | 2026-11-22 | mapped (T9) |
| H4 | [architecture, read-path invariants and the read-model register](../dash/architecture.md#read-path-invariants-and-the-read-model-register) carries the contract and its sanctioned carve-outs: a consumer-shaped read is the rule, content text belongs to the view or the rebuild that consumes it, maintenance may scan the corpus and request paths may not, and read-path projection defers and is single-flight. ADR 0091, written by T10 in this run, records the same decision with its rejected alternatives (`includeContent` switch, DB-side aggregation, precomputed-table-only reads) and its `decided-by` provenance | extends-component | KyberDash | 2026-11-22 | harvested (ADR 0091 by T10) |
| D6 | Documentation waiver — the uncommitted `EXISTS`→`IN` predicate tweak is a query-plan note about code this change retires; the new per-session queries choose their own predicate, so no invariant survives for a reader to consult | none | KyberDash | 2026-11-22 | waived |
| D8 | Documentation waiver — the autonomy and development-mode provenance of this run, with no behavioural or architectural invariant behind it | none | KyberDash | 2026-11-22 | waived |
| D12 | Documentation waiver — how the interface migration was sliced for reviewability; the slices are done and no rule about sequencing survives them | none | KyberDash | 2026-11-22 | waived |
| AP1 | Documentation waiver — the approve-and-execute approval record, which lives in this plan's Decisions table rather than in canonical documentation | none | KyberDash | 2026-11-22 | waived |

## Investigation findings

Established by the investigator and accepted as given; the code sites the design needs were
re-verified in the run worktree's own CodeGraph index (worktree
`/Users/dave/git/personal/warp/kyber-weave.worktrees/20261122T225413Z-factory-fix-dash-api-stalls-on-large-store`,
`b0b0be34`, branch `factory/fix/dash-api-stalls-on-large-store`):

- **Funnel**: `DashEndpointMappings` → `DashboardQueryService` → `KyberBridgeEngine.GetSessions:176`
  / `GetCoverage:934` (and the other ~20 read methods) → `EnsureProjected:146` →
  `CanonSessionProjectorEngine.ProjectIncrementalRelational:2401` → `SqliteRelationalStore.QueryRecords:1142`
  via `BaseSqliteRepository.ExecuteSqliteCore` (single `StringBuilder` capture, `Pooling=false`,
  per-call connection).
- **`ProjectIncrementalRelational` builds `collectedByKey` from an unfiltered `reader.Records()`
  before it knows which sessions are dirty** — the whole store's content is materialized to
  decide skips. `PriorIdsOf` and `SessionIdentities` only consume `(session key, harness)`
  pairs; `BuildStoredSessionProjection` consumes full records but only for rebuilt keys.
- **`GetSessions` discards the `turns` half of `ReadSessionFigures`** — `SessionTurnSummaries`
  per row is pure waste in the list path; the aggregate half is needed only for figures.
- **`ListSessionAggregates` already exists** (`SqliteRelationalStore.Reader.cs:622`) and is used
  by the analytics path (`ExecuteAnalyticsCore`); internally it also calls `Records()` once
  (content-full today).
- **`BuildReaderTurnSummary` computes figures from token counts, reported cost, model, and
  timestamps only** — no part text — supporting D1's content-free aggregate path. The parity
  tests are the proof, not this reading.
- **The background scheduler is not the freshness mechanism under capture.**
  `JobSchedulerService.TimerLoopAsync` runs a maintenance pass every `refreshCadenceMinutes`
  (minutes), so between ticks the GET-path `EnsureProjected` is what keeps projections fresh —
  it cannot simply be deleted; it must be bounded (D3, D4).
- **The maintenance pass calls `ingestion.PurgeExpiredContent` then `projection.ProjectAll()`**
  (`JobSchedulerService.RunMaintenancePass:305`), and `CanonProjectionService.ProjectIncremental`
  short-circuits when current — deferral (D3) therefore lands in a read-path-only entry that
  the service never calls.
- **Retention tables examined**: `content_purge_mark` / `content_retention_days` are written by
  the stub `PurgeExpiredContent` (`TranscriptIngestionService.cs:102` returns
  `Purged: 0` unconditionally); no purge SQL exists against the relational store. See D7.
- **Existing coverage**: unit tests run over small in-memory fakes
  (`InMemoryRelationalStore` implements `IRelationalStoreReader`, `IProjectionStore`, and
  `IOperationalStore` in one class); nothing exercises read latency or a large store. Parity
  harnesses to extend: `SqliteRelationalStoreReadTests.AssertQueryParity`,
  `ApplicationParityTests`, `AnalyticsReadPathTests`, `CanonAnalysisTests` (Domain.Tests).
- **Standards consulted**: `docs_get_coding_standard_for_technology` is not available in this
  harness (not in the tool catalog), so the standards were read directly, targeted:
  `docs/standards/sqlite/sqlite.standards.md` (schema-change rule, column semantics, `TextExpr`
  binding, `hex(CAST(... AS BLOB))` read convention), `docs/standards/test/test.standards.md`
  (which project a test belongs in, assertion-style test names, zero-flakiness isolation),
  `docs/standards/csharp/csharp.standards.md` (section map; naming and layout).
- **Divergence found (documented rule vs existing code)**: none on this path — and that absence
  **is** the finding. No written standard, test rule, or reviewer question covers store-read
  shaping, content-column scope, or change-set-proportional passes, which is why this defect
  class was invisible to the author, the tests, and the reviewers that shipped it. The human's
  instruction ("also we need to update our standards so slop like this does not happen again")
  closes the gap in scope: T9 writes the rules, T10 the ADR (D13). Deduplication found while
  checking the homes: `REVIEW.md` class 4 (Unbounded work and unbounded growth) already forbids
  the unbounded-materialised-query, per-row-query-loop, and per-request-rebuild variants, and
  the test standard already bans sleeps and fixed waits — the new rules add only what those do
  not carry (read shaping, content-column scope, single-flight, read-shape assertions, parity
  on narrowing) and cite the existing bullets rather than restating them.

Facts established for the 2026-11-22 scope change (full interface restructure), all from the
worktree:

- **The endpoint surface** (`DashEndpointMappings.cs:49-106`): `/api/kyber/sessions` (with a
  `/session` alias), `session/{id}`, `session/{id}/summary`, `session/{id}/content`,
  `session/{id}/turn/{index}`, `session/{id}/turn/{index}/content`, `span/{id}/attributes`,
  `session/{sessionKey}/stored-fields`, `compare`, `compare/runs`, `quarantine`,
  `quarantine/{spanId}`, `problems`, `problem/{id}`, `meta`, `version`, `coverage`, `report`,
  `findings`, `finding/{findingId}`, `predictions` (GET/POST), `calibration`, `harnesses`,
  `harness/{id}`, `runs`, `run/{id}`, `timeline`, `review` (GET/POST), `model-catalog`,
  `capture`, `settings`, `jobs`, `refresh`/`import-history`/`clean` (POST), and
  `analytics/status`, `analytics/catalog`, `analytics/{op}`. Every read routes
  `DashEndpointMappings` → `DashboardQueryService` → `KyberBridgeEngine`, which holds the
  reader.
- **The interface holds 21 members** (full list in Scale); the reader call sites in
  `KyberDash.Application` concentrate in `KyberBridgeEngine` (28: `Sessions`×7, `GetSession`×5,
  `Records`×4, `SessionTurnSummaries`×3, `GetSessionAggregate`×2, `CountRecords`×2, and one
  each of `StoredFields`, `Sources`, `ListSessionAggregates`, `Harnesses`, `GetRecord`), plus
  `CanonSessionProjectorEngine` (the unfiltered `Records`), `CleanService` (maintenance:
  `Sources`/`Sessions`/`CountRecords`), `DoctorService` (maintenance:
  `Sessions`/`Harnesses`/`CountRecords`), and the analytics/report services.
- **Implementers that move with every interface change**: `SqliteRelationalStore`
  (`SqliteRelationalStore.Reader.cs:13`), `InMemoryRelationalStore`, and two test-local
  `FaultingSessionReader` copies (`AnalyticsReadPathTests.cs:663`,
  `AnalyticsOperationsDetailTests.cs:329`).
- **`GetSessionDetail` renders turn figures, not content** (`KyberBridgeEngine.cs:256-298`):
  it consumes the aggregate plus `ReadTimeTurnSummary` figures — spans, token classes,
  durations, dual costs — so the detail read model is content-free. `GetSessionSummary`
  discards the turns half of `ReadSessionFigures` entirely. `GetSpanAttributes` reads one
  record via `GetRecord(spanId)` and hydrates it (`CanonRecordHydration.Hydrate` +
  `AttachCapturedPrompt`), so its content exposure is at most that one span's prompt part.
  `GetTimeline` hydrates records for timeline nodes — metadata only.
- **The `ReadTime*` DTO family already shapes the views** (`IRelationalStoreReader.cs:120-338`):
  the read models formalize what these DTOs already carry; no content-text DTO is on a
  non-content path today except through the broad `Records`/`ContentParts` calls the
  restructure removes.

Discovery: codegraph 11 (worktree index via `codegraph explore`; 3 further explores for the
2026-11-22 scope change: the remaining bridge method bodies, the full interface tail, and the
scheduler/projection-service cadence), docs 0 (kw-docs MCP tools not available in this
harness), direct reads 7 (`docs/plans/README.md`, `dash/AGENTS.md`, `docs/dash/runbook.md` §4,
`docs/catalog.md` grep, standards section maps, ADR listing) plus repository greps for the
endpoint routes, interface member inventory, implementers, and per-file reader call-site
counts — the kw-docs MCP server is not listed in this harness's tool catalog, so documentation
questions were answered by reading the named files directly.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/server.tests/KyberDash.Application.Tests/Projection/` (new focused class), extending `ApplicationParityTests` and `dash/server.tests/KyberDash.Domain.Tests/CanonAnalysisTests.cs`; adapter parity in `dash/server.tests/KyberDash.Data.Tests/Repositories/SqliteRelationalStoreReadTests.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~ApplicationParityTests"` (plus the new class's own filter in the same form) | `AnIncrementalPassReadsOnlyTheDirtySessionsRecords`: one dirty session of two — the pass materializes only that session's records (instrumented fake read log), never issues a whole-store read, skips the clean session, and its projection is identical to a full pass's. `AttributionDrivenClaimingAssignsTheSameSessionIdsAsRecordIteration`. `AReadPathPassDefersOversizedDirtySessionsAndKeepsThemDirty` (budget 2): the small dirty session rebuilds, the oversized one is listed in `DeferredKeys`, its stored projection is unchanged, its key stays dirty, and a full pass rebuilds it. `ASecondEnsureProjectedReturnsWhileTheFirstPassHoldsTheReader` (gate-based, deterministic). `DeferredSessionsSurfaceInProjectionHealth`. `SessionAttributionsMatchTheInMemoryStore` over the SQLite adapter (keys, harnesses, counts, ordering; empty store returns empty). | The instrumentation assertions fail on current code: the pass calls unfiltered `Records()` (both sessions' content counted); the concurrent caller blocks on the gate; `DeferredKeys` is absent from `ProjectionPassReport`; the new members do not compile (the task's own symbols are missing). | All named tests pass deterministically (gates released in `finally`; no elapsed-time assertions); `ApplicationParityTests` and `CanonAnalysisTests` stay green; the attributions parity passes over adapter and fake. |
| T2 | `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsReadPathTests.cs` (extend); adapter/fake parity in `SqliteRelationalStoreReadTests.cs` / `SqliteRelationalStoreListAggregatesTests.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsReadPathTests"` | `SessionListFiguresAnswerTheSameFiguresAsTheBroadAggregateRead`: the list read model equals the old `GetSessionAggregate`/`ListSessionAggregates` output on seeded stores with a token-less session, a multi-harness session, an empty session, and a content-bearing session (absent stays distinct from zero for token totals). `TheSessionsListNeverIssuesAContentRead`: during `GetSessions` the fake's read log records zero content-bearing reads and the harness filter set/unset, empty store, and limit cases return the same JSON as before. | The content-read assertion fails on current code: `GetSessions` calls `GetSessionAggregate` and `SessionTurnSummaries` per row, both of which materialize content. The read-model members do not compile. | Both tests pass; `GetSessionsAnswersTypedFiguresAndNeverBlendsReportedAndPublishedCosts` keeps its assertions intact; the run reports a non-zero test count. |
| T3 | `AnalyticsReadPathTests.cs`, `RelationalIngestTests.cs`, `ApplicationParityTests.cs` (extend); parity rows in `SqliteRelationalStoreReadTests.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsReadPathTests"` | `SessionDetailAnswersTurnFiguresWithoutContentReads` and `SessionSummaryAnswersWithoutTurnOrContentReads`: detail/summary read models equal the old `ReadSessionFigures` output (including `ReadTimeTurnSummary` fields), and the read log records zero content reads — the acceptance case is a 10,624-turn-shaped seeded session (many turns, large parts) whose detail and summary paths never touch content. `TurnFiguresAnswerOneTurnScopedToItsIndex`. | The content-read assertions fail on current code (detail/summary call the content-materializing aggregate and turn summaries); the read-model members do not compile. | All named tests pass; detail and summary payloads are byte-for-byte the pre-change shapes (existing endpoint tests stay green, locators narrowed only where the new work made them ambiguous, each edit named). |
| T4 | `AnalyticsReadPathTests.cs`, `ApplicationParityTests.cs` (extend) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsReadPathTests"` | `TurnContentReadsOnlyTheRequestedTurnsParts`: the scoped content read returns exactly the turn/span/part requested, honouring the budget and `full` flag as today; `EveryOtherPathLeavesContentUnread` — the full endpoint sweep (list, detail, summary, turn figures, span attributes, timeline, coverage, findings, problems, quarantine, harnesses, runs, report, analytics) records zero content reads on the instrumented fake. | The sweep fails on current code: `ContentParts`/broad reads run on paths the steer names as non-content. The scoped member does not compile. | Both tests pass; content payloads are unchanged where they exist today; the sweep's zero-content assertion holds for every non-content endpoint. |
| T5 | `AnalyticsReadPathTests.cs`, `AnalyticsOperationsDetailTests.cs`, `AnalyticsErrorEnvelopeTests.cs` (extend); `StoreInterfaceShapeTests` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsReadPathTests"` | `SpanAttributesAnswerOneSpansMetadataWithoutSessionReads` (single-span read model; at most that span's designated prompt part if the SPA renders its text, and the test pins whichever way that resolved); `TimelineAnswersFromContentFreeRecords`; `StoredFieldsStaysScopedToTheNamedSession` (kept, not regressed). Harnesses/runs/compare/findings/problems/quarantine verified against the operational and projection stores with no relational-reader calls added. | The span-attributes and timeline assertions fail on current code (they hydrate through broad record reads); the members do not compile. | All named tests pass; the endpoint payloads are unchanged; the read log shows only purpose-named, scoped reads. |
| T6 | `AnalyticsServiceOperationsTests.cs`, `AnalyticsErrorEnvelopeTests.cs`, `AnalyticsOperationsDetailTests.cs`, `ApplicationParityTests.cs` (extend) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsServiceOperationsTests"` | Every analytics operation and the report answer from the purpose-named read models with output identical to today (the operations suite already pins the shapes); the endpoint sweep `AnalyticsReadPathTests.EveryOtherPathLeavesContentUnread` reports zero content reads with the analytics and report paths in it. | The migration's instrumentation assertion fails while analytics still reads through `ListSessionAggregates`/broad reads. | The operations and parity suites pass unchanged in their assertions; the sweep holds; the run reports a non-zero test count. |
| T7 | `StoreInterfaceShapeTests.cs` (`KyberDash.Domain.Tests`), the architecture-boundary tests in `dash/server.tests`, `InMemoryRelationalStoreTests.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~StoreInterfaceShapeTests"` | `TheReaderInterfaceCarriesNoBroadReads`: `Records`, `GetSessionAggregate`, `ListSessionAggregates`, `SessionTurnSummaries`, `ContentParts`, `GetRecord`, and every other member with no remaining purpose are gone from the interface and all four implementers. `RequestPathsCannotReachMaintenanceReads`: the boundary test asserts the maintenance-named whole-store read is referenced only by the projector's full pass, the CLI build path, and doctor — not by `KyberBridgeEngine`, `DashboardQueryService`, `AnalyticsService`, or `ReportService`. | The members still exist and compile on current code; the boundary test has nothing to assert. | Both tests pass; the solution builds with no implementer left carrying a retired member; the full pre-existing unit suites for the touched projects stay green. |
| T8 | Documentation; verified by the docs gates, no test class | `kyber-weave docs validate .` and `kyber-weave docs drift .` in the run worktree | The runbook troubleshooting entry and the architecture read-model register exist and match the shipped behaviour, written against an oversized store (no cleanup assumed). | Not applicable (documentation task; read-only verification named here explicitly). | Both docs checks report no finding for the plan or the edited documents. |
| T9 | Standards and review guidance; verified by the docs gates, no test class — the rules' own enforcement is the test-shape rule (`KW-STD-TEST-001`), which this change's slices already apply, so no new test infrastructure is proposed (ADR 0089) | `kyber-weave docs validate .` and `kyber-weave docs drift .` in the run worktree | Five rules a reviewer can apply to a diff, each one checkable judgment with its permanent id, in the homes and form the standards already use; every rule cites the shipped worked example (read-model register, purpose-named reads, boundary test) and carves out the maintenance read, `StoredFields`, and the projection rebuild, so none outlaws the design. | Not applicable (standards task; the rules land before the end-of-run council, which is the reviewer the human's instruction targets). | Both docs checks pass with the new sections carrying `KW-STD-SQL-001..003`, `KW-STD-TEST-001..002`, and `R17`, and no rule restating an existing one (the deduplicated behaviours are cited, not repeated). |
| T10 | ADR; verified by the docs gates, no test class | `kyber-weave docs validate .` and `kyber-weave docs drift .` in the run worktree | ADR 0091 records the read-shape contract: consumer-shaped reads, scoped content columns, maintenance-may-scan/request-paths-may-not, deferred and single-flight read-path projection, the rejected alternatives, and the carve-outs; registered in `docs/adr/README.md`. | Not applicable (ADR task; the decision is already made in D1/D3/D4/D5/D11 and the ADR is its durable record). | Both docs checks pass with ADR 0091 registered and every symbol and doc it cites resolving. |

## Tasks

### T1: Projection pass — purpose-named reads, deferral, single-flight

- **Objective:** Remove the whole-store read from the projection pass: identities and skips from attributions, full records only per rebuilt key, a maintenance-named whole-store read for the wildcard/full path, read-path deferral of oversized dirty sessions, and a single-flight guard.
- **Capabilities:** C# application-layer engine work (projection, concurrency) and data-layer query work over the KyberDash SQLite store, with in-memory unit tests over the shared fake.
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `canon-projection`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add `SessionAttributions()` (with the `StoredSessionAttribution(SessionKey, HarnessId, RecordCount)` record, ordered by session key then harness), `SessionRecordsForRebuild(string sessionKey)`, and the maintenance-named whole-store record read (for example `AllSessionRecordsForMaintenance()`), whose XML doc states it is reserved for `kyber build`, wildcard projection rebuild, and doctor, and must not be called from a request path. Additions only — nothing is removed yet (D12).
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: implement the three members — attributions as one grouped query over `record` joined to `record_session_link` (bind with `TextExpr` where caller text appears; keep the `hex(CAST(... AS BLOB))` read convention; no new index, no schema change); the per-key and maintenance reads through the existing `QueryRecords` filtering, renamed/wrapped to the new shapes.
  - `dash/server/KyberDash.Application/CanonSessionProjectorEngine.cs`: `ProjectIncrementalRelational` — replace the unfiltered `reader.Records()` grouping with attribution-driven identities and skip decisions; read full records only per rebuilt key via `SessionRecordsForRebuild`; the wildcard/full path reads `AllSessionRecordsForMaintenance()`. Add the read-path entry (for example `ProjectIncrementalForReads()`) applying the per-session record-count budget (default 2,000, injectable through the constructor for tests), returning deferred keys, and writing back a dirty state that still lists them; add the single-flight guard (in-flight flag checked by every pass entry, cleared in `finally`).
  - `dash/server/KyberDash.Domain/StoreAndServiceDtos.cs`: `ProjectionPassReport` — add `IReadOnlyList<string>? DeferredKeys = null` as an optional trailing parameter.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: `EnsureProjected` — call the read-path entry; `BuildProjectionHealth` — surface deferred keys so `GetCoverage` reports projection lag.
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`: implement the three new members, and add the instrumented read log (which read methods ran, and content bytes materialized) that this and every later slice's tests assert on — extend the fake's existing style, no new fixture project.
  - `dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsReadPathTests.cs:663` and `AnalyticsOperationsDetailTests.cs:329`: update both `FaultingSessionReader` copies for the new members.
  - Tests per the Test contract (new focused class under `dash/server.tests/KyberDash.Application.Tests/Projection/`, extends to `ApplicationParityTests` and `dash/server.tests/KyberDash.Domain.Tests/CanonAnalysisTests.cs`, adapter parity in `SqliteRelationalStoreReadTests.cs`).
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T1 --red`
  - `kyber-weave review gates . --agent <worker> --task T1`
- **Acceptance:**
  - All Test-contract rows pass deterministically (gate-based concurrency test; no elapsed-time assertions).
  - A full pass still rebuilds every dirty session; the maintenance pass and CLI `kyber build` behaviour is unchanged apart from the shared single-flight guard.
  - The deferred write-back keeps the deferred key dirty and preserves today's marker semantics for spans ingested during a pass.

Failure modes this task must not allow (advanced-task design, decided here):

- **Lost dirty sessions** — a deferred key never re-projected. Mitigations: the write-back test pins that the deferred key survives the pass in the dirty state; the full pass never defers; the maintenance pass runs on the operator cadence.
- **Session-id churn** — attribution-driven claiming assigning different ids than record iteration, pruning and rebuilding every stored projection. Mitigation: the ordering decision (D10) plus `AttributionDrivenClaimingAssignsTheSameSessionIdsAsRecordIteration`.
- **Stuck single-flight** — a faulted pass leaving the in-flight flag set. Mitigation: the flag clears in `finally`; the concurrency test releases its gate in `finally` and asserts a later pass proceeds.

### T2: Sessions list — the `SessionListFigures` read model

- **Objective:** Give the sessions list its own purpose-named, content-free read model and migrate `GetSessions` off the per-session aggregate/turn reads.
- **Capabilities:** C# contract, data-layer, and application read-path work with parity and instrumentation tests.
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T1 (the instrumented fake and attribution read land there; same adapter and fake files)
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add the `SessionListFigures` read model — its result type carries, per session key, the list's fields (harness id, workspace repo/branch, turn count, request count, token-total availability with absent distinct from zero, published and reported cost with basis mismatch, model names, problems count). No content.
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: implement it as one batched, content-free pass (no `content_part_text`, no tool-call text columns), following the existing `hex` read conventions and `TextExpr` binding.
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`: implement the model from state, content-free, so the parity and instrumentation tests run over the fake.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: `GetSessions` — answer from `SessionListFigures(normalizedHarness)` joined with the existing `ListSessionProjections` rows and per-row `GetSession` (session metadata only); stop calling `ReadSessionFigures` in the list path. `ReadSessionFigures` stays for T3's pages until that slice migrates it.
  - Both `FaultingSessionReader` copies; tests per the Test contract (`AnalyticsReadPathTests.cs` extends, parity in `SqliteRelationalStoreReadTests.cs` / `SqliteRelationalStoreListAggregatesTests.cs` seeded with the token-less, multi-harness, empty, and content-bearing sessions).
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T2 --red`
  - `kyber-weave review gates . --agent <worker> --task T2`
- **Acceptance:**
  - `SessionListFiguresAnswerTheSameFiguresAsTheBroadAggregateRead` and `TheSessionsListNeverIssuesAContentRead` pass; the existing figures test keeps its assertions; the run reports a non-zero test count.
  - No `RelationalSchemaCatalog` edit and no `SchemaVersion` bump.

Failure mode this task must not allow: a figure silently diverging from the broad read (the token-availability absent-versus-zero trap the SQLite standard names). Mitigation: the parity row seeds exactly that case and must not be weakened; the read model derives from the same columns the broad path read.

### T3: Session detail, summary, and turn figures — page-shaped, content-free

- **Objective:** Give the session view's three figure pages their own scoped read models so selecting the 10,624-turn session never reads its 485 MB of text.
- **Capabilities:** C# contract, data-layer, and application read-path work with parity and instrumentation tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T2 (the pattern, the instrumented fake, and the same files)
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add `SessionDetailFigures(sessionKey)` (the detail header's figure set including per-turn `ReadTimeTurnSummary` figures), `SessionSummaryFigures(sessionKey)` (the bounded summary block), and `TurnFigures(sessionKey, turnIndex)`. All content-free; scoping keys as the read-model table states.
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: implement the three from the existing aggregate/turn builder logic, reading content-free record metadata (the builders already compute figures from token counts, reported costs, models, and timestamps — `BuildReaderTurnSummary` touches no part text).
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs` and both `FaultingSessionReader` copies: implement the members.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: migrate `GetSessionDetail`, `GetSessionSummary`, `GetTurnFigures`, and `GetTurnContent`'s figures half off `ReadSessionFigures`/`SessionTurnSummaries` onto the new models; retire `ReadSessionFigures` when its last caller is gone (compiler-verified).
  - Tests per the Test contract, including the many-turn seeded session whose detail and summary paths never touch content.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T3 --red`
  - `kyber-weave review gates . --agent <worker> --task T3`
- **Acceptance:**
  - All Test-contract rows pass; detail and summary payloads are byte-for-byte the pre-change shapes; projection-failure isolation still answers `PROJECTION_FAILED` for a broken session only.

### T4: Content view — the single scoped content read

- **Objective:** Make the content view the only request path that reads content text, scoped to one turn's or one request's parts.
- **Capabilities:** C# contract, data-layer, and application read-path work with parity and instrumentation tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T3 (the turn figures it joins against)
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add `TurnContentParts(sessionKey, turnIndex/spanId, part, budget)` — the read-model table's only content-text member on a request path.
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: implement it scoped by `(session_key, turn/span, position)`, budget and `full` semantics exactly as the current endpoints apply them; no other new read may touch `content_part_text`.
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs` and both `FaultingSessionReader` copies: implement the member (the fake's read log counts its content bytes).
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: migrate `GetTurnContent` and `GetSessionContent` onto it; the endpoint sweep test lands here.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T4 --red`
  - `kyber-weave review gates . --agent <worker> --task T4`
- **Acceptance:**
  - `TurnContentReadsOnlyTheRequestedTurnsParts` and the endpoint sweep `EveryOtherPathLeavesContentUnread` pass; content payloads are unchanged where they exist today.

### T5: Remaining session-adjacent endpoints — span attributes, timeline, stored-fields, harnesses

- **Objective:** Finish the session-adjacent pages with purpose-named reads; verify the projection-store and operational-store pages need no reader.
- **Capabilities:** C# contract, data-layer, and application read-path work with parity and instrumentation tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T4 (same files; the sweep grows here)
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add `SpanAttributes(spanId)` (single-span metadata; if the SPA renders prompt text on that panel, at most that span's designated prompt part — the test pins whichever way this resolves) and `SessionTimeline(sessionKey?)` (content-free timeline nodes). `StoredFields` and `Harnesses` stay as they are (already purpose-named and scoped).
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`, `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`, both `FaultingSessionReader` copies: implement the two members.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: migrate `GetSpanAttributes` (off `GetRecord` + whole hydration) and `GetTimeline` (off broad record reads) onto the new models; verify runs/compare/findings/problems/quarantine read only the projection and operational stores and add no reader calls.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T5 --red`
  - `kyber-weave review gates . --agent <worker> --task T5`
- **Acceptance:**
  - All named tests pass; endpoint payloads are unchanged; the read log shows only purpose-named, scoped reads for this page group.

### T6: Analytics and report — the last figure consumers migrate

- **Objective:** Migrate the analytics operations and `kyberdash report` onto the purpose-named figure read models, ending request-path use of `ListSessionAggregates` and the broad child queries.
- **Capabilities:** C# application and analytics read-path work with parity tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T5 (the read models it consumes exist; same adapter and fake files)
- **Files:**
  - `dash/server/KyberDash.Application/Services/ReportService.cs` and the analytics service in `dash/server/KyberDash.Application` (compiler-located; its operations suite is `AnalyticsServiceOperationsTests`): replace `ListSessionAggregates`/broad reads with `SessionListFigures`-shaped and scoped read models; output identical.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: `GetReport` and the analytics funnel migrate off any remaining broad member.
  - Tests per the Test contract (`AnalyticsServiceOperationsTests.cs`, `AnalyticsErrorEnvelopeTests.cs`, `AnalyticsOperationsDetailTests.cs`, `ApplicationParityTests.cs`).
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T6 --red`
  - `kyber-weave review gates . --agent <worker> --task T6`
- **Acceptance:**
  - The operations and parity suites pass unchanged in their assertions; the sweep `EveryOtherPathLeavesContentUnread` holds; the run reports a non-zero test count.

### T7: Retire the broad reads and fence the maintenance read

- **Objective:** Remove every broad member with no remaining purpose from the interface and all implementers, and pin the maintenance-read boundary with a shape/boundary test.
- **Capabilities:** C# contract cleanup and architecture-boundary tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-read-models`
- **Depends on:** T6 (the last request-path consumers have migrated)
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: remove `Records`, `GetSessionAggregate`, `ListSessionAggregates`, `SessionTurnSummaries`, `ContentParts`, `GetRecord`, and every other member the compiler proves caller-less (the child-table queries with no named maintenance consumer go too; `Sessions`, `Sources`, `CountRecords`, `Harnesses`, `StoredFields`, `SessionModelUsages`, and the purpose-named additions stay).
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: delete the retired implementations and the now-unreachable `QueryRecords` shapes they fed (the human's `EXISTS`→`IN` tweak targets this code — see D6).
  - `dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`, both `FaultingSessionReader` copies, `dash/server.tests/KyberDash.Domain.Tests/Contracts/StoreInterfaceShapeTests.cs`, and the architecture-boundary tests in `dash/server.tests`: remove retired members; add `TheReaderInterfaceCarriesNoBroadReads` and `RequestPathsCannotReachMaintenanceReads` per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T7 --red`
  - `kyber-weave review gates . --agent <worker> --task T7`
- **Acceptance:**
  - The solution builds with no implementer carrying a retired member; both boundary/shape tests pass; the pre-existing suites for the touched projects stay green.

### T8: docs-dev closeout — runbook troubleshooting and the read-model register

- **Objective:** Land the runbook and architecture harvest rows: the troubleshooting entry and the read-model register with the read-path invariants.
- **Capabilities:** documentation
- **Difficulty:** easy
- **Session:** `dash-docs-closeout`
- **Depends on:** T7 (harvest describes the shipped behaviour)
- **Files:**
  - `docs/dash/runbook.md`: new "Common troubleshooting" entry for slow API responses on a large store, written against an oversized store (no cleanup assumed): symptom, cause, the purpose-named read contract, the deferred-session behaviour and projection health, and the honest note that stored-fields on an oversized session stays heavy by design.
  - `docs/dash/architecture.md`: the read-model register and read-path invariants per the Design harvest row — this register is also the worked example T9's standards rules cite, so it must name each read model, its scoping key, its content policy, and the maintenance-read boundary.
- **Commands:**
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - Both docs checks pass in the run worktree with no finding for the edited documents; the closeout mapping names each harvest destination.

### T9: Standards — five store-read rules the next author and reviewer can apply

- **Objective:** Close the standards gap the human named ("update our standards so slop like this does not happen again"): write five checkable rules, with new permanent ids, into the homes and form the standards already use, so the next author writes shaped reads, the next test asserts them, and the next reviewer catches the class in a diff.
- **Capabilities:** standards and review-guidance authoring (documentation)
- **Difficulty:** medium
- **Session:** `store-read-standards`
- **Depends on:** T7 and T8 (the rules cite the shipped code and the read-model register as the worked example, so every citation resolves when the docs gates run)
- **Files:**
  - `docs/standards/sqlite/sqlite.standards.md`: new section under Coding standards — "Store reads are shaped to their consumer" — with three rules in the standard's prose form (id in parentheses after the lead sentence, as `KW-STD-INPUT-001` does):
    - `KW-STD-SQL-001`: a store read is shaped to its consumer — it returns the columns, rows, and scope its one calling page or pass consumes; a read whose result the caller filters, subsets, or half-discards in C# is a defect, and the query carries the shape. Whole-corpus scans are maintenance work and live behind a maintenance-named method (`AllSessionRecordsForMaintenance` in `SqliteRelationalStore.Reader.cs` is the worked example); no request path may call them. The per-session `StoredFields` inventory is the sanctioned exception — the raw rows are the view.
    - `KW-STD-SQL-002`: content text columns (`content_part_text`, tool-call input/result text) are selected only by the path that consumes them — the content view (scoped to the requested turn/span), the stored-fields inventory (scoped to the named session), and the projection rebuild (scoped to the session being rebuilt, which derives persisted figures from text). Any other path selecting them is a defect.
    - `KW-STD-SQL-003`: a read's cost is proportional to its input, not the corpus — an incremental pass reads only what changed (per-dirty-key reads and attribution pairs for skip decisions, never the store to decide skips); a list endpoint's store-query count is bounded regardless of row count (one batched figures read, not per-row reads); an expensive shared refresh on the request path is single-flight or moved off the path.
  - `docs/standards/test/test.standards.md`: new section under Coding standards — "Read-path tests assert the shape of the reads" — with two rules:
    - `KW-STD-TEST-001`: a read path's test asserts which store reads the path issues, over the fake's instrumented read log, not only the values returned. This is the mechanical lever for the store-read rules: it fails without a reviewer remembering anything, and it proposes no guard script and no new architecture test (ADR 0089 keeps prose enforcement in written guidance plus review; this rule is that guidance's testable half). It reconciles with "Test the decision, not the implementation" by naming the read log the seam's observable contract — the reads a path issues are its cost behaviour. The worked examples are this change's endpoint sweep and the `RequestPathsCannotReachMaintenanceReads` boundary test.
    - `KW-STD-TEST-002`: narrowing or replacing a read requires scalar/aggregate parity against the read it replaces, on seeded shapes that include the absent-versus-zero cases (a token-less session), because a smaller read returning different figures is a silent defect. A fixture orders of magnitude smaller than production cannot prove a read path's latency, so the proof is the shape assertion of `KW-STD-TEST-001` — extending the existing no-sleeps/no-fixed-waits rule, not restating it — and the suite's 120-second budget is untouched because these are ordinary in-memory assertions with no waits.
  - `REVIEW.md`: extend class 4 (Unbounded work and unbounded growth) with `R17`, covering only the angles the class does not already carry: before flagging, the reviewer answers two questions by reading the diff — does each endpoint's store read return only what its page renders (no C# filtering of a superset, no content columns outside the content view, the stored-fields inventory, or the projection rebuild)? and is any expensive shared refresh on the request path single-flight or deferred to maintenance? The class's existing bullets (unbounded materialised queries, per-row query loops, per-request rebuilds of growing structures) already catch the other variants and are cited, not repeated.
- **Commands:**
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - Every rule is one checkable judgment with its permanent id; a reviewer could not check it and no test could express it → it is cut.
  - Every rule is checked against the shipped read models (the register in `docs/dash/architecture.md` from T8): none outlaws the maintenance whole-store read, the heavy-by-design `StoredFields`, or the projection rebuild's content read — the carve-outs are written into the rules, and a rule the approved design violates is a defect in the rule, fixed there.
  - No guard script and no new architecture test is proposed anywhere in the rules.
  - Both docs gates pass with the new sections and no duplicate of an existing rule.

### T10: ADR 0091 — reads are shaped by their consumer

- **Objective:** Record the durable decision as a numbered ADR and register it: reads are shaped by their consumer, content text is read only by the view or rebuild that consumes it, maintenance may scan the corpus while request paths may not, and read-path projection is deferred and single-flight.
- **Capabilities:** architecture decision record authoring (documentation)
- **Difficulty:** easy
- **Session:** `store-read-standards`
- **Depends on:** T9 (the ADR cites the rule ids)
- **Files:**
  - `docs/adr/0091-store-reads-are-shaped-by-their-consumer.md`: new ADR in the repository's ADR form — context (the dashboard stall on a 579 MB store with the measured funnel), the decision, the rejected alternatives with reasons (DB-side aggregation — large surface, parity-hostile; precomputed-table-only reads — contradicts the runbook's read-time dual-spend design; the `includeContent` switch — treats over-fetching as a caller's responsibility), the consequences (the read-model register, the maintenance-read boundary, the sanctioned carve-outs, deferred and single-flight passes), and pointers to the rules (`KW-STD-SQL-001..003`, `KW-STD-TEST-001..002`, `R17`) and the worked example.
  - `docs/adr/README.md`: index row for ADR 0091.
- **Commands:**
  - `kyber-weave docs validate .`
  - `kyber-weave docs drift .`
- **Acceptance:**
  - ADR 0091 is registered and every symbol and document it cites resolves; both docs gates pass.
  - The ADR states the carve-outs explicitly, so a future change that widens a request-path read contradicts a numbered decision, not a convention.

## Dependency graph

```
T1 (projection pass: purpose-named reads, deferral, single-flight)
      |
      v
T2 (sessions list read model)  ── chain in session group `dash-read-models`, one worker
      |                           session; every slice touches the contract, the adapter,
      v                           the fake, and the FaultingSessionReader copies
T3 (detail / summary / turn figures)
      |
      v
T4 (scoped content view)
      |
      v
T5 (span attributes, timeline, stored-fields, harnesses)
      |
      v
T6 (analytics and report migration)
      |
      v
T7 (retire the broad reads, fence the maintenance read)
      |
      +---------------+
      v                 v
T8 (runbook +            T9 (standards rules: KW-STD-SQL-001..003,
read-model register)         KW-STD-TEST-001..002, R17)
      |                 |
      +-------+---------+
              v
T10 (ADR 0091, registered)
```

T1 is the latency win (it removes the whole-store read from every GET); T2–T4 follow in
descending latency impact; the chain is serial because every slice edits
`IRelationalStoreReader.cs`, `SqliteRelationalStore.Reader.cs`, `InMemoryRelationalStore.cs`,
and the `FaultingSessionReader` copies. `max-concurrent-workers` (default 4) is not the
constraint here; the file overlap is. T9 follows T7 and T8 because its rules cite the shipped
worked example (the register, the purpose-named reads, the boundary test) and every citation
must resolve when `docs drift` runs; T10 cites the rule ids. T9 and T10 land inside the run,
before the end-of-run council — the reviewer of the implementation slices has the rules in
hand, and no agent reviews a single task earlier than that council.

## Risks

| Risk | Mitigation |
|---|---|
| A figure silently diverges when a page moves from the broad read to its read model (the absent-versus-zero trap for token totals above all). | Every migration slice carries a parity row against the old broad read on the four named seed shapes (token-less, multi-harness, empty, content-bearing); assertions are never weakened; the broad reads stay in place until T7 so parity is always against live code. |
| A slice misses a caller, leaving a page on a broad read. | The compiler enumerates implementers; T7's retirement makes any survivor a compile error, and `RequestPathsCannotReachMaintenanceReads` plus the endpoint sweep (T4) pin the boundary mechanically. |
| Session ids churn when claiming moves to attribution pairs, pruning and rebuilding every projection. | Deterministic ordering (D10) pinned by `AttributionDrivenClaimingAssignsTheSameSessionIdsAsRecordIteration`. |
| Deferred sessions look stale in the UI during continuous capture. | By design (D3): projection health reports deferred keys, the maintenance cadence refreshes them, and the runbook entry explains the behaviour to operators. |
| A faulted pass leaves the single-flight flag set or the dirty state inconsistent. | Flag clears in `finally`; write-back and deferred-key tests pin the state contract (T1). |
| The 10,624-turn session's detail path still costs seconds after T3 through non-content volume (12k records' metadata per request). | The acceptance test pins that the detail path issues zero content reads for a many-turn session; if metadata volume alone still dominates, that is a measured follow-up named in the pull request notes, not silent. |
| Merge overlap with the human's uncommitted `EXISTS`→`IN` tweak in the main checkout. | D6: the tweak is likely moot (its target line is retired by T7); the pull request notes recommend dropping it, and the merge resolves by taking this branch's version either way. |
| Migration breadth: eight slices touching one contract, reviewed over a longer run. | Vertical slices (D12): each lands contract + adapter + fake + endpoints + tests for one page group and compiles on its own; the review council sees the whole change against base `b0b0be34`. |
| A new standard rule accidentally outlaws the approved design (the maintenance whole-store read, the heavy-by-design `StoredFields` inventory, or the projection rebuild's content read). | The carve-outs are written into each rule (T9 files), T9's acceptance requires checking every rule against the read-model table, and a rule the fix violates is a finding against this plan — the rule is fixed, not the design. |

## Out-of-scope boundaries

- The dashboard stays slow on the human's live 579 MB store until this change ships: the stopgap was declined (D7), so no interim relief is planned, and the pull request description states that plainly.
- PR note: `dash/server/KyberDash.Application/Services/TranscriptIngestionService.cs` — the relational `PurgeExpiredContent` records a mark and purges nothing, contradicting `docs/dash/runbook.md` §4 and ADR 0018 — a separate defect in a flow this change does not touch; left to a human.
- PR note: the human's uncommitted `EXISTS`→`IN` predicate tweak in the main checkout's `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs` — likely moot after T7 retires the code it optimizes (D6); they can decide to drop it.
- PR note: live-store latency re-verification — the fix is proven by the parity, instrumentation, and endpoint-sweep tests; re-probing the human's live store requires the fixed binary installed, which is the owner's post-merge check.
- PR note: stored-fields on an oversized session stays heavy by design (a raw inventory scoped to that session); if the owner wants a bounded inventory variant, that is a separate human decision.
- PR note: a total pass budget (bounding the sum of a first read-path pass over many small sessions) — pre-existing behaviour, deliberately not added here.
- Incremental-within-session projection (rebuilding only new turns of a huge session) is a deeper redesign this change does not need; D3's deferral bounds the read path without it.
- No canon schema change, no `SchemaVersion` bump, no new test project or fixture strategy (tests extend the existing fakes, parity classes, and gate runs; the instrumented read log extends `InMemoryRelationalStore` in its existing style).

## Verification gates

- Each developer task ends with its own `task-gate` run (the Commands above) over the unit
  projects; no dev agent runs the full suite. `.NET` commands run directly via `dotnet`.
- The end-of-run review council reviews the change with the `code-review` skill, measured
  against the base `b0b0be34a38aeade81616999532893e054af2a9b` and admitted to the full-suite
  gates with `--council`; the reviewer derives the command (the plan does not carry it).
- Documentation changes (T8) re-run `kyber-weave docs validate .` and `kyber-weave docs drift .`
  in the run worktree to zero findings.

## Review

The end-of-run council (`code-review` skill, base `b0b0be34`) reviews the whole change. The
plan's own review gate is the conductor's approve-and-execute presentation: scale is
`extends-component` over `KyberDash`, with the Design harvest table above naming the durable
documentation destinations.

## docs-dev closeout

- Execute the Design harvest rows (runbook troubleshooting entry, written against an oversized
  store; the architecture read-model register and read-path invariants; the standards rules and
  ADR 0091, delivered by T9 and T10).
- Run `kyber-weave docs standards migrate` for any standards warning period findings
  (`KW-DOC-SPEC-012`, `KW-DOC-SPEC-013`, `KW-CONFIG-REG-001`, `KW-CONFIG-REG-002`) and commit
  the migration with the closeout.
- Closeout mapping: the table above names every destination — read-model register and read-path
  invariants → `docs/dash/architecture.md`; troubleshooting entry → `docs/dash/runbook.md`;
  store-read rules (`KW-STD-SQL-001..003`, `KW-STD-TEST-001..002`, `R17`) →
  `docs/standards/sqlite/sqlite.standards.md`, `docs/standards/test/test.standards.md`,
  `REVIEW.md` (T9); read-shape contract and freshness decisions (D1, D3, D4, D5, D11) → the
  architecture section now, with ADR 0091 as the immutable decision record (T10 — mandatory, not
  waiverable). No RESIDUAL row: the retention purge stub would delete nothing on the captured
  store, so it stays a pull request note for a separate change rather than a todo this closeout
  creates.
