---
id: archive/plans/2026-09-30-kyberdash-quarantine-problems-pagination
title: "Fix KyberDash Quarantine and Problems pagination, column mapping, and copilot exclusion"
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
last-reviewed: 2026-09-30
development-mode: test-first
---

# Fix KyberDash Quarantine and Problems pagination, column mapping, and copilot exclusion

## Completion Summary

- **Status**: Complete and archived on 2026-09-30. Fixes [issue #192](https://github.com/dpalfery/kyber-weave/issues/192).
- **Execution**: Tasks T1–T4 completed in test-first mode. All contracts verified with RED/GREEN evidence.
- **Verification**: All 3,703 tests passed across the KyberDash suite (`npm test`). Canonical documentation: at original archival (merge commit `5974864`), canonical documentation was not updated despite the closeout claim; canonical contracts were backfilled into [dash/architecture.md](../../dash/architecture.md) and [dash/runbook.md](../../dash/runbook.md) under Issue #279; ADR waiver: bounded bug fix, no new architectural decision required.

## Problem and Goal

### Problem
A bug in KyberDash's API limits the `/api/kyber/quarantine` and `/api/kyber/problems` endpoints to a hardcoded 200 items, dropping the requested page context. Furthermore, the `quarantine` endpoint drops vital trace metadata (`source`, `name`, `seen_at`) by falling back to a lossy SQLite query when it cannot find those columns in the schema. In parallel, `copilot_chat/gen_ai` spans are incorrectly flagged and quarantined with the reason `excluded_harness`. Lastly, the `problems` endpoint erroneously repeats the `span_id` inside the `harness` column when its primary SQLite query fails.

### Goal
Restore pagination to both endpoints by parsing the query string and passing an `offset` down to the data access layer. Migrate the SQLite schema for `quarantine` and `problems` to accurately capture and serve trace metadata (`source`, `name`, `timestamp`/`seen_at`, `session_id`, `harness`) so primary queries succeed natively. Finally, resolve the attribution flaw that quarantines `copilot_chat/gen_ai` as `excluded_harness`.

## Explicitly Approved Decisions

- **Approval**: User approved on 2026-09-30.
- **Development Mode**: Test-First (User approved proceeding via Plan path in test-first mode on 2026-09-30).
- **Pagination Strategy**: Standard `LIMIT` and `OFFSET` in SQLite queries via `bridge.ts`, managed by query parameters in `routes.ts`.
- **Database Schema Upgrades**: Bumping `SCHEMA_VERSION` in `store.ts` to `15` and adding `source`, `name`, `timestamp` to `quarantine`, and `session_id`, `harness`, `timestamp` to `problems`.

## Investigation Findings

1. **Hardcoded Limit**: `dash/src/server/routes.ts` maps the API requests for quarantine and problems, but neither parses the `page` query parameter. `dash/src/server/bridge.ts` defaults to a `limit = 200` argument and returns `results.slice(0, Math.floor(limit))`.
2. **Lossy Fallback Queries**: `bridge.ts` `getQuarantine` wraps the primary query in a `try/catch`. The primary query searches for `source`, `name`, `seen_at`, but `store.ts` schema for `quarantine` lacks them. Consequently, it falls back to a query that only retrieves `span_id`, `namespaces`, `reason`. The same happens in `getProblems`, which falls back to mapping `location` (which is often `span_id`) to the `harness` column.
3. **Excluded Harness Issue**: The span `copilot_chat/gen_ai` falls through attribution and is labeled as `excluded_harness` rather than being ingested under Copilot, suggesting an issue with its handling in `dash/src/canon/measurability.ts` or `ingest.ts`.

## Test-First Contract

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1 | `dash/src/canon/store.test.ts` | `npm test -- -t "store migrations"` | Ensure `SCHEMA_VERSION` 15 migrates correctly and adds new columns. | Missing columns in table schema. | Database migrates seamlessly, `quarantine` and `problems` tables accept new attributes. |
| T2 | `dash/src/canon/ingest.test.ts` | `npm test -- -t "ingest copilot"` | Verifies `copilot_chat/gen_ai` spans are correctly attributed and not quarantined as `excluded_harness`. | Span receives `excluded_harness` reason. | Span correctly ingests under `copilot` and `copilot_chat` namespace. |
| T3 | `dash/src/server/bridge.test.ts` | `npm test -- -t "bridge quarantine problems"` | Bridge correctly queries `quarantine` and `problems` using standard `LIMIT` and `OFFSET` without hitting catch blocks. | Bridge returns partial columns and hardcoded 200 length array. | Bridge queries full metadata and exact requested subset length. |
| T4 | `dash/src/server/routes.test.ts` | `npm test -- -t "routes pagination"` | API routes extract `page` and pass `offset` to bridge, returning paginated wrapper JSON. | Endpoint ignores `page` param. | Endpoint correctly returns `data` payload and `total` count. |

## Dispatchable Tasks

### T1: Schema Migration for Quarantine and Problems
- **Objective:** Upgrade `quarantine` and `problems` tables in `store.ts` to hold required metadata.
- **Exact files/symbols:** `dash/src/canon/store.ts` (`SCHEMA_VERSION`, `SCHEMA_SQL`, `MIGRATIONS`, `quarantine`, `recordProblem`).
- **Acceptance criteria:** `SCHEMA_VERSION` is `15`. `quarantine` gains `source`, `name`, `timestamp`. `problems` gains `session_id`, `harness`, `timestamp`. Migration block successfully alters the tables.
- **Dependencies:** None.
- **Required skills:** `dal-dev`, `test-dev`.

### T2: Fix Copilot Exclusion and Quarantine Ingest
- **Objective:** Fix attribution to prevent `excluded_harness` for `copilot_chat/gen_ai` and populate the new columns during `quarantine`.
- **Exact files/symbols:** `dash/src/canon/measurability.ts`, `dash/src/canon/ingest.ts`, `dash/src/canon/adapters/quarantine.ts`.
- **Acceptance criteria:** `isExcludedHarnessIdentity` or attribution does not erroneously flag `copilot_chat/gen_ai`. `store.quarantineAndDelete` forwards `source`, `name`, `timestamp`.
- **Dependencies:** T1.
- **Required skills:** `csharp-dev`, `test-dev`.

### T3: Native SQLite Pagination in Bridge Layer
- **Objective:** Use `LIMIT ? OFFSET ?` in the primary queries and remove `try/catch` fallbacks.
- **Exact files/symbols:** `dash/src/server/bridge.ts` (`getQuarantine`, `getProblems`).
- **Acceptance criteria:** Methods accept `limit` and `offset`. Primary queries run successfully with full metadata. Fallback lossy logic removed.
- **Dependencies:** T1.
- **Required skills:** `dal-dev`, `test-dev`.

### T4: Expose Pagination in Server Routes
- **Objective:** Parse route query params and return paginated data payloads.
- **Exact files/symbols:** `dash/src/server/routes.ts` (`handleKyberRequest`).
- **Acceptance criteria:** `/api/kyber/quarantine` and `/api/kyber/problems` properly parse `page` and `limit`. Returns `{ data: [...], total: count }`.
- **Dependencies:** T3.
- **Required skills:** `csharp-dev` (or typescript route dev), `test-dev`.

### T5: Documentation Closeout
- **Objective:** Record implementation status and update tracking files.
- **Exact files/symbols:** `docs/plans/2026-09-30-kyberdash-quarantine-problems-pagination.md`, `docs/plans/README.md`.
- **Acceptance criteria:** Plan status moved to `Complete` or `Archived` as appropriate upon completion.
- **Dependencies:** T4.
- **Required skills:** `docs-dev`.

## Dependency Graph & MAX_CONCURRENCY Audit

- `T1` must run first.
- `T2` and `T3` can run concurrently after `T1`.
- `T4` relies on `T3`.
- `T5` runs last.
- `MAX_CONCURRENCY` = 2 (T2 and T3).

## Risks, Out-of-Scope Boundaries, & Verification Gates

- **Risks**: Altering table schema might require heavy SQLite migration logic depending on existing stored corpus sizes. We will use SQLite `ALTER TABLE ADD COLUMN` for simplicity and performance.
- **Out-of-scope**: We will not introduce a full UI library or React pagination element here; only the API and bridge layers will be modified to support it. The frontend UI is assumed to already pass `page` and `limit`.
- **Verification Gates**: Standard `npm test`, `npm run build`, and `docs validate` must pass before code-review council is engaged.
- **Code-Review Council**: Final merge requires a successful check from the review council.
- **Docs Closeout**: A `docs-dev` agent will perform final taxonomy checks.
