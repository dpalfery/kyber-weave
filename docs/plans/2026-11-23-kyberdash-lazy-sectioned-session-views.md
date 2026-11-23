---
id: plans/2026-11-23-kyberdash-lazy-sectioned-session-views
title: Load KyberDash session views by section and by page
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-11-23
development-mode: test-first
documentation-impact: extends-component
impact-components:
  - KyberDash
---

# Load KyberDash session views by section and by page

**Status: Ready, 2026-11-23.**

## Problem and goal

The previous change on this branch (archived plan
[2026-11-22-dash-api-stalls-on-large-store](../archive/plans/2026-11-22-dash-api-stalls-on-large-store.md))
made every dashboard read content-free and consumer-shaped. It did not change *how much* each
request asks for. The owner reviewed the result and asked for a lazy-load design: the sessions
page loads only what the list shows, opening a session loads that view, each section of the view
has its own API call, every list pages, and conversation text is fetched only when it is shown.

What the code does today (verified at `3dc143af2`, the head of this branch):

1. **The sessions list is unpaged and over-reads.** `ContextExplorer` (`dash/web/src/components/ContextExplorer.tsx`)
   fetches `/api/kyber/sessions` with no limit and filters by harness in the browser.
   `KyberBridgeEngine.GetSessions` reads `ListSessionProjections(harness)` — every session's full
   projection, including every `session_turn`, context and schema row
   (`SqliteRelationalStore.Reader.cs`, `QuerySessionProjections`) — calls `reader.GetSession` once
   per row, prices every session through `SessionListFigures` (which itself calls
   `Sessions(harness)` and `ListSessionProjections(harness)` internally), and only then cuts the
   roots to `limit`.
2. **Opening a session is one monolithic call.** `AgentSessionLoader`
   (`dash/web/src/components/AgentSessionDashboard.tsx`) issues a raw `fetch` (outside the shared API
   client, against the React standard) for `/api/kyber/session/:id`, and nothing renders until the
   server has built every section: header, all turns, tools, servers, schema, context,
   reconciliation, the whole timeline tree, requests, skills and notes
   (`KyberBridgeEngine.GetSessionDetail`). A 10,000-turn session returns all its turns and the full
   span tree in one response.
3. **No list on the session view pages.** Tools, servers, skills, requests, reconciliation rows,
   problems, timeline nodes and the stored-fields tables all render every row.
4. **Other pages pull the whole sessions list to use one row.** `RunDetail` fetches the entire list
   to find one session; `ContextDoctor` loads it into a `<select>` picker.
