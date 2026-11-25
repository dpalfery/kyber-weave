---
id: adr/0092-dashboard-views-load-by-section-and-page
title: Dashboard views load by section and by page
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-25
component: KyberDash
decided-by: []
supersedes:
  - adr/0007-kyberdash-agent-session-analysis-integration
  - adr/0011-asad-only-context-view-and-payload-contract
  - adr/0041-kyberdash-retire-legacy-context-schema-routes
code-refs:
  - KyberBridgeEngine
  - DashEndpointMappings
  - SessionHeaderPage
  - SessionListFigures
  - SessionTurnFigures
  - SessionTimelineRecords
  - StoredFieldsInventory
  - StoredFieldsRows
  - RequestPromptPreviews
  - SpanPromptText
  - SessionReportFigures
  - useSectionPage
  - Pager
---

# ADR 0092: Dashboard views load by section and by page

## Status

Accepted, 2026-11-23; amended 2026-11-25. Records owner decisions D1, D2, D4, D10, D11, D19 and D20 of
the lazy sectioned session-views plan: the recommended per-section calls with paged lists, 100
rows on the sessions list, 25 rows per list section, parallel section loading in page order,
PR 666, opencode execution under Claude authorship, and the two approved parallel starts.
It builds on [ADR 0091](0091-store-reads-are-shaped-by-their-consumer.md): reads were already
shaped by their consumer; now views are split so each shape has its own endpoint.

**2026-11-25 amendment (Decision D5 of the session detail paging, sorting, and Claude composition plan):**
Session list sections and `/api/kyber/session/{id}/charts` `context.turns` accept server-side page sizes
`25`, `50`, or `100` (default `25`) and sort order `desc` (newest/highest first, default) or `asc`
(oldest/lowest first). `/charts` now pages and sorts `context.turns` on the server while keeping
whole-session `turns` and context summary aggregates unpaged.

This record supersedes, in part, the clauses that served the whole session through one payload:
[ADR 0007](0007-kyberdash-agent-session-analysis-integration.md) decision D3 (the
`GET /api/kyber/session/:id` entry in the formal REST contract), [ADR
0011](0011-asad-only-context-view-and-payload-contract.md) decision D3 (the ASAD payload served
whole by `GET /api/kyber/session/:id`), and [ADR
0041](0041-kyberdash-retire-legacy-context-schema-routes.md) decision D3 (the
`GET /api/kyber/session/:id` passthrough standing beside its summary sibling). Everything else
in those records stands.

## Context

The stall fix made every dashboard read content-free (ADR 0091) but did not change how much
each request asks for. Opening a session was still one monolithic call: the server built every
section — header, all turns, tools, servers, schema, context, reconciliation, the whole timeline
tree, requests, skills and notes — and nothing rendered until it all arrived. A 10,000-turn
session returned all its turns and the full span tree in one response. The sessions list read
every session's full projection twice and priced every session, then discarded all but the
limit. The owner reviewed the result and asked for a lazy-load design: the sessions page loads
only what the list shows, opening a session loads that view, each section has its own API call,
every list pages, and conversation text is fetched only when it is shown.

## Decision

1. **One endpoint and one TanStack query per section.** The session view is a layout of section
   components (header/overview, charts, tools, servers, skills, timeline, requests,
   reconciliation, problems, stored fields), each owning its query and its loading, error, empty
   and data states through the shared `useSectionPage` hook. A section waits only for its own
   data. The monolithic `GET /api/kyber/session/:id` route, `GetSessionDetail` and
   `SessionDetailFigures` are retired; the report prices its window with one keyed
   `SessionReportFigures` read and the run page reads `SessionTurnFigures` per execution.
2. **Every list pages on the server.** The sessions list pages at 100 roots (filtered and
   searched in SQL, priced only for the page) with the first 25 subagents inline per root;
   every session list section (`tools`, `servers`, `skills`, `kw-skills`, `requests`,
   `reconciliation`, `problems`, `timeline`, and `/charts` `context.turns`) pages at `25`, `50`,
   or `100` rows (default `25`, persisted per section in `sessionStorage`) and sorts `desc`
   (default) or `asc` on the server through the one shared `Pager`, reusing `DashHttp.Pagination`
   alongside strict `TrySectionLimit` / `TrySortOrder` validation (`400` `{"error": "..."}` on
   invalid values) and returning `{ total, page, limit, offset, order }` (with `entries`/`data` or
   section-specific item keys). On `/api/kyber/session/{id}/charts` (amended 2026-11-25),
   `context.turns` is paged and sorted on the server (`total`, `page`, `limit`, `offset`, `order`
   emitted on both measurable and unmeasurable responses, preserving 1-based `index = turn.Position + 1`),
   while whole-session `turns` (for the spend curve) and context summary aggregates (`first`, `last`,
   `flaggedTurns`, `residualTotal`, `measuredInputTotal`, `maxBucketTokens`, `observedBuckets`,
   `observedServers`, `missingBuckets`, `residualReasons`, and tokenizer labels) are computed over
   unpaged `ctx.Turns` before slicing so chart scaling, legends, and residual percentages stay exact
   across pages.