5. **The report endpoint** reads `SessionSummaryFigures` once per session in its window.
6. **A regression already on this branch: the consent-allowed prompt is gone.** The stall fix
   made `GetSpanAttributes` content-free and dropped `AttachCapturedPrompt`, and request rows'
   `prompt` is always `null` on the content-free path (`ExtractReaderPromptPreview` reads `Parts`,
   which the content-free read leaves empty). CI's `Dash harness-collection e2e` job fails on it
   (`harness-collection.spec.ts:494`, "the consent-on session carries the prompt text the consent
   allowed").

**Goal.** The sessions page and the session view load in small, independent pieces:

- The sessions list is paged on the server, **100 root sessions per page**, filtered and searched on
  the server, and priced only for the page it returns.
- The session view is a set of sections, each with **its own endpoint and its own query**. A section
  waits only for its own data.
- **Every list section pages at 25 rows.** Only the first page is fetched; the next page is fetched
  when the viewer presses the page button.
- On opening a session, the header and every section's first page are requested **at once, in the
  order the sections appear on the page**, so the top of the page arrives first and a viewer who
  scrolls at reading speed does not meet a spinner. A fast scroll may briefly show one.
- Conversation text is fetched only when it is displayed: the inspector drawer, the turn page, the
  request prompt previews of the page of requests being shown, the span inspector's consent-allowed
  prompt, and stored-fields rows of a table the viewer opened.
- The monolithic `/api/kyber/session/:id` route and `GetSessionDetail` are retired.

## Request trace

| Request instruction | Covered by | Evidence that it is met |
|---|---|---|
| "the sessions page should only load the content needed to fill in the initial list of sessions" | D3, D4; T2, T3 | `/sessions` pages in SQL before pricing; header-only projection read; keyed figures with no unkeyed read (SQL-recording adapter test); no per-row `GetSession`. |
| "when a session is opened a new query / load should get the information for that view … each section of the page has its own api call" | D1, API contract; T4–T8, T9–T13 | One endpoint per section; `AgentSessionDashboard` becomes section components, each with its own TanStack query; the monolithic route is retired (T14, T15). |
| "the text/context pull should not happen till it needs to be displayed" | D9, D15, D16; T1, T5, T8, T10, T12, T13 | Content reads are scoped to what is on screen (API contract, last column). Sanctioned on-screen exceptions: the 25 request prompt previews of the shown requests page, the consent-allowed prompt of the span the viewer inspects, and the rows of a stored-fields table the viewer opens. A Vitest pins that opening a session issues no content-route and no stored-fields-rows request; the endpoint sweep covers every new route. |
| "look at our current api and ui design and assess what needs to be changed" | Problem and goal | The six verified gaps above, each with a task. |
| "go with recommended … per-section calls, load on scroll, paged lists" | D1, D2, D5 | Per-section calls and paged lists as recommended; "load on scroll" was replaced by the owner's later instruction (next rows). |
| "when we have more than 25 rows for any one of the list sections, it should page … only load the first 25 … as someone clicks the page button, we go get the next 25" | D5, D6; T3, T5–T8, T11–T13 | Every list section endpoint pages at 25 with a total; one shared pager fetches a page only when pressed. Charts are not lists (D7). |
| "pages … load in order of their appearance on the page … lists lower on the page should load automatically … if I scroll slowly, I should never see a spinning button" | D2; T10 | All section queries mount with the view and are issued in page order; nothing waits for scrolling. |
| "Why not run the individual requests in parallel? Will SQLite not support parallel reads?" | D2 | Verified: WAL journal mode (`SqliteRelationalStore.Version.cs`), a fresh connection per read (`BaseSqliteRepository.ExecuteSqliteCore`), no application lock on reads (`writeGate` guards writes only). Requests run in parallel; the browser's per-origin connection limit (six on HTTP/1.1) queues the overflow in issue order, which is page order. |
| "Let's do 100 on the main session list" | D4; T2, T3 | Sessions page size 100; section page size 25. |
| "push everything to branch 666 / PR 666 … we'll probably close 662" | D10 | Work lands on `factory/fix/dash-api-stalls-on-large-store` (PR 666), which already carries 662's branch merged. |
| "send it to open code to do the work, but you do the plan … conductor flow" | D11 | Authored by Claude (Opus); executed by a conductor run on opencode. |

## Scale

`documentation-impact: extends-component`; `impact-components: KyberDash`.

Server: new purpose-named reads on `IRelationalStoreReader` and `IProjectionStore`, new section
endpoints, new engine methods on `KyberBridgeEngine` and `IDashboardQueryService`, and retirement of
`GetSessionDetail`, `SessionDetailFigures`, the unpaged `GetSessions(limit, harness)` signature and
the per-session report loop. Every interface change moves all implementers together:
`SqliteRelationalStore` (reader and projection store), `InMemoryRelationalStore`
(`dash/server.tests/KyberDash.Application.Tests/Fakes/InMemoryRelationalStore.cs`), and the
test-local readers `FaultingSessionReader` (`dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsReadPathTests.cs`,
`dash/server.tests/KyberDash.Application.Tests/Analytics/AnalyticsOperationsDetailTests.cs`) and
`DelegatingReader` (`dash/server.tests/KyberDash.Application.Tests/Projection/ProjectionReadPathTests.cs`), plus the Moq setups on
`ListSessionProjections` / `GetSessions` in `AnalyticsErrorEnvelopeTests.cs`,
`AnalyticsOperationsDetailTests.cs`, `dash/server.tests/KyberDash.Presentation.Tests/MinimalApiEndpointTests.cs` and
`dash/server.tests/KyberDash.Presentation.Tests/PresentationBuilder.cs`. The compiler enumerates any other.

Web: `ContextExplorer`, `AgentSessionDashboard` (split into section components),
`SessionSpendCharts`, `SchemaCostRanking`, `SkillsPanel`, `SessionCostPanel`, `StoredFieldsPanel`,
`TimelineView` usage, `SessionInspectorDrawer`, `RunDetail`, `ContextDoctor`, `TurnDetail`,
`App.tsx` navigation, and `lib/kyberApi.ts` (fetchers and response validators). End-to-end specs
under `dash/e2e/` that read the changed routes.

**No canon schema change.** `RelationalSchemaCatalog` is untouched and `SchemaVersion` stays 25 (a
bump deletes every operator's store unread). New reads are queries over the existing tables; no
new table, column or index.

## API contract

All routes are `GET` under `/api/kyber`, answer `Cache-Control: no-store` JSON like their siblings,
return 404 for an unknown session, and 400 for a malformed `page`, `limit`, index or `table`.

**Paging (D5).** Paged routes reuse the existing `DashHttp.Pagination(query, defaultLimit, out invalid)`
parameters (`limit`, `page` 1-based, `offset`) and the existing envelope shape the findings,
quarantine and problems routes already answer: `{ entries, data, total, page, limit }` (`data` is the
existing alias of `entries`). Section routes default and clamp `limit` to 25. A page past the end
answers empty `entries` with the true `total`. Every section payload also carries
`projection_status` when that session's read faulted, as today's detail payload does.

| Route | Answers | Read (purpose-named) | Content text |
|---|---|---|---|
| `/sessions?harness=&q=&limit=&page=` (and the `/session` alias) | `{ sessions, total, page, limit }` — the existing `sessions` key keeps today's root row fields, each root with its first 25 `subagents` and `subagentTotal`; `limit` is the page size (default 100, clamp 1..100), so `?limit=10` keeps today's meaning for the first page; `q` matches label or session id, case-insensitive substring, with `%`, `_` and `\` escaped | `SessionHeaderPage` (projection header columns only, roots resolved, filtered and paged in SQL) + `SessionChildPreview` (one batched statement for the page's subagents) + keyed `SessionListFigures` | No |
| `/session/:id/subagents?page=` | Envelope of one root's subagent rows, 25 per page | `SessionChildHeaderPage(parentId)` + keyed `SessionListFigures` | No |
| `/session/:id/summary` (exists, widened) | Today's fields plus everything the header strip, banners and overview strip render: `span_count`, `notes`, `costBasisMismatch`, `request_count`, `reconciliation: { total, mismatched }`, `problems_count`, `tools_offered_count`, `tools_invoked_count`, `unused_tools_count` (offered minus invoked by name, computed on the server), `skills_count`, aux-call fields, schema waste fields (`schema_waste_cost`, `unused_schema_per_turn`, `schema_tokens_per_turn`, `defs_turns`), and the cost blocks by basis and model | `SessionSummaryFigures` | No |
| `/session/:id/charts` | Exactly today's detail payload `turns` array (the `ToTurnDto` shape) and `context` object (the `BuildContextPayload` shape, including `flaggedTurns`, `sessionAccumulationRate`, `derivedCounts`, `freshJumpFactor`, `residualTotal`, `unmeasuredTurns`, `contextLimit`, per-turn `toolDefinitionsByServer`, `residual`, `headroom`, `first`, `last`). **Not paged (D7).** | `SessionTurnFigures` + the stored context projection | No |
| `/session/:id/tools?page=` | Envelope of ranked rows, never-invoked tools included and flagged (today's `ToolRows` fields: `name`, `server`, `invocations`, `schema_tokens`, `turns_resident`, `total_schema_cost`, `invoked`, `in_definitions`, `is_mcp`) plus a page-independent `aggregate`: `measurable` / `reason`, `maxCost`, `residentCostTotal`, `invocationCount`, `unusedCount`, `unusedRange`, `byServer` totals, `definitionsSpanId` | `SessionToolFigures` | No |
| `/session/:id/servers?page=` | Envelope of server rollup rows (today's `ServerRows` fields) | `SessionToolFigures` | No |
| `/session/:id/skills?page=` | Envelope of skill rows (today's `skills.rows` fields) plus `aggregate`: `measurable` / `availability`, `reason`, `maxTokens` | `SessionSkillFigures` | No |
| `/session/:id/requests?page=` | Envelope of request rows: `request`, `timestamp`, `turns`, `model`, `prompt` (preview) | `SessionRequestFigures` + `RequestPromptPreviews(sessionKey, requestSpanIds)` for the page's rows only | **Yes — prompt previews of the requested page only (D16)** |
| `/session/:id/reconciliation?page=` | Envelope of reconciliation rows (today's snake-case fields, null-match rows omitted as today) | `SessionReconciliationFigures` | No |
| `/session/:id/problems?page=` | Envelope of problem rows | `SessionProblems` | No |
| `/session/:id/timeline?parent=&page=` | Envelope of one tree level's nodes (today's node fields **without** `children`, plus `childCount`); `parent` omitted = top level, otherwise a node id URL-encoded by the client and decoded by the server (synthetic ids such as `(auxiliary)` included); plus `bounds: { startMs, endMs }` | `SessionTimelineLevel` (D17) | No |
| `/session/:id/turn/:index` (exists, widened) | Today's turn figures plus that turn's `context` buckets | `TurnFigures` + the stored context turn | No |
| `/session/:sessionKey/stored-fields` (exists, changed) | Inventory only: table names, columns, row counts, missing parent keys — **no row values** | `StoredFieldsInventory` | No |
| `/session/:sessionKey/stored-fields/:table?page=` (new) | Envelope of that table's rows; `table` must be a table the inventory lists (catalog allowlist), else 404 | `StoredFieldsRows(sessionKey, table, page, limit)` | Yes — one table, one page, only when the viewer opens it (D15) |
| `/span/:id/attributes` (exists, fixed) | Today's fields; for a Droid span whose consent allowed it, the captured prompt in `attributes` as before the stall fix | `RecordContentFree` + `SpanPromptText(spanId)` (that one span's prompt part) | Yes — one span's prompt, only for the inspector the viewer opened (D16) |
| `/session/:id/content`, `/session/:id/turn/:index/content` | Unchanged | `TurnContentParts` | Yes — unchanged |
| `/session/:id` | **Retired (T14)** | — | — |

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | How is the session view split? | One endpoint and one TanStack query per section (header/overview, charts, tools, servers, skills, timeline, requests, reconciliation, problems, stored fields). Each section component owns its query and renders its own loading, error, empty and data states. | The React standard already requires "a section waits only for its own queries". The rejected two-tier split keeps one slow section blocking all others. | **Owner decision 2026-11-23** ("go with recommended") |
| D2 | When does each section load? | **At once, in page order.** Every section query is enabled when the view mounts and is issued in the order the sections appear; no scroll gating. The one exception is stored-fields rows (D15). | Verified SQLite serves parallel reads here: WAL, a fresh connection per read, no read lock. The browser caps connections per origin (six on HTTP/1.1), so overflow queues in issue order — page order. Scroll gating is what would make a slow scroller see spinners. | **Owner decision 2026-11-23** (parallel, in page order) |
| D3 | How does the sessions list avoid reading every session? | `IProjectionStore` gains `SessionHeaderPage(harness?, search?, page, limit)`, `SessionChildHeaderPage(parentId, page, limit)`, `SessionChildPreview(parentIds, perParent)` and `SessionHeadersByKeys(keys)`, reading only `session_projection` header columns. Root resolution, harness filter, search, `ORDER BY started_at_utc DESC, session_id` and `LIMIT/OFFSET` run in SQL with a `COUNT(*)` total. `IRelationalStoreReader` gains a keyed `SessionListFigures(IReadOnlyCollection<string> keys, pricing)` whose SQLite implementation never calls `Sessions(harness)` or `ListSessionProjections` (it reads the page's session rows, header columns and records by key). The page build drops the per-row `reader.GetSession` call (repo and branch come from the figures). | Today's list reads every session's full projection twice and every session row once per row, then discards all but `limit`. The root rule stays today's: a session is a root unless its parent is a projected session within the same harness filter and attaching it would not form a cycle. The page's subagent preview (first 25 children per root plus each root's child total) is **one** statement for all roots on the page — a `row_number() OVER (PARTITION BY parent_session_id ORDER BY started_at_utc, session_id)` window query filtered to `<= 25`, with the totals from the same pass — never one query per root. Cycle handling: a session whose parent chain loops back to itself is treated as a root, as today; the chain is resolved by a recursive CTE over `session_projection` (`parent_session_id` links, bounded by the store's session count) inside the header query, so ancestors off the page are seen. The parity seed includes a cycle whose members fall on different pages. | autonomous: follows `KW-STD-SQL-001/003` |
| D4 | Page sizes | Sessions list: 100 roots per page. Every list section, subagent lists included: 25 rows per page. | — | **Owner decision 2026-11-23** ("100 on the main session list"; "more than 25 rows … should page") |
| D5 | Paging convention | Reuse the existing `DashHttp.Pagination` parameters and the existing `{ entries, data, total, page, limit }` envelope for every section list, produced by one engine helper (extracted from the findings/quarantine envelope code, `KyberBridgeEngine` around the quarantine listing). `/sessions` keeps its existing `sessions` key and `limit` parameter and adds `total` and `page`, so its other consumers (e2e specs, Presentation tests, tray tests) keep working on a small store. Offset paging by page number. | The repository already has a paging convention; a second one would be duplicate implementation. Page buttons need page numbers and a total; keyset cursors cannot jump to page N. Under live capture a new session can shift the sessions list by one row between pages; accepted for a monitoring view and stated in the runbook. | autonomous: follows existing convention (`DashHttp.cs`) |
| D6 | Which lists page? | Every list a section renders: sessions, subagents, tools, servers, skills, requests, reconciliation, problems, timeline nodes at each level, stored-fields rows per table. Not paged: cost blocks by basis (one per basis) and harness notes (a fixed per-harness set). | The owner's rule is "more than 25 rows for any one of the list sections"; those two lists cannot exceed a handful by construction. | autonomous: low-confidence — owner may veto |
| D7 | Charts over all turns | `/session/:id/charts` is not paged: it returns today's `turns` and `context` shapes for the whole session. The spend chart and context composition chart need the whole session to be correct. | Paging a chart shows a different chart per page and hides the session's shape. The payload is content-free but not small: per-turn `toolDefinitionsByServer` grows with servers × turns. It is still a fraction of today's detail payload (no tools, requests, reconciliation or span tree). The size on a large session is measured and reported in the PR notes; server-side downsampling is a named follow-up. | autonomous: low-confidence — owner may veto |
| D8 | How do section reads stay independent? | Each section endpoint calls one purpose-named read that queries only the tables and columns its section needs (tools: tool-call and tool-definition metadata plus the stored schema; reconciliation: turn token columns; requests: request rows and turn ownership; skills: skill rows; charts: turn figures plus stored context). No section read goes through `BuildSessionFigures` or `SessionRecordsContentFree`'s full record hydration unless its section needs every column it loads. A section's 404 check uses `SessionHeadersByKeys([id])`, never `GetSessionProjection` (which loads turns and context). | Splitting routes while each still ran the whole-session pass would multiply the old cost by the number of sections. Read-log tests on the fake and a SQL-recording adapter test (subclass `SqliteRelationalStore`, override the `protected virtual ExecuteSqliteCore` to capture statements) pin each route's statements. Some overlap remains (several sections read turn token columns); its cost on a large session is measured in the PR notes. | autonomous: follows `KW-STD-SQL-001` |
| D9 | How does the inspector drawer work without the session-wide turn list and span map? | It fetches what it opens: a turn through `/session/:id/turn/:index` (widened with that turn's `context`), a span from the loaded timeline node or `/span/:id/attributes`, a tool's definitions through the tools `aggregate.definitionsSpanId` and the existing content route, a skill through its row's `turns_listed`. | Today the drawer looks rows up in `session.turns`, `session.context` and a whole-timeline span map — the payload this change retires. | autonomous: derived |
| D10 | Branch and PR | `factory/fix/dash-api-stalls-on-large-store` (PR 666), which already carries PR 662's branch merged. | One plan, one PR, per the plan inventory; PR 666 then carries two plans, each archived in this PR. | **Owner decision 2026-11-23** |
| D11 | Who plans and who executes | Claude (Opus) authors and checks this plan; a conductor run on opencode executes it, on the owner's standing opencode model rule; Claude monitors, verifies and re-dispatches. | Owner instruction. | **Owner decision 2026-11-23** |
| D12 | Autonomy and development mode | `autonomy: autonomous` (`.kyber-weave/kyber-weave.yml`), `development-mode: test-first` (default). | — | autonomous: given |
| D13 | The report endpoint's per-session reads | `GetReport` prices its window's sessions with one keyed batched read, `SessionReportFigures(keys, pricing)`, carrying exactly the fields the report reads today from `SessionSummaryFigure`: `TotalInput`, `TotalCacheRead`, `ToolsOffered`, `ToolsInvoked`, `ReportedCost`, `PublishedCost`. The latest session's turns come from `SessionTurnFigures`. | Replaces the one-read-per-session loop; the report's existing figure tests stay the gate, with their read-count assertions updated to the new read names. | autonomous: derived |
| D14 | Retired routes and members, and the run page | `/api/kyber/session/:id`, `GetSessionDetail`, `SessionDetailFigures`/`SessionDetailFigure`, the unpaged `GetSessions(limit, harness)` signature and `fetchKyberSessionTurns`'s whole-payload fallback are removed. `ExtractRunTurns` (the run page, `GetRunById`) reads `SessionTurnFigures` per execution instead of `SessionDetailFigures(...).Turns`. `/api/kyber/timeline` (global) is unchanged; no UI calls it. | Verified callers: only the web UI used `/session/:id`; the engine's run page and report used `SessionDetailFigures` (`KyberBridgeEngine.cs` `ExtractRunTurns`, `GetReport`). No tray, MCP or CLI caller. | autonomous: verified by search and plan review |
| D15 | Stored fields reads content — when may it load? | The stored-fields section loads only the inventory at mount (names, columns, counts, missing keys; no row values). A table's rows load when the viewer opens that table, one page of 25 at a time. | `KW-STD-SQL-002`: content text is read only where it is consumed. Rows hold stored column values, including content text. | autonomous: from plan review |
| D16 | Request prompts and the span inspector's prompt are content | Both are fetched only for what is on screen: `/requests` reads prompt previews for the 25 requests of the page it returns, through `RequestPromptPreviews(sessionKey, requestSpanIds)` (a scoped read of those spans' prompt parts, preview-truncated); `/span/:id/attributes` restores the consent-allowed Droid prompt through `SpanPromptText(spanId)` (that one span's designated prompt part). The register and the content sweep record both as sanctioned scoped content reads. | Restores the behaviour the stall fix lost (CI's harness-collection e2e) without reading content on any other path. The archived plan's register already allowed the single-span prompt read for the inspector. | autonomous: from verified regression |
| D17 | Timeline paging | `BuildTimeline` creates synthetic grouping nodes (`(auxiliary)`, `(orphan:…)`, `CanonContextAnalysis.cs`), so SQL paging by parent span is not possible. `SessionTimelineLevel` builds the tree from the session's content-free record metadata (only the columns `BuildTimeline` reads), finds the requested level, and pages it with `childCount` per node. `AttachCapturedPrompt` is removed from the timeline path: it is a no-op on content-free records (no parts), and the prompt now reaches the inspector through D16. | The per-request cost is the session's metadata tree, not its content; it is recorded in the read-model register and measured in the PR notes. | autonomous: from plan review |
| D18 | How are the end-to-end specs verified? | Playwright does not run in a worker gate (it needs Chromium and a self-booted engine). T16's gate is `npm --prefix dash run typecheck`; the specs run in CI's `Dash harness-collection e2e` job, whose verdict the end-of-run council reports. Request order and content laziness are pinned in Vitest (T10, T12, T13), not in a new Playwright spec. | Plan-authoring rule: verification gates use what the host already runs. A new browser spec would be unverifiable inside the run. | autonomous: from plan review |
| D19 | Can the web chain start before the server chain finishes? | Yes. T9 depends on T3 only, so T9–T12 run beside T7–T8; T13 depends on T7, T8 and T12. | T9–T12 edit only `dash/web` files and mock every route; the API contract fixes each route's shape, so they do not need the server tasks merged first. Saves roughly one server task's duration. | **Owner decision 2026-11-23** ("any opportunity to run some of these in parallel?" — approved) |
| D20 | Can parts of T14 and T16 land early, beside T11–T13? | Yes, done by the operator as side workers and merged at `23fac1a38`: T14's report part (`SessionReportFigures` batched read, `GetReport` reads it once and the latest session through `SessionTurnFigures`; commit `5221662ee`) and T16's API-level part (every `dash/e2e` spec reads `/session/:id/requests`, the paged `/sessions` shape, or `/summary`; commit `b6ce39857`). T14 still retires `/api/kyber/session/:id`, `GetSessionDetail` and `SessionDetailFigures`; T16 still updates `data-testid` locators to the final markup and confirms no spec reads the retired route. | Neither part depends on T11–T13: the report is server-only, and the e2e edits touch only API reads. Whole .NET suite (1,056) and web suite (1,098) verified on the merged branch. | **Owner decision 2026-11-23** ("start both in parallel") |
| AP1 | Approve and execute | Approved for execution as planned. | Recorded by the finalize step, which changed no task, file scope, test row or command. | ANSWERED: autonomous: recommendation accepted, AUTONOMOUS_APPROVAL: approve-and-execute — owner chose the recommended lazy-load design, 25-row section pages, 100-row sessions page, parallel section loading in page order, PR 666, opencode execution (2026-11-23); plan reviewed adversarially twice (22 findings closed), 2026-11-23 |

## Design harvest

| ID | Target component | Target doc type | Section | Content to harvest |
|---|---|---|---|---|
| H1 | KyberDash | architecture (`docs/dash/architecture.md`) | Read-model register; Backend REST API contract | Every new, widened and retired route with its purpose-named read and content policy (the API contract table); the paging convention; the sanctioned scoped content reads (request prompt previews, span prompt, stored-fields rows). |
| H2 | KyberDash | architecture (`docs/dash/architecture.md`) | Web dashboard data loading (new section) | Sectioned views: one query per section, issued at mount in page order and served in parallel (WAL, per-read connection), each list paged at 25 (sessions at 100) through the shared pager, charts unpaged by design, content only on display, stored-fields rows on demand. |
| H3 | KyberDash | runbook (`docs/dash/runbook.md`) | §11 Slow dashboard responses on a large store | What a viewer sees now: header first, sections filling in page order, pages fetched on demand; the live-capture row shift on the sessions list; the chart payload size note. |
| H4 | KyberDash | ADR (`docs/adr/0092-dashboard-views-load-by-section-and-page.md`) | Lazy sectioned views | The durable decision, building on ADR 0091 (reads shaped by their consumer): views load by section, each section has its own endpoint and read, lists page server-side, content loads only on display — with the rejected alternatives (one payload per view, two-tier split, scroll-gated loading, client-side paging) and `decided-by` provenance. ADRs 0007, 0011, 0014 and 0041, which cite the retired `/session/:id` payload, get a supersession note pointing to 0092, not a rewrite. |

## Completion summary

Verified by docs-dev at closeout against the run branch
`factory/fix/dash-api-stalls-on-large-store` at `5ee2ae2b` (30 commits after the
`3dc143af2` base; T1–T16 all landed). Each criterion below was checked against the
test and the code it names — a green summary from an earlier session was not taken
as evidence. No task list uses checkboxes in this plan, so there was nothing to tick.

| Task | Evidence |
|---|---|
| T1 | `AnalyticsReadPathTests.SpanAttributesCarryTheConsentAllowedDroidPrompt` pins the restored prompt; the sweep records `SpanPromptText` only on the span-attributes path; `IRelationalStoreReader.SpanPromptText` and the `FactoryKeys.PromptText` attach verified in code. |
| T2 | `SessionListPagingTests` (page 2 of 250 roots, literal `%_\` search, 25/25 subagent preview in one window statement, empty past-the-end, 400 on malformed paging, `limit=10` first-ten parity) plus the SQL-recording `TheSessionsPageIssuesNoUnkeyedStatement`; `GetSessions(limit, harness)` is gone and `GetSessionPage`/`GetSubagentPage` serve the routes. |
| T3 | `ContextExplorer` requests `/sessions?page=&limit=100&harness=&q=`, pages only on press, subagent pager hits `/subagents?page=2`; `RunDetail` resolves through `/summary`; `ContextDoctor` is a search combobox validating through `/summary`; `Pager.tsx` and the `KyberPage` envelope validator exist in `lib/kyberApi.ts`. |
| T4 | `SessionSectionEndpointsTests.TheSummarySectionCarriesTheContractFields`, `TheChartsSectionCarriesTheTurnFiguresTurnsAndContext`, `TheTurnSectionCarriesThatTurnsContextBuckets` (128-turn parity, no tool/skill/request/timeline/content read); `ExtractRunTurns` reads `SessionTurnFigures`. |
| T5 | `TheRequestsSectionPagesAt25AndCarriesTodaysRows`, `TheReconciliationSectionPagesAt25AndCarriesTodaysRows`, `TheProblemsSectionPagesStoredIngestProblems` (request previews scoped to the page's spans). |
| T6 | `TheToolsSectionPagesRankedRowsAndCarriesTodaysAggregate` (aggregate over all rows before paging, also on page 2), `TheServersSectionPagesTodaysRowsAt25`, `TheSkillsSectionPagesTodaysRowsAt25`, with SQL-recording statements limited to tool/skill tables and the stored schema. |
| T7 | `TheTimelineSectionPagesTheTopLevelAt25WithChildCountAndBounds`, the synthetic-parent page test, `TheUnionOfTimelineLevelsEqualsTodaysBuildTimelineTree`; no content read; `AttachCapturedPrompt` is out of the timeline path. |
| T8 | `StoredFieldsRowsReturnOnePageOfTheNamedTableScopedToTheSession`, `StoredFieldsRowsPageTwentyFiveRowsAtATime`, SQL-level `LimitsThePageInSql` / `ReturnsNullForATableOutsideTheCatalog`; inventory route carries no row values. |
| T9 | One fetcher and one validator per section route in `lib/kyberApi.ts` with `['kyber-session', id, section, page]` keys; malformed payloads reject. |
| T10 | `AgentSessionDashboard` mounts the ten section components in page order, each owning one `useSectionPage` query; the mount-order test asserts the section requests before any response resolves with no content or rows request; no `fetch` outside `lib/kyberApi.ts` (only query `refetch` callbacks remain). |
| T11 | Charts render from `/charts`; ranking, skills and cost sections read their routes with page-25 lists. Known gap carried to the architecture docs and PR notes: the skills section forwards the page's rows alone, so its tiles/headline read "not measurable" and token bars scale to the loaded page until the route serves the whole-list aggregate (the section `aggregate` prop already exists for it). |
| T12 | Drawer openers resolve turn (`/turn/:index`, then content), tool (`aggregate.definitionsSpanId`), skill (`turns_listed`) and span (`/span/:id/attributes`) by key. The web two-route chain landed here. |
| T13 | Timeline expands by level with URL-encoded parent ids and per-level paging against `bounds`; `StoredFieldsPanel` loads a table's rows only on open, 25 at a time. |
| T14 | `GET /api/kyber/session/:id` answers 404 (route gone); `SessionDetailFigures`/`GetSessionDetail`/unpaged `GetSessions` are gone from contracts and implementers; report reads one `SessionReportFigures` plus one `SessionTurnFigures` with unchanged figures. |
| T15 | `fetchKyberSessionTurns` is a thin wrapper over the per-turn route with no whole-payload fallback; the session-payload fetch and its unused types are gone; `TurnDetail` always requests `/turn/:index`. |
| T16 | `harness-collection.spec.ts` reads the consent-on prompt from `/requests` and pages `/sessions`; no spec reads the retired route or relies on the whole list for a known id; typecheck is the gate. NOT VERIFIED: the CI `Dash harness-collection e2e` job verdict on the pushed branch (D18) — needs CI, recorded here and reported by the end-of-run council, not a closeout blocker. |
| T17 | This task: H1–H4 harvested, ADR 0092 registered with supersession notes and the 0091 cross-reference, Closeout mapping above complete; `docs validate` and `docs drift` clean (gates below). |

NOT VERIFIED: T16's CI `Dash harness-collection e2e` verdict — requires the CI environment, not this worktree.

## Closeout mapping

| ID | Destination | Documentation impact | Impact components | Last reviewed | Status |
|---|---|---|---|---|---|
| H1, D3, D8 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — every new, widened and retired route with its purpose-named read and content policy; the paging envelope; the boundary paragraph (29 swept paths, the two prompt-read pins, per-section zero-content pins); [SQLite standard](../standards/sqlite/sqlite.standards.md#store-reads-are-shaped-to-their-consumer) and [test standard](../standards/test/test.standards.md#read-path-tests-assert-the-shape-of-the-reads) worked examples updated to the section reads | extends-component | KyberDash | 2026-11-23 | harvested |
| H1, D14 | [architecture, Backend REST API contract](../dash/architecture.md#backend-rest-api-contract-dashserverkyberdashapiendpointsdashendpointmappingscs) — paged `/sessions`, the retired `/session/:id` row, the widened `/summary` and `/turn/:index` rows, one row per section route, inventory plus per-table stored-fields rows; [architecture, collector attribution](../dash/architecture.md#collector-attribution-and-read-side-api-projection) — the retired payload bullet replaced by the section projections | extends-component | KyberDash | 2026-11-23 | harvested |
| H2, D1, D2, D9 | [architecture, web dashboard data loading by section and page](../dash/architecture.md#web-dashboard-data-loading-by-section-and-page) — one query per section through `useSectionPage`, issued in page order and served in parallel, every list paged at 25 (sessions at 100) through the shared `Pager`, charts unpaged by design, content only on display, the drawer fetch order, the paging envelope | extends-component | KyberDash | 2026-11-23 | harvested |
| H2, D4, D5, D6 | [architecture, web dashboard data loading](../dash/architecture.md#web-dashboard-data-loading-by-section-and-page) — 100 roots / 25 rows, the reused envelope, which lists page and which never do; [ADR 0092](../adr/0092-dashboard-views-load-by-section-and-page.md) (Decision 2 and 3) | extends-component | KyberDash | 2026-11-23 | harvested |
| D7 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — the `/charts` row (whole-session `turns` and `context`, not paged by design); the REST table `/charts` row | extends-component | KyberDash | 2026-11-23 | harvested |
| D13, D14 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — the `/report` row (one keyed `SessionReportFigures` read, latest turns from `SessionTurnFigures`) and the retired `/session/:id` row; [ADR 0092](../adr/0092-dashboard-views-load-by-section-and-page.md) (Decision 1) | extends-component | KyberDash | 2026-11-23 | harvested |
| D15 | [architecture, stored-fields surface](../dash/architecture.md#analytics-read-path-error-envelope-and-stored-fields-surface) — inventory at mount, per-table rows on demand; [architecture, sanctioned exceptions](../dash/architecture.md#sanctioned-exceptions); [runbook §11](../dash/runbook.md#11-slow-dashboard-responses-on-a-large-store) — the inventory is content-free, table pages ride in SQL at 25 | extends-component | KyberDash | 2026-11-23 | harvested |
| D16 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — the `/requests` and `/span/:id/attributes` rows with the two sanctioned prompt reads; [architecture, sanctioned exceptions](../dash/architecture.md#sanctioned-exceptions); [ADR 0091](../adr/0091-store-reads-are-shaped-by-their-consumer.md) (Decision 2, sanctioned exceptions) | extends-component | KyberDash | 2026-11-23 | harvested |
| D17 | [architecture, the read-model register](../dash/architecture.md#the-read-model-register) — the `/timeline` level row (`SessionTimelineRecords`, `childCount`, `bounds`); [architecture, data loading](../dash/architecture.md#web-dashboard-data-loading-by-section-and-page) — levels page on expand | extends-component | KyberDash | 2026-11-23 | harvested |
| D18 | [ADR 0092](../adr/0092-dashboard-views-load-by-section-and-page.md) (Consequences) — specs typechecked in the gate, run in CI's `Dash harness-collection e2e` job; the council reports that job's verdict (recorded NOT VERIFIED in the completion summary below) | extends-component | KyberDash | 2026-11-23 | harvested |
| H3 | [runbook §11](../dash/runbook.md#11-slow-dashboard-responses-on-a-large-store) — header first with sections filling in page order, pages fetched on demand, the live-capture row shift, the chart payload note, deferred sessions unchanged, what stays heavy by design | extends-component | KyberDash | 2026-11-23 | harvested |
| H4 | [ADR 0092](../adr/0092-dashboard-views-load-by-section-and-page.md), registered in the [ADR index](../adr/README.md) — the durable decision with rejected alternatives and owner-decision provenance; supersession notes on [ADR 0007](../adr/0007-kyberdash-agent-session-analysis-integration.md), [ADR 0011](../adr/0011-asad-only-context-view-and-payload-contract.md) and [ADR 0041](../adr/0041-kyberdash-retire-legacy-context-schema-routes.md); cross-reference from [ADR 0091](../adr/0091-store-reads-are-shaped-by-their-consumer.md) | extends-component | KyberDash | 2026-11-23 | harvested |
| H4 (0014) | Documentation waiver — ADR 0014's cited routes (`/session/:id/turn/:index/content`, `/session/:id/content`) still stand unchanged and its drawer flow is refined, not retired, by sections; a supersession note would misstate the record. The drawer refinement is covered by H2's destination | none | KyberDash | 2026-11-23 | waived |
| D10, D11, D12, D19, D20, AP1 | Documentation waiver — branch and PR choice, who plans and executes, autonomy and test-first mode, the approved parallel starts, and the approve-and-execute record: run provenance with no surviving architectural, operational or behavioural invariant for future readers or agents to consult | none | KyberDash | 2026-11-23 | waived |

## Investigation findings

Verified in this worktree at `3dc143af2` and by an adversarial plan review:

- `GetSessions` (`KyberBridgeEngine.cs`) reads `projections.ListSessionProjections`, calls
  `reader.GetSession` per row, prices all sessions, builds the tree, sorts roots by `Started`
  descending, and applies `limit` last. `SqliteRelationalStore.SessionListFigures` internally reads
  `Sessions(harnessId)` and `ListSessionProjections(harnessId)`.
- `QuerySessionProjections` selects, per session, the projection row plus `session_request`,
  `session_turn`, `session_context*` and schema rows.
- `GetSessionDetail` builds the payload from `ReadSessionDetailFigures`, `ReadSessionTimeline`
  (`TimelineChildren` → `SessionRecordsContentFree` → `CanonContextAnalysis.BuildTimeline`), and the
  helpers `BuildContextPayload`, `BuildSchemaPayload`, `BuildNotes`, `ToolRows`, `ServerRows`,
  `ReconciliationRows`, `RequestRows`, `BuildSkillsPayload`, `BuildSessionSummary`,
  `BuildCostBasisMismatchObject` — the field definitions the section endpoints keep.
- `SessionDetailFigures` callers: `GetSessionDetail`, `ExtractRunTurns` (run page), `GetReport`
  (latest session). Tests asserting on it or on `GetSessionDetail`: `AnalyticsReadPathTests.cs`
  (the many `GetSessionDetail` read-path tests), `SettingsCleanDoctorReportTests.cs` (report read
  counts), `ApplicationParityTests.cs`, `RelationalIngestTests.cs`,
  `dash/server.tests/KyberDash.Data.Tests/Repositories/SqliteRelationalStoreReadTests.cs`,
  `dash/server.tests/KyberDash.Domain.Tests/Contracts/StoreInterfaceShapeTests.cs`.
- Existing paging: `DashHttp.Pagination` (`dash/server/KyberDash.Api/Endpoints/DashHttp.cs`) and the
  `{ entries, data, total, page, limit }` envelope used by findings, quarantine and problems.
- Web: `AgentSessionLoader` raw `fetch`; `AgentSessionContent` renders header and banners, overview
  strip, spend and context charts, tool and schema ranking, skills, execution timeline (tree and
  duration bars), cost panel, stored fields, and the drawer. Drawer openers look rows up in
  `session.turns`, `session.context` and a whole-timeline span map. The requests banner renders
  each request's `prompt`. `SchemaCostRanking` reads `schema.ranked`, `schema.neverInvoked`,
  `schema.byServer` and derives scales over all rows. `RunDetail` reads top-level
  `turnCount`/`turn_count` from the list row it finds. `ContextDoctor`'s picker is a `<select>`
  that checks list membership for validity.
- Consumers of `/sessions` and `/session/:id` outside the web app: `dash/e2e/deep-links.spec.ts`,
  `dash/e2e/harness-collection.spec.ts` (`/session/:id` `requests[].prompt` and two `/sessions`
  reads), `dash/e2e/otel-tray-dashboard.spec.ts`, `dash/e2e/harness-collection-world.ts`;
  `MinimalApiEndpointTests.cs` (`limit=10`), `PresentationBuilder.cs`; web tests
  `ContextDoctor.test.tsx`, `App.navigation.test.tsx`, `kyberApi.turn-route.test.ts`. CI runs only
  `harness-collection.spec.ts` (`.github/workflows/ci.yml`, job `Dash harness-collection e2e`).
- Store concurrency: WAL, `Pooling = false`, `writeGate` on writes only, single-flight
  `EnsureProjected`.

## Test contract (development-mode: test-first)

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `AnalyticsReadPathTests.cs` (extend) and a Presentation endpoint test | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~AnalyticsReadPathTests"` | `SpanAttributesCarryTheConsentAllowedDroidPrompt`: a Droid span with a captured prompt part answers it in `attributes` (the key `AttachCapturedPrompt` used); a span without consent carries none; the read log shows `RecordContentFree` and `SpanPromptText` for that span only, and no session-wide read. | Fails on current code: the attributes carry no prompt. | Test passes; the content sweep records `SpanPromptText` only on the span-attributes path. |
| T2 | new `dash/server.tests/KyberDash.Application.Tests/Sessions/SessionListPagingTests.cs`; `SqliteRelationalStoreReadTests.cs` (extend); `MinimalApiEndpointTests.cs`, `PresentationBuilder.cs` | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~SessionListPagingTests"` | `TheSessionsListPagesRootsInSqlAndPricesOnlyThePage` (250 roots, page 2 → 100 rows, `total` 250; read log: `SessionHeaderPage`, `SessionHeadersByKeys`, keyed `SessionListFigures` with the page's keys; no `ListSessionProjections`, no `GetSession`); `HarnessAndSearchFilterOnTheServer` (search text with `%`, `_`, `\` and a quote matches literally); `SubagentsAttachWithTheFirst25AndATotal` (the preview is one statement for all roots on the page, asserted by the SQL-recording test); `SubagentPagesAnswer25ByPage`; `APagePastTheEndAnswersEmptyWithTheTotal`; `MalformedPagingIs400`; `LimitTenStillAnswersTheFirstTenRoots`. Adapter: `SessionHeaderPageMatchesTheTreeRule` (orphaned child, parent outside the harness filter, a cycle whose members fall on different pages); `TheSessionsPageIssuesNoUnkeyedStatement` (SQL-recording subclass: every statement on the page path filters by the page's keys or is the header page query). Page figures equal today's `GetSessions` figures on the token-less, multi-harness, empty and content-bearing seeds. | New members do not compile; current code reads every projection and prices every session. | All named tests pass; existing `GetSessions` figure assertions kept; non-zero test count. |
| T3 | `ContextExplorer.test.tsx`, `RunDetail.test.tsx`, `ContextDoctor.test.tsx`, `App.navigation.test.tsx`, new `dash/web/src/components/ui/Pager.test.tsx`, `kyberApi` tests | `npm --prefix dash run test:related -- <changed files>` | The list requests `/api/kyber/sessions?page=1&limit=100` with harness and `q` on the query string; next requests page 2 only on press; loading, error, empty and data states render; a root with 30 subagents shows 25 and its pager requests `/session/:id/subagents?page=2`; `RunDetail` resolves its session through `/session/:id/summary` and shows `summary.turn_count`; the `ContextDoctor` picker is a search combobox over `q`, and a deep-linked session not on page 1 resolves through `/summary` (not reported unavailable); a malformed envelope renders the error state. | The explorer requests the unpaged list; no pager; the picker checks list membership. | Named tests pass; `typecheck` and `lint` clean. |
| T4 | new `dash/server.tests/KyberDash.Application.Tests/Sessions/SessionSectionEndpointsTests.cs`, Presentation endpoint tests | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~SessionSectionEndpointsTests"` | `/summary` carries every field the API contract lists (parity with today's `GetSessionDetail` `summary`, `notes`, `costBasisMismatch`, `span_count`, reconciliation totals, `unused_tools_count`); `/charts` equals today's `GetSessionDetail` `turns` and `context` on a 128-turn seed and issues no tool, skill, request, timeline or content read; `/turn/:index` carries that turn's context buckets; `ExtractRunTurns` answers the same run turns from `SessionTurnFigures`; section 404s use `SessionHeadersByKeys`. | Routes and reads do not exist. | Named tests pass; `EveryOtherPathLeavesContentUnread` includes the new routes. |
| T5 | `SessionSectionEndpointsTests.cs` (extend) | same filter as T4 | `/requests`, `/reconciliation`, `/problems` page at 25 with totals and equal today's rows; `/requests` reads prompt previews only for the page's request spans (`RequestPromptPreviews` called once with ≤ 25 ids); each route's read log names only its own reads. | Routes and reads do not exist. | Named tests pass; the sweep records `RequestPromptPreviews` only on `/requests`. |
| T6 | `SessionSectionEndpointsTests.cs` (extend) | same filter as T4 | `/tools` pages ranked rows (never-invoked included) at 25, and its `aggregate` equals what today's payload yields over all rows (`maxCost`, `residentCostTotal`, `invocationCount`, `unusedCount`, `unusedRange`, `byServer`, `definitionsSpanId`), also on page 2; `/servers` and `/skills` page at 25; each route's statements (SQL-recording adapter test) touch only tool / skill tables and the stored schema. | Routes and reads do not exist. | Named tests pass; the sweep holds. |
| T7 | `SessionSectionEndpointsTests.cs` (extend), `dash/server.tests/KyberDash.Domain.Tests` timeline tests | same filter as T4 | `/timeline` top level returns the first 25 nodes with `childCount` and `bounds`; `?parent=` (including a synthetic id, URL-encoded) returns that node's children paged at 25; the union of all levels equals today's `BuildTimeline` tree on a nested seed with more than 25 children at one level and synthetic nodes; no content read. | Route and read do not exist. | Named tests pass. |
| T8 | stored-fields tests in `dash/server.tests` (extend) | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~StoredFields"` | The inventory route returns names, columns, counts and missing keys with no row values and no content read; the rows route returns one table's page of 25; an unknown or non-catalog `table` is 404; rows are limited in SQL (SQL-recording adapter test). | Rows are returned unbounded with the inventory today. | Named tests pass. |
| T9 | `kyberApi` tests | `npm --prefix dash run test:related -- <changed files>` | Each section fetcher requests its route with the page and returns the validated payload; a malformed payload rejects with a validation error. | Fetchers do not exist. | Named tests pass; `typecheck` and `lint` clean. |
| T10 | `AgentSessionDashboard.test.tsx` and new section tests under `dash/web/src/components/session/`, `kyberApi` tests | `npm --prefix dash run test:related -- <changed files>` | Opening a session issues, before any response resolves, the section requests in page order (summary, charts, tools, servers, skills, timeline top level, requests, reconciliation, problems, stored-fields inventory) and no content-route or stored-fields-rows request; each section renders its own loading, error, empty and data states while the others are held back; the header and banners render from `/summary` alone; requests and reconciliation lists show the shared pager past 25; no component outside `lib/kyberApi.ts` calls `fetch`. | One monolithic raw `fetch`; no per-section states. | Named tests pass; `typecheck`, `lint`, `check:reachable` clean. |
| T11 | `dash/web/src/components/SessionSpendCharts.test.tsx`, `dash/web/src/components/SchemaCostRanking.test.tsx`, `dash/web/src/components/SkillsPanel.test.tsx`, `dash/web/src/components/SessionCostPanel.test.tsx` | `npm --prefix dash run test:related -- <changed files>` | Charts render from `/charts`; the tool ranking, skills and problems lists page at 25; bar scales, waste banner and "N of M tools never called" come from the summary or the section `aggregate` — with page 2 held back they already show whole-session values. | Components read the monolithic payload. | Named tests pass. |
| T12 | `SessionInspectorDrawer*.test.tsx`, `AgentSessionDashboard.test.tsx` drawer cases | `npm --prefix dash run test:related -- <changed files>` | Opening a turn requests `/turn/:index` and only then its content; a bucket drill-down uses that turn's `context`; a tool opens its definitions through `aggregate.definitionsSpanId`; a skill through `turns_listed`; a span from the loaded node or `/span/:id/attributes`. | The drawer looks rows up in the retired payload. | Named tests pass. |
| T13 | `TimelineView` tests, `StoredFieldsPanel.test.tsx` | `npm --prefix dash run test:related -- <changed files>` | Expanding a node with `childCount > 0` requests that node's first page (id URL-encoded); a level past 25 pages; the duration view scales to `bounds` and lists loaded nodes; a stored-fields table's rows load only when the viewer opens it, 25 at a time. | The tree expects nested `children`; the panel renders rows from the inventory. | Named tests pass. |
| T14 | `StoreInterfaceShapeTests.cs`, `AnalyticsReadPathTests.cs`, `ApplicationParityTests.cs`, `RelationalIngestTests.cs`, `SqliteRelationalStoreReadTests.cs`, `SettingsCleanDoctorReportTests.cs`, Presentation tests | `dotnet test dash/server/KyberDash.sln -c Release --filter "FullyQualifiedName~StoreInterfaceShapeTests"` plus each named class's filter | `/api/kyber/session/:id` is gone (404); `SessionDetailFigures`, `SessionDetailFigure`, `GetSessionDetail` and the unpaged `GetSessions` signature are gone from contracts and implementers; the `GetSessionDetail` read-path and parity tests are moved to the section routes (each assertion kept, re-pointed, never deleted or weakened); the report reads `SessionReportFigures` once and `SessionTurnFigures` once with unchanged figures. | Members, route and per-session report loop still exist. | Named tests pass; the solution builds. |
| T15 | `kyberApi.turn-route.test.ts`, `App.navigation.test.tsx`, `TurnDetail` tests | `npm --prefix dash run test:related -- <changed files>` | The session-payload fetch, its unused types and `fetchKyberSessionTurns`'s whole-payload fallback are gone; `TurnDetail` always requests `/turn/:index`. | The fallback still exists. | Named tests pass; `typecheck`, `lint`, `check:reachable` clean. |
| T16 | `dash/e2e/harness-collection.spec.ts`, `dash/e2e/deep-links.spec.ts`, `dash/e2e/otel-tray-dashboard.spec.ts`, `dash/e2e/otel-web-dashboard.spec.ts`, `dash/e2e/harness-collection-world.ts` | `npm --prefix dash run typecheck` (the specs compile against the new shapes); the specs themselves run in CI's `Dash harness-collection e2e` job, not in a worker gate (D18) | The consent-on prompt assertion reads `/session/:id/requests`; list readers accept `{ sessions, total, page, limit }`; specs that need one known session use `/session/:id/summary` or `q`; `data-testid` values used by the specs exist in the new section components. | Specs read the retired route. | Typecheck clean; CI's harness-collection job is the end-to-end verdict, reported by the council. |
| T17 | Documentation; verified by the docs gates | `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready` and `dotnet run --project src/KyberWeave.Cli -- docs drift .` | H1–H4 harvested; ADR 0092 registered in `docs/adr/README.md`; supersession notes on ADRs 0007, 0011, 0014, 0041; ADR 0091's cross-reference; the Closeout mapping filled. | Not applicable (documentation task). | Both gates report zero findings. |

## Tasks

### T1: Restore the consent-allowed prompt in the span inspector

- **Objective:** `/span/:id/attributes` carries the Droid captured prompt again, read from that one span's prompt part, so CI's harness-collection e2e passes the inspector assertion.
- **Capabilities:** C# read-path work in the KyberDash engine with read-log tests over the shared fake.
- **Difficulty:** easy
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** none
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: add `SpanPromptText(string spanId)` (that span's first non-empty `UserPrompt`, `UserMessage` or `ConversationHistory` part text — the parts `CapturedPromptText` chose — or `null`).
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: implement it as one query scoped to the span.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: `GetSpanAttributes` attaches the prompt under the same `FactoryKeys.PromptText` key for Droid records, as `AttachCapturedPrompt` did before the stall fix.
  - `InMemoryRelationalStore.cs` (logged through `NoteRead`), both `FaultingSessionReader` copies, `DelegatingReader`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T1 --red`
  - `kyber-weave review gates . --agent <worker> --task T1`
- **Acceptance:** T1 rows pass; the content sweep's expected reads list `SpanPromptText` for the span-attributes path only.

### T2: Sessions list server — page and filter before pricing

- **Objective:** `/sessions` returns one page of 100 roots filtered and searched on the server, each with its first 25 subagents and a total, priced only for the page; `/session/:id/subagents` pages a root's subagents at 25.
- **Capabilities:** C# contract, SQLite query and application read-path work, with read-log, SQL-recording and parity tests.
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T1
- **Files:**
  - `dash/server/KyberDash.Contracts/Repositories/IProjectionStore.cs`: `SessionHeaderPage`, `SessionChildHeaderPage`, `SessionChildPreview`, `SessionHeadersByKeys`, the `StoredSessionHeader` record (header columns only) and a page result record (`Items`, `Total`).
  - `dash/server/KyberDash.Contracts/Repositories/IRelationalStoreReader.cs`: keyed `SessionListFigures(IReadOnlyCollection<string> sessionKeys, IModelPricingLookup? pricing = null)`.
  - `dash/server/KyberDash.Data/Repositories/SqliteRelationalStore.Reader.cs`: the three header reads in SQL per D3 (`TextExpr` binding, `LIKE … ESCAPE '\'` with escaped search text, `hex(CAST(... AS BLOB))` read convention); the keyed figures read that never calls `Sessions(harnessId)` or `ListSessionProjections`.
  - `dash/server/KyberDash.Application/KyberBridgeEngine.cs`: `GetSessionPage(page, limit, harness, search)` and `GetSubagentPage(parentId, page)`; the shared paging envelope helper (extracted from the existing findings/quarantine envelope code) that later tasks reuse; drop the per-row `reader.GetSession`; keep today's row DTO (`ToSessionDto`) and the batched-fault fallback; remove `GetSessions(limit, harness)`.
  - `dash/server/KyberDash.Contracts/Services/IDashServices.cs`, `dash/server/KyberDash.Application/Services/DashboardQueryService.cs`, `dash/server/KyberDash.Api/Endpoints/DashEndpointMappings.cs` (`/sessions`, `/session` alias, `/session/{id}/subagents`, through `DashHttp.Pagination`).
  - `InMemoryRelationalStore.cs`, `FaultingSessionReader` copies, `DelegatingReader`, Moq setups in `AnalyticsErrorEnvelopeTests.cs`, `AnalyticsOperationsDetailTests.cs`, `MinimalApiEndpointTests.cs`, `PresentationBuilder.cs`.
  - Tests per the Test contract (the SQL-recording subclass lives in `dash/server.tests/KyberDash.Data.Tests/` and is reused by T6 and T8).
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T2 --red`
  - `kyber-weave review gates . --agent <worker> --task T2`
- **Acceptance:** T2 rows pass; the `/sessions` and `/session/{id}/subagents` routes issue no `ListSessionProjections`, no unkeyed `Sessions`, no per-row `GetSession` (other callers of `ListSessionProjections` — `EnsureProjected`, `CleanService` — are out of this task's scope); no schema change.

Failure modes this task must not allow:

- **Root membership changes** under the SQL root rule — a root becomes a child or vanishes. Mitigation: the adapter parity test on orphaned children, a parent outside the harness filter and a cycle.
- **A hidden unkeyed read** inside the keyed figures pass (the fake cannot see adapter internals). Mitigation: the SQL-recording adapter test asserts every statement is keyed.
- **Figure drift** between the page and today's list (absent-versus-zero token totals). Mitigation: parity on the four named seeds, assertions never weakened.
- **An N+1 subagent preview** (one child query per root). Mitigation: `SessionChildPreview` is one window-function statement; the SQL-recording test counts it.
- **Search injection or wildcard leakage** through `q`. Mitigation: `TextExpr` binding, `ESCAPE '\'`, and the literal-match test with `%`, `_`, `\` and a quote.

### T3: Sessions list UI, shared pager, and the pages that used the whole list

- **Objective:** The sessions list and subagent lists page through the server with one shared pager; `RunDetail` and `ContextDoctor` stop downloading the whole list.
- **Capabilities:** React and TypeScript UI work over TanStack Query and the shared API client, with Vitest and Testing Library tests.
- **Difficulty:** medium
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T2
- **Files:**
  - `dash/web/src/lib/kyberApi.ts`: `KyberPage<T>` type and validator for the `{ entries, data, total, page, limit }` envelope, the sessions-page validator for `{ sessions, total, page, limit }`, `fetchKyberSessionPage({ page, limit, harness, q })`, `fetchKyberSubagentPage(id, page)`; remove the unpaged `fetchKyberSessions` once its callers move.
  - New `dash/web/src/components/ui/Pager.tsx`: the one pager every list uses (previous, next and page numbers from `page`, `limit`, `total`; keyboard-operable; disabled while its page loads; nothing rendered when `total <= limit`).
  - `dash/web/src/components/ContextExplorer.tsx`: query key `['kyber-sessions', harness, q, page]`; harness from the shell's selector sent to the server; a search box sending `q`; pager; subagent rows from the root's first 25 with a pager fetching further pages.
  - `dash/web/src/pages/RunDetail.tsx`: resolve the execution's session through `fetchKyberSession` (`/session/:id/summary`); read `summary.turn_count`.
  - `dash/web/src/pages/ContextDoctor.tsx`: replace the `<select>` with a search combobox over `q` (paged); validate a selected or deep-linked id through `/summary`, not list membership; keep harness scoping.
  - `dash/web/src/App.tsx` and `dash/web/src/App.navigation.test.tsx` where they mock or call `fetchKyberSessions`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T3 --red`
  - `kyber-weave review gates . --agent <worker> --task T3`
- **Acceptance:** T3 rows pass; each query-backed view has loading, error, empty and data tests.

### T4: Section server part 1 — summary, charts, turn figures, run turns

- **Objective:** Widen `/summary` and `/turn/:index`, add `/charts`, add `SessionTurnFigures`, and move `ExtractRunTurns` onto it.
- **Capabilities:** C# contract, SQLite query and application read-path work, with parity and read-log tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T2
- **Files:**
  - `IRelationalStoreReader.cs`: `SessionTurnFigures(sessionKey, pricing)` (the per-turn `ReadTimeTurnSummary` list, content-free); widen `SessionSummaryFigure` with the header fields the API contract lists.
  - `SqliteRelationalStore.Reader.cs`: implement from turn records' token and cost columns (reuse `BuildContentFreeTurnSummaries` and `BuildReaderTurnSummary`), the summary widening, and the stored context turn for `/turn/:index`.
  - `KyberBridgeEngine.cs`: `GetSessionCharts` (today's `turns` via `ToTurnDto` and `context` via `BuildContextPayload`), widened `GetSessionSummary` (fields via `BuildNotes`, `BuildCostBasisMismatchObject`, `BuildSessionSummary`) and `GetTurnFigures`; `ExtractRunTurns` reads `SessionTurnFigures`; section 404s through `SessionHeadersByKeys`.
  - `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs`, `InMemoryRelationalStore.cs`, `FaultingSessionReader` copies, `DelegatingReader`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T4 --red`
  - `kyber-weave review gates . --agent <worker> --task T4`
- **Acceptance:** T4 rows pass; `/charts` and `/summary` parity with today's detail payload holds while `GetSessionDetail` still exists.

### T5: Section server part 2 — requests, reconciliation, problems

- **Objective:** Add the three paged routes; request rows carry prompt previews read for the page only.
- **Capabilities:** C# contract, SQLite query and application read-path work, with parity and read-log tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T4
- **Files:**
  - `IRelationalStoreReader.cs`: `SessionRequestFigures`, `SessionReconciliationFigures`, `SessionProblems` (paged: items plus total), `RequestPromptPreviews(sessionKey, requestSpanIds)`.
  - `SqliteRelationalStore.Reader.cs`: implement each over only its tables (`session_request` and turn ownership; turn reported/tokenized totals; ingest problems); prompt previews via the scoped part read, truncated as `ExtractReaderPromptFromParts` does.
  - `KyberBridgeEngine.cs`: `GetSessionRequests`, `GetSessionReconciliation`, `GetSessionProblems` with today's field mapping (`RequestRows`, `ReconciliationRows`) and the T2 envelope helper.
  - `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs`, `InMemoryRelationalStore.cs`, `FaultingSessionReader` copies, `DelegatingReader`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T5 --red`
  - `kyber-weave review gates . --agent <worker> --task T5`
- **Acceptance:** T5 rows pass.

### T6: Section server part 3 — tools, servers, skills

- **Objective:** Add the tools, servers and skills routes, paged at 25 with page-independent aggregates.
- **Capabilities:** C# contract, SQLite query and application read-path work, with parity, read-log and SQL-recording tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T5
- **Files:**
  - `IRelationalStoreReader.cs`: `SessionToolFigures(sessionKey, pricing)` (ranked rows including never-invoked, server rollups, the aggregate fields of the API contract) and `SessionSkillFigures(sessionKey)` — tool-call and tool-definition metadata, skill rows and the stored schema projection only.
  - `SqliteRelationalStore.Reader.cs`: implement them reusing `BuildReaderToolSummaries`, `BuildReaderServerSummaries`, `ResolveReaderUnusedRange`.
  - `KyberBridgeEngine.cs`: `GetSessionTools`, `GetSessionServers`, `GetSessionSkills`, mapping fields as `ToolRows`, `ServerRows`, `BuildSchemaPayload`, `BuildSkillsPayload` do; aggregates over all rows before paging.
  - `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs`, `InMemoryRelationalStore.cs`, `FaultingSessionReader` copies, `DelegatingReader`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T6 --red`
  - `kyber-weave review gates . --agent <worker> --task T6`
- **Acceptance:** T6 rows pass.

### T7: Timeline by level and page

- **Objective:** `/session/:id/timeline` answers one tree level at a time, 25 nodes per page, with `childCount` and `bounds` (D17).
- **Capabilities:** C# application and domain work on the timeline builder with a content-free scoped read and parity tests.
- **Difficulty:** hard
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T6
- **Files:**
  - `IRelationalStoreReader.cs`, `SqliteRelationalStore.Reader.cs`, `InMemoryRelationalStore.cs`, test-local readers: `SessionTimelineRecords(sessionKey)` — the content-free record metadata `BuildTimeline` reads, no more.
  - `KyberBridgeEngine.cs`: `GetSessionTimeline(sessionId, parentNodeId, page)` builds the tree with `CanonContextAnalysis.BuildTimeline`, locates the level (top level, or the node with that id), pages it, and maps nodes with today's fields minus `children`, plus `childCount`; `bounds` from the tree; remove `AttachCapturedPrompt` from the timeline path (D17).
  - `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs` (decode `parent`).
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T7 --red`
  - `kyber-weave review gates . --agent <worker> --task T7`
- **Acceptance:** T7 rows pass, including union-of-levels parity with today's tree.

Failure modes this task must not allow:

- **Synthetic node ids** (`(auxiliary)`, `(orphan:…)`) not round-tripping through the URL. Mitigation: the test requests a synthetic node's children through an encoded `parent`.
- **Lost or duplicated nodes across pages** (unstable sibling order). Mitigation: siblings keep `BuildTimeline`'s order; the union-of-levels parity test.
- **A content read sneaking in** through record hydration. Mitigation: `SessionTimelineRecords` returns metadata only; the sweep covers the route.
- **Cost on a very large session** (the tree is rebuilt per page). Mitigation: recorded in the register and measured in the PR notes (D17).

### T8: Stored fields — inventory at mount, rows on demand

- **Objective:** Split stored fields into a content-free inventory and a paged, allowlisted per-table rows route (D15).
- **Capabilities:** C# read-path and endpoint work with read-log and SQL-recording tests.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T7
- **Files:**
  - `IRelationalStoreReader.cs`, `SqliteRelationalStore.Reader.cs`, `InMemoryRelationalStore.cs`, test-local readers: `StoredFieldsInventory(sessionKey)` and `StoredFieldsRows(sessionKey, table, page, limit)` replacing `StoredFields`; `table` checked against the catalog tables the inventory lists.
  - `KyberBridgeEngine.cs`, `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs`: the inventory route and `/session/{sessionKey}/stored-fields/{table}`.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T8 --red`
  - `kyber-weave review gates . --agent <worker> --task T8`
- **Acceptance:** T8 rows pass.

### T9: Session view API client — fetchers, validators, query keys

- **Objective:** The shared client gains one fetcher and one response validator per section route, plus the section query-key helpers, so the layout task only wires components.
- **Capabilities:** TypeScript API-client work with Vitest tests.
- **Difficulty:** easy
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T3
- **Files:**
  - `dash/web/src/lib/kyberApi.ts`: fetchers and validators for summary, charts, tools, servers, skills, timeline level, requests, reconciliation, problems, stored-fields inventory and rows, and the widened turn route; query-key helpers `['kyber-session', id, section, page]`. Validators build on the T3 envelope validator.
  - `kyberApi` tests (one per fetcher: valid envelope parses; malformed envelope is an error, never a crash or a default).
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T9 --red`
  - `kyber-weave review gates . --agent <worker> --task T9`
- **Acceptance:** T9 rows pass; `typecheck` and `lint` clean.

### T10: Session view layout — sections issued in page order

- **Objective:** The session view renders from per-section queries issued at mount in page order; header, banners and overview render from `/summary` (header, subagent notice, reconciliation badge from `summary.reconciliation`, harness notes, requests banner with the pager and "N user requests" from the total, aux banner, overview strip); requests and reconciliation lists page; no raw `fetch`.
- **Capabilities:** React UI refactor over TanStack Query and the shared API client, with Vitest and Testing Library tests.
- **Difficulty:** hard
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T9
- **Files:**
  - `dash/web/src/components/AgentSessionDashboard.tsx`: `AgentSessionLoader` loads the summary through the client; `AgentSessionContent` becomes a layout of section components in today's order, each owning its query and four states. Keep every `data-testid` the e2e specs use.
  - `dash/web/src/components/session/SessionChartsSection.tsx`, `dash/web/src/components/session/SessionCostSection.tsx`, `dash/web/src/components/session/SessionProblemsSection.tsx`, `dash/web/src/components/session/SessionReconciliationSection.tsx`, `dash/web/src/components/session/SessionRequestsSection.tsx`, `dash/web/src/components/session/SessionServersSection.tsx`, `dash/web/src/components/session/SessionSkillsSection.tsx`, `dash/web/src/components/session/SessionSummarySection.tsx`, `dash/web/src/components/session/SessionTimelineSection.tsx`, `dash/web/src/components/session/SessionToolsSection.tsx`: one section component per section.
  - `dash/web/src/components/session/SectionError.tsx`, `dash/web/src/components/session/SectionLoading.tsx`, `dash/web/src/components/session/types.ts`, `dash/web/src/components/session/useSectionPage.ts`: the shared section shell, paging hook and types.
  - `dash/web/src/components/session/SessionChartsSection.test.tsx`, `dash/web/src/components/session/SessionCostSection.test.tsx`, `dash/web/src/components/session/SessionProblemsSection.test.tsx`, `dash/web/src/components/session/SessionReconciliationSection.test.tsx`, `dash/web/src/components/session/SessionRequestsSection.test.tsx`, `dash/web/src/components/session/SessionServersSection.test.tsx`, `dash/web/src/components/session/SessionSkillsSection.test.tsx`, `dash/web/src/components/session/SessionSummarySection.test.tsx`, `dash/web/src/components/session/SessionTimelineSection.test.tsx`, `dash/web/src/components/session/SessionToolsSection.test.tsx`: one section test per section.
  - Charts, tools, skills, cost and timeline sections render their existing components fed from the new queries with minimal adaptation; their paging and scale work is T11–T13.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T10 --red`
  - `kyber-weave review gates . --agent <worker> --task T10`
- **Acceptance:** T10 rows pass; no measured value renders as `0` or `—` while loading or failed.

Failure modes this task must not allow:

- **A section waits on another's query** (one slow route blanks the page). Mitigation: each section component owns exactly one query; the test holds every other route back and asserts each section's own data state still renders.
- **Request order drifts from page order** as components move. Mitigation: the test asserts the issued order before any response resolves.
- **A content route fires at mount** through a drawer default or a stored-fields rows prefetch. Mitigation: the test asserts no `/content`, `/turn/:index/content` or `/stored-fields/:table` request on open.
- **e2e locators break** when markup moves into section files. Mitigation: every `data-testid` the e2e specs use is kept; T16's typecheck plus CI's harness-collection job catch the rest.

### T11: Charts, tool ranking, skills and cost panel on their own data

- **Objective:** The four components consume their section routes; their lists page at 25; scales and totals come from server aggregates.
- **Capabilities:** React UI component work with Vitest tests.
- **Difficulty:** medium
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T10
- **Files:**
  - `dash/web/src/components/SessionSpendCharts.tsx` (from `/charts`), `dash/web/src/components/SchemaCostRanking.tsx` (paged rows; `aggregate` for scale, `byServer`, waste and counts), `dash/web/src/components/SkillsPanel.tsx` (paged; `aggregate.maxTokens`), `dash/web/src/components/SessionCostPanel.tsx` (cost blocks from the summary; problems list paged).
  - `dash/web/src/components/session/SessionChartsSection.tsx`, `dash/web/src/components/session/SessionToolsSection.tsx`, `dash/web/src/components/session/SessionSkillsSection.tsx`, `dash/web/src/components/session/SessionCostSection.tsx`, `dash/web/src/components/session/SessionProblemsSection.tsx`: the matching section components.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T11 --red`
  - `kyber-weave review gates . --agent <worker> --task T11`
- **Acceptance:** T11 rows pass.

### T12: Inspector drawer fetches what it opens

- **Objective:** Drawer openers resolve turns, tools, skills and spans by key (D9), with content requested only after opening.
- **Capabilities:** React UI work on the drawer and its openers with Vitest tests.
- **Difficulty:** medium
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T11
- **Files:**
  - `dash/web/src/components/SessionInspectorDrawer.tsx`; the drawer openers in `AgentSessionDashboard.tsx` or their extracted section components.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T12 --red`
  - `kyber-weave review gates . --agent <worker> --task T12`
- **Acceptance:** T12 rows pass.

### T13: Timeline tree, duration view and stored-fields panel by page

- **Objective:** The call tree and duration view load levels on expand and page them; the stored-fields panel loads a table's rows only when opened.
- **Capabilities:** React UI work on the timeline and stored-fields components with Vitest tests.
- **Difficulty:** medium
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T7, T8, T12
- **Files:**
  - `dash/web/src/components/analysis/TimelineView.tsx` and the timeline section component; `dash/web/src/components/StoredFieldsPanel.tsx` and its section component.
  - Tests per the Test contract.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T13 --red`
  - `kyber-weave review gates . --agent <worker> --task T13`
- **Acceptance:** T13 rows pass.

### T14: Retire the monolithic route and migrate the report (server)

- **Objective:** Remove `/api/kyber/session/:id`, `GetSessionDetail` and `SessionDetailFigures`; move their tests to the section routes; the report reads batched (D13).
- **Capabilities:** C# contract retirement, application migration and test migration.
- **Difficulty:** medium
- **Technology:** csharp
- **Session:** `dash-sections-server`
- **Depends on:** T8, T13
- **Files:**
  - `IRelationalStoreReader.cs`, `SqliteRelationalStore.Reader.cs`, `InMemoryRelationalStore.cs`, test-local readers: remove `SessionDetailFigures` / `SessionDetailFigure`; add `SessionReportFigures(keys, pricing)`.
  - `KyberBridgeEngine.cs`: remove `GetSessionDetail`, `ReadSessionDetailFigures` and helpers left without callers; `GetReport` per D13.
  - `IDashServices.cs`, `DashboardQueryService.cs`, `DashEndpointMappings.cs`: remove the route and members.
  - Tests: `StoreInterfaceShapeTests.cs`, `AnalyticsReadPathTests.cs`, `ApplicationParityTests.cs`, `RelationalIngestTests.cs`, `SqliteRelationalStoreReadTests.cs`, `SettingsCleanDoctorReportTests.cs`, Presentation tests — every `GetSessionDetail` assertion re-pointed to the section route that now serves that field; none deleted or weakened.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T14 --red`
  - `kyber-weave review gates . --agent <worker> --task T14`
- **Acceptance:** T14 rows pass; the solution builds.

### T15: Retire the client's whole-payload paths

- **Objective:** Remove the session-payload fetch, its unused types and the whole-payload turn fallback from the web client.
- **Capabilities:** TypeScript client cleanup with Vitest tests.
- **Difficulty:** easy
- **Technology:** react
- **Session:** `dash-sections-ui`
- **Depends on:** T14
- **Files:**
  - `dash/web/src/lib/kyberApi.ts`, `dash/web/src/pages/TurnDetail.tsx`, `dash/web/src/lib/kyberApi.turn-route.test.ts`, `dash/web/src/App.navigation.test.tsx`.
- **Commands:**
  - `kyber-weave review gates . --agent <worker> --task T15 --red`
  - `kyber-weave review gates . --agent <worker> --task T15`
- **Acceptance:** T15 rows pass.

### T16: End-to-end specs on the new routes

- **Objective:** The Playwright specs read the new routes and shapes, so CI's harness-collection job passes.
- **Capabilities:** Playwright end-to-end spec maintenance in TypeScript.
- **Difficulty:** easy
- **Technology:** typescript
- **Session:** `dash-sections-ui`
- **Depends on:** T15
- **Files:**
  - `dash/e2e/harness-collection.spec.ts` (the consent-on prompt reads `/session/:id/requests`; the two `/sessions` reads accept the new fields), `dash/e2e/deep-links.spec.ts`, `dash/e2e/otel-tray-dashboard.spec.ts`, `dash/e2e/otel-web-dashboard.spec.ts`, `dash/e2e/harness-collection-world.ts`.
- **Commands:**
  - `npm --prefix dash run typecheck`
- **Acceptance:** Typecheck clean; no spec reads `/session/:id` or relies on the whole sessions list for a known id. The specs run in CI's `Dash harness-collection e2e` job (D18); the council reports that job's verdict.

### T17: docs-dev closeout — register, data-loading section, runbook, ADR 0092

- **Objective:** Harvest H1–H4 into canonical documentation and record ADR 0092.
- **Capabilities:** Documentation in Markdown under the repository's governed-docs rules.
- **Difficulty:** easy
- **Session:** `docs`
- **Depends on:** T16
- **Files:**
  - `docs/dash/architecture.md`: read-model register and Backend REST API contract rows for every new, widened and retired route; the web dashboard data-loading section (H2).
  - `docs/dash/runbook.md`: §11 update (H3).
  - New `docs/adr/0092-dashboard-views-load-by-section-and-page.md`, registered in `docs/adr/README.md`; supersession notes in ADRs 0007, 0011, 0014, 0041; cross-reference from ADR 0091 (H4).
  - This plan's Closeout mapping.
- **Commands:**
  - `dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready`
  - `dotnet run --project src/KyberWeave.Cli -- docs drift .`
- **Acceptance:** Both gates report zero findings; every Design harvest row has a Closeout mapping row.

## Dependency graph

```
Server chain (dash-sections-server)            UI chain (dash-sections-ui)

T1 span prompt
 |
T2 sessions list server ---------------------> T3 sessions list UI + shared Pager
 |                                               |
T4 summary, charts, turn, run turns              |
 |                                               |
T5 requests, reconciliation, problems            |
 |                                               |
T6 tools, servers, skills                       T9 session view API client (after T3)
 |                                               |
T7 timeline by level                            T10 session view layout
 |                                               |
T8 stored fields inventory/rows                 T11 charts, ranking, skills, cost
 |                                               |
 |                                              T12 drawer by key
 |                                               |
 +---------------------------------------------> T13 timeline + stored-fields UI (after T7, T8, T12)
 |                                               |
T14 retire route, report <-----------------------+
 |
 +-------------------------------------------> T15 retire client paths
                                                 |
                                                T16 e2e specs
                                                 |
                                                T17 docs-dev closeout, ADR 0092
```

The server chain is serial because every server task edits `IRelationalStoreReader.cs`,
`SqliteRelationalStore.Reader.cs`, `InMemoryRelationalStore.cs`, `KyberBridgeEngine.cs` and
`DashEndpointMappings.cs`. The UI chain touches only `dash/web` and `dash/e2e`; every `dash/web` edit
stays on it, so no two concurrent tasks edit the same file. T3 runs beside T4–T8, and T9–T12 run beside T7–T8: their tests mock the routes, whose shapes the API contract fixes (D19). T13 renders the timeline and stored-fields routes, so it waits for T7 and T8.

## Risks

| Risk | Mitigation |
|---|---|
| A section payload drifts from today's field names or values when its mapping moves out of `GetSessionDetail`. | T4–T7 parity rows compare each section with today's detail payload while it still exists; T14 re-points, never deletes, the old assertions. |
| A page-scoped figure is shown as the session's (scales, waste totals, counts). | D8 aggregates; the T11 test holds page 2 back and asserts whole-session values. |
| Ten section calls per view multiply server work: each starts with `EnsureProjected`, a 404 check, and several read turn token columns. | `EnsureProjected` is single-flight and returns at once when clean or busy; the 404 check uses header columns only (D8); section reads touch only their tables (SQL-recording tests). The remaining overlap is measured on a large session in the PR notes. |
| The chart payload is large on a long session (per-turn tool-definition maps). | D7: measured in the PR notes; downsampling is the named follow-up. |
| The sessions list shifts by a row under live capture between pages. | Accepted (D5), stated in the runbook. |
| The timeline rebuilds the session's metadata tree per page. | D17 and T7's failure modes; recorded in the register, measured in the PR notes. |
| Playwright cannot run in a worker gate. | D18: specs are typechecked in the gate and run in CI; Vitest pins request order and laziness (T10, T12, T13). |
| CI stays red between T1 and T16 (the requests prompt assertion). | The PR is a draft; T16 restores green; the council reports the CI verdict. |

## Out-of-scope boundaries

- `/api/kyber/timeline` (global timeline) is unchanged; no UI calls it. PR note: retire or page it separately.
- `RunDetail`'s run turn list renders every turn of a run unpaged (now fed by `SessionTurnFigures` per execution). PR note: page it at 25 in a follow-up plan.
- Server-side downsampling of the chart series (D7). PR note.
- PR note: CI's `Dash harness-collection e2e` verdict on the final pushed commit (D18), with the measured section and chart payload sizes on a large session (D7, D8).
- `ListSessionProjections` callers off the sessions route (`EnsureProjected`, `CleanService`) are unchanged.
- No canon schema change, no `SchemaVersion` bump, no new index.
- No change to the content routes' contract or to `TurnContentParts`.
- Standards: no new rule ids. Whether to add a paging rule to the React standard is the owner's call, raised in the PR description.

## Verification gates

- Each developer task ends with its own `task-gate` run (the Commands above) over the touched
  projects; no dev agent runs the full suite.
- D18 — Playwright runs only in CI (`Dash harness-collection e2e`); T16's gate is the typecheck.
- The end-of-run review council reviews the change with the `code-review` skill, measured against
  base `3dc143af2`, admitted to the full-suite gates with `--council`, and reports the CI verdict of
  the pushed branch.
- Documentation changes re-run `docs validate . --merge-ready` and `docs drift .` to zero findings.

## Review

The end-of-run council (`code-review` skill, base `3dc143af2`) reviews the whole change. Scale is
`extends-component` over `KyberDash`; the Design harvest table names the durable destinations.

## docs-dev closeout

- Execute the Design harvest rows: register and REST API table (H1), data-loading section (H2),
  runbook §11 (H3), ADR 0092 with its registration, supersession notes and 0091 cross-reference
  (H4, mandatory: the decision constrains every future dashboard view, had rejected alternatives,
  and would be expensive to revisit).
- Fill the Closeout mapping with one row per Design harvest row and per decision, with waivers only
  for D10, D11 and D12 (run provenance, no surviving invariant).
- Archive this plan and remove its task file in the same commit.