3. **Sections load at once, in page order; no scroll gating.** Every section query is enabled
   when the view mounts and issued in the order the sections appear, so the top of the page
   arrives first. Verified against SQLite: WAL journal mode, a fresh connection per read, and no
   application read lock, so the requests run in parallel; the browser's per-origin connection
   limit queues the overflow in issue order, which is page order. Scroll gating is what would
   make a slow scroller meet spinners.
4. **Content text is fetched only when it is displayed.** Opening a session issues no content
   route and no stored-fields rows request. The inspector drawer resolves a turn through
   `/session/:id/turn/:index` (widened with that turn's context) and only then its content; a
   tool opens its definitions through the tools `aggregate.definitionsSpanId`; a skill through
   its row's `turns_listed`; a span from the loaded timeline node or `/span/:id/attributes`.
   Three scoped content reads are sanctioned, each pinned to the screen that shows it: the
   shown requests page's prompt previews (`RequestPromptPreviews`), the inspected span's
   consent-allowed prompt (`SpanPromptText`, restoring what the stall fix lost), and the opened
   stored table's page (`StoredFieldsRows`, with the inventory content-free). The timeline
   builds its tree from content-free record metadata once per page — synthetic `(auxiliary)`
   and `(orphan:…)` nodes included — because node ids are created by the build, not stored.

## Alternatives Considered

- **One payload per view, smaller.** Rejected: a smaller monolith keeps one slow section
  blocking all others and re-centralizes every future section's cost negotiation.
- **Two-tier split (header plus one body call).** Rejected by review: the body call is the same
  monolith with a faster header, and one slow section still blocks all others.
- **Load on scroll.** Rejected by the owner: a viewer who scrolls at reading speed must never
  meet a spinner, so nothing waits for scrolling. The mount-ordered parallel issue is what
  guarantees it.
- **Client-side paging over full section reads.** Rejected: paging in the browser still
  materializes every row on the server for every view, which is the over-reading the sessions
  list was just cured of.
- **Keyset cursors instead of page numbers.** Rejected: page buttons need page numbers and a
  total; keyset cursors cannot jump to page N. The one-row shift a live capture can cause
  between pages is accepted and stated in the runbook.
- **Downsample the chart series now.** Rejected as scope: the chart payload on a very long
  session is measured and reported, and downsampling is a named follow-up, not a blocker.

## Consequences

- **The read-model register** in
  [the KyberDash architecture](../dash/architecture.md#read-path-invariants-and-the-read-model-register)
  lists every section route, the one purpose-named read that answers it, and whether it reads
  content text. It is the contract a new section is written against.
- **The endpoint sweep** `AnalyticsReadPathTests.EveryOtherPathLeavesContentUnread` covers 29
  request paths over an instrumented fake and fails on the first materialized content byte; the
  two sanctioned prompt reads are pinned to the span-attributes and requests paths, and every
  section route carries its own zero-content pin in `SessionSectionEndpointsTests`.
- **The web contract** is one query per section in page order with paged lists at 25/50/100
  (default 25, sorted `desc` by default;
  [web dashboard data loading](../dash/architecture.md#web-dashboard-data-loading-by-section-and-page)),
  pinned in Vitest (request order before any response resolves; no content route at mount) and
  typechecked end-to-end specs that run in CI's `Dash harness-collection e2e` job.
- **Known follow-ups, not blockers:** the skills section serves no whole-list aggregate yet
  (headline, harness aggregate and bar scale stay page-scoped), the spend `turns` series has no
  server-side downsampling, and the run page renders its turn list unpaged.
- Changing a section to read content outside its sanctioned scope, or widening a read beyond
  its section, is a change to this record, not a local decision.

## Related

- [ADR 0091](0091-store-reads-are-shaped-by-their-consumer.md) — the read-shape rule this record builds on: one purpose-named read per consumer, content only where consumed
- [KyberDash architecture](../dash/architecture.md#read-path-invariants-and-the-read-model-register) — the read-model register, the maintenance-read boundary, deferral and single-flight
- [KyberDash architecture](../dash/architecture.md#web-dashboard-data-loading-by-section-and-page) — the web data-loading contract
- [KyberDash runbook](../dash/runbook.md#11-slow-dashboard-responses-on-a-large-store) — what a viewer sees now: header first, sections filling in page order, pages fetched on demand
- [Plan: 2026-11-23 lazy sectioned session views](../archive/plans/2026-11-23-kyberdash-lazy-sectioned-session-views.md) — the twenty decisions and the request trace this record harvests
