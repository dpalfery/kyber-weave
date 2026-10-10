---
id: plans/2026-10-02-issue-227-kilocode-zero-records
title: "KyberDash: KiloCode sources produce no records (#227)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-03
development-mode: test-first
---

# KyberDash: KiloCode sources produce no records (#227)

**Status: Complete, archived 2026-10-02.** Approved via the autonomous "Hal approves" approve-and-execute gate on 2026-10-02. Tasks T1–T5 complete. Decisions D1–D6 recorded below and harvested into [dash/architecture.md](../../dash/architecture.md) (parser contract invalidation, checkpoint recordCount reconciliation, flat token validation and markdown part recognition) and [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) (KiloCode shared runtime SQLite ingestion, contract version 2). No new ADR — D1–D6 recorded in this plan. Archived per KW-DOC-LIFECYCLE-003.
This plan addresses GitHub issue [#227](https://github.com/dpalfery/kyber-weave/issues/227): KiloCode shared-runtime SQLite sources produce 0 records in `canon.db` despite 70 units discovered in `source_checkpoint`.

**Development mode:** `test-first`. Every implementation task defines failing automated tests over synthetic fixture databases before touching production logic.

---

## 1. Problem and Scope

### 1.1 Context and Evidence

Issue [#227](https://github.com/dpalfery/kyber-weave/issues/227) follows [#189](https://github.com/dpalfery/kyber-weave/issues/189) (Condition-1 live audit evidence gathered 2026-09-30):
- **Live source:** `~/.local/share/kilo/kilo.db` (104 MB, mtime 2026-09-28, well within the 14-day history window).
- **Checkpoints:** 70 `kilo-shared-runtime` units in `source_checkpoint` (69 zero-record checkpoints, 1 nonzero checkpoint claiming 3 records).
- **Records:** Zero rows in `records` (`SELECT COUNT(*) FROM records WHERE source LIKE '%kilo%'` = 0).
- **Verdict from #189:** The gap is **not** a refresh window effect (unlike Codex 458/545 pre-window or Copilot 0 in-window files). KiloCode has active in-window database sessions, but a parser/adapter defect prevents any records from landing in `canon.db`.

### 1.2 Two Investigation Questions

1. **Why 69/70 checkpoint units yield 0 records:**
   - In `dash/src/providers/sqlite-session-parser.ts`, session turns are extracted by querying `message` and `part` tables for each session tree.
   - For assistant messages, `buildAssistantCall` (`dash/src/providers/session-message.ts`) extracts token counts from `data.tokens?.input` and `data.usage?.input_tokens`.
   - In KiloCode's `kilo.db`, assistant `message.data` stores tokens under flat keys: `tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`. Because `buildAssistantCall` does not read flat keys, all token counters evaluate to 0.
   - Furthermore, KiloCode emits assistant text responses as `part.data` with `type: 'markdown'` rather than `type: 'text'`. In `buildAssistantCall`, `hasTextOutput` checks only `p.type === 'text'`, and `hasAnySubstantiveParts` excludes `'markdown'`.
   - Consequently, for turns with flat tokens and markdown parts, `allZero && (data.cost ?? 0) === 0 && !hasActivity` evaluates to `true`, and `buildAssistantCall` returns `null` for every assistant turn.
   - When all message turns yield 0 calls, `sqlite-session-parser.ts` falls back to `tryQuerySessionTokens(db, sessionId)`. In 69 of the 70 sessions, the `session` table row has null or 0 tokens, yielding 0 calls. Thus 69 units produce 0 records.

2. **Where the 3 claimed records went:**
   - In `dash/src/refresh/orchestrator.ts:318`, `recordCount` on a `SourceCheckpoint` is computed cumulatively as `(previous?.recordCount ?? 0) + created`.
   - In an earlier refresh pass (or prior to a `records` purge/rebuild/clean), 1 unit had 3 records created.
   - `source_checkpoint` and `records` are independent tables. When records are removed (e.g. via quarantine, rebuild, or test wipe), `source_checkpoint.record_count` is never decremented or reconciled against `records`.
   - Crucially, in `recordsForUncoveredCommit` (`dash/src/refresh/orchestrator.ts:478-488`):
     ```ts
     const reusable = checkpointIsReusable(previous, request)
     const gaps = uncoveredIntervals(previous, requested, request)
     return merged.filter((record) => {
       if (store.get(record.spanId) !== undefined) return false
       if (!reusable || previous === undefined) return true
       if (gaps.length === 0) return false
       ...
     })
     ```
     When `checkpointIsReusable` is `true` and the window has no uncovered intervals (`gaps.length === 0`), `recordsForUncoveredCommit` returns `[]` even if `store.get(record.spanId) === undefined`!
   - In addition, `previousFingerprintsFor` skips units with `recordCount > 0` as `unchanged` if the checkpoint is reusable.
   - Because `parserContractVersion` was left at `'1'`, the orchestrator considered the unit covered and skipped re-inserting the missing records on subsequent refreshes.

### 1.3 Goals

1. **Parser recovery in `session-message.ts`:**
   - Read flat token fallbacks (`tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`) when nested `tokens` and `usage` are absent.
   - Validate flat values using `finiteOrUndefined` so non-finite numbers or strings do not leak into calculations (addressing PR #264 CodeRabbit review findings).
   - Recognize `type: 'markdown'` in `hasTextOutput` and `hasAnySubstantiveParts`.
2. **User message parsing in `sqlite-session-parser.ts`:**
   - Recognize `type: 'markdown'` parts for user message prompts.
   - Normalize model resolution when `data.model` is an object (`{ id, providerID }`) or string.
3. **Parser contract invalidation:**
   - Define `KILO_PARSER_CONTRACT_VERSION = '2'` in `dash/src/refresh/registry.ts` for `kilo-shared-runtime` and `kilo-vscode-legacy`.
   - Ensure existing checkpoints with version `'1'` are recognized as stale, forcing re-parse and re-commit of records on refresh.
4. **Automated test coverage:**
   - Unit tests for flat token extraction and finite validation in `session-message.test.ts`.
   - Synthetic SQLite fixture tests for `kilo.db` in `sqlite-session-parser.test.ts` (or `kilo-code.test.ts`).
   - End-to-end refresh integration test verifying that `kilo-shared-runtime` populates `records` and upgrades stale checkpoints.

---

## 2. Approved Decisions and Decision Ledger

### Approved Decisions

| Id | Decision | Provenance |
|---|---|---|
| **D1** | Read flat token counters (`tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`) wrapped in `finiteOrUndefined` validation as fallback in `buildAssistantCall`, preserving nested `tokens` and `usage` precedence. | Grounded in PR #264 review threads and `dash/architecture.md` honest-measurement rules. Prevents corrupted cost arithmetic from non-finite values or strings while extracting valid counters from KiloCode SQLite message payloads. Approved 2026-10-02. |
| **D2** | Recognize `type: 'markdown'` parts as substantive text output in `hasTextOutput` and `hasAnySubstantiveParts` in `session-message.ts`, and support markdown parts for user prompts in `sqlite-session-parser.ts`. | Grounded in KiloCode emitter schema (`part.data` with `type: 'markdown'`). Prevents assistant turns with markdown output and zero tokens/costs from being dropped as silent no-op calls. Approved 2026-10-02. |
| **D3** | Bump `parserContractVersion` to `'2'` for affected Kilo harnesses (`kilo-shared-runtime`, `kilo-vscode-legacy`) and reconcile `recordCount` on non-reusable checkpoints directly from `merged.length` in `refreshHarnessSources`. | Grounded in ADR 0016 and orchestrator checkpoint contract. Invalidation forces refresh to re-parse and commit previously dropped records without deleting metadata; reconciliation eliminates phantom record counts. Approved 2026-10-02. |
| **D4** | Define dedicated `KILO_PARSER_CONTRACT_VERSION = '2'` in `dash/src/refresh/registry.ts` applied specifically to `kilo-shared-runtime` and `kilo-vscode-legacy`. | Grounded in ADR 0016 and performance isolation. Confines checkpoint invalidation to affected Kilo harnesses without triggering re-parsing on unrelated healthy harnesses (Cursor, Pi, Codex, Copilot). Approved 2026-10-02. |
| **D5** | Normalize model resolution in `sqlite-session-parser.ts` to accept both string identifiers and structured objects (`{ id, providerID }`). | Grounded in KiloCode/OpenCode SQLite records schema. Prevents `[object Object]` or `unknown` models in canonical records. Approved 2026-10-02. |
| **D6** | Strictly require synthetic temporary fixture databases in tests and enforce `sqlite3 -readonly` with `.timeout 5000` for all live store queries. | Grounded in repository live-DB hygiene rules and issue #227 intake instructions. Guarantees live databases are never mutated and tests are reproducible. Approved 2026-10-02. |

### Decision Details (Question, Answer, Rationale, Source)

#### D1 — Flat token fallback with finite validation
- **Question:** When nested `tokens` and `usage` structures are absent in assistant `message.data` (e.g. in KiloCode SQLite stores), should flat token properties (`tokens_input`, `tokens_output`, `tokens_reasoning`, `tokens_cache_read`, `tokens_cache_write`) be extracted, and how should they be validated?
- **Options Considered:**
  - *(a)* Read flat `tokens_input` / `tokens_output` / `tokens_reasoning` wrapped in `finiteOrUndefined` validation while preserving nested tokens/usage precedence.
  - *(b)* Typecast directly without validation.
  - *(c)* Only read session-level tokens via `tryQuerySessionTokens`.
- **Answer / Decision:** *(a)* Read flat tokens with `finiteOrUndefined` validation, preserving nested precedence.
- **Rationale:** Grounded in PR #264 review threads and honest-measurement rules (`dash/architecture.md`). Unvalidated values could corrupt cost arithmetic with `NaN`, `Infinity`, or string concatenations (`"100"`). Nested precedence must remain intact for providers that supply structured usage.
- **Source / Provenance:** PR #264 review threads; `dash/architecture.md` honest-measurement rules; KiloCode `kilo.db` schema inspection.

#### D2 — Substantive part types in assistant and user turns
- **Question:** Should `type: 'markdown'` parts in assistant responses and user messages be recognized as substantive activity?
- **Options Considered:**
  - *(a)* Add `p.type === 'markdown'` to `hasTextOutput` and `hasAnySubstantiveParts` in `session-message.ts`, and support markdown parts for user prompts in `sqlite-session-parser.ts`.
  - *(b)* Require explicit `'text'` type conversion in SQL query or parser caller.
- **Answer / Decision:** *(a)* Add `'markdown'` to substantive parts in `session-message.ts` and `sqlite-session-parser.ts`.
- **Rationale:** KiloCode emitter schema stores assistant markdown text responses in `part.data` with `type: 'markdown'`. When tokens and costs are zero, `hasTextOutput` and `hasAnySubstantiveParts` previously evaluated to false because only `type === 'text'` was recognized, dropping non-empty assistant turns as empty no-ops.
- **Source / Provenance:** KiloCode shared-runtime schema inspection (`kilo.db`), PR #227 live audit evidence.

#### D3 — Parser contract invalidation and checkpoint reconciliation
- **Question:** How should previously parsed zero-record checkpoints and stale records from earlier runs be recovered when the parser is fixed?
- **Options Considered:**
  - *(a)* Bump `parserContractVersion` to `'2'` for Kilo harnesses, and reconcile checkpoint `recordCount` on non-reusable checkpoints to actual merged record count (`merged.length`).
  - *(b)* Rely on file mtime change.
  - *(c)* Wipe `source_checkpoint` table globally.
- **Answer / Decision:** *(a)* Bump `parserContractVersion` to `'2'` for Kilo harnesses, and reconcile checkpoint `recordCount` directly to `merged.length` when checkpoints are non-reusable.
- **Rationale:** Per ADR 0016 and orchestrator design, a bumped parser contract invalidates reusable checkpoints without wiping historical metadata, causing refresh to re-parse and commit previously dropped records. Reconciling `recordCount` on non-reusable checkpoints directly from `merged.length` rather than `(previous?.recordCount ?? 0) + created` eliminates phantom record claims when records were previously dropped or wiped.
- **Source / Provenance:** ADR 0016; `dash/src/refresh/orchestrator.ts`; issue #227 evidence (1 unit claimed 3 records with 0 in `records`).

#### D4 — Scope of parser contract version bump
- **Question:** Should `DEFAULT_PARSER_CONTRACT_VERSION` be bumped globally, or should a dedicated `KILO_PARSER_CONTRACT_VERSION` be used?
- **Options Considered:**
  - *(a)* Dedicated `KILO_PARSER_CONTRACT_VERSION = '2'` in `dash/src/refresh/registry.ts applied to `kilo-shared-runtime` and `kilo-vscode-legacy`.
  - *(b)* Bump `DEFAULT_PARSER_CONTRACT_VERSION` globally for all harnesses.
- **Answer / Decision:** *(a)* Dedicated `KILO_PARSER_CONTRACT_VERSION = '2'` in `dash/src/refresh/registry.ts`.
- **Rationale:** Isolates checkpoint invalidation to affected Kilo harnesses, avoiding unnecessary re-parsing and performance overhead across unrelated healthy harnesses (e.g., Cursor, Pi, Codex, Copilot).
- **Source / Provenance:** `dash/src/refresh/registry.ts`, ADR 0016.

#### D5 — Model identifier resolution
- **Question:** How should model identifiers be extracted from session message data when the model may be stored as an object `{ id, providerID }` or as a string?
- **Options Considered:**
  - *(a)* Normalize both `modelID` and `model` (handling `{ id, providerID }` object or string).
  - *(b)* Only read `modelID`.
- **Answer / Decision:** *(a)* Normalize both `modelID` and `model`.
- **Rationale:** KiloCode and OpenCode SQLite records can store model as a structured object (`{ id: string, providerID?: string }`) or string; normalizing avoids `[object Object]` or `unknown` in canonical records.
- **Source / Provenance:** `sqlite-session-parser.ts`, KiloCode database inspection.

#### D6 — Live store hygiene and test fixture policy
- **Question:** Should tests use copies of the live database or synthetic temporary fixture databases?
- **Options Considered:**
  - *(a)* Synthetic temporary fixture databases in tests; all live queries use `sqlite3 -readonly` with `.timeout 5000`.
  - *(b)* Copy live database for tests.
- **Answer / Decision:** *(a)* Synthetic temporary fixture databases only.
- **Rationale:** User instructions and repository hygiene rules mandate `sqlite3 -readonly` for live stores and synthetic fixtures for all tests to protect live databases from corruption and ensure portable, reproducible tests.
- **Source / Provenance:** Issue #227 requirements; repository testing guidelines.

### Decision Ledger (Draft Resolution)

| Question Id | Decision | Options Considered | Recommendation | Resolution / Rationale | Status |
|---|---|---|---|---|---|
| **Q1** | Flat token fallback in `buildAssistantCall` | (a) Read flat `tokens_input` / `tokens_output` / `tokens_reasoning` wrapped in `finiteOrUndefined`; (b) Typecast directly without validation; (c) Only read session-level tokens | **(a) Read flat tokens with `finiteOrUndefined`** | Grounded in PR #264 review threads and honest-measurement rules (`dash/architecture.md`). Unvalidated values could corrupt cost arithmetic; nested precedence must remain intact. | **ANSWERED (a) → D1** |
| **Q2** | Substantive part types | (a) Add `p.type === 'markdown'` to `hasTextOutput` and `hasAnySubstantiveParts`; (b) Require explicit `'text'` type conversion in SQL | **(a) Add `'markdown'` to substantive parts** | Grounded in KiloCode emitter schema where assistant markdown is stored in `part.data` with `type: 'markdown'`. | **ANSWERED (a) → D2** |
| **Q3** | Handling missing 3 records and stale checkpoints | (a) Bump `parserContractVersion` to `'2'` for Kilo harnesses; (b) Rely on mtime change; (c) Wipe `source_checkpoint` table | **(a) Bump `parserContractVersion` to `'2'`** | ADR 0016 / orchestrator contract: a bumped parser contract invalidates reusable checkpoints without wiping historical metadata, causing refresh to re-parse and commit previously dropped records. | **ANSWERED (a) → D3** |
| **Q4** | Contract version placement | (a) Dedicated `KILO_PARSER_CONTRACT_VERSION = '2'` in `dash/src/refresh/registry.ts`; (b) Bump `DEFAULT_PARSER_CONTRACT_VERSION` globally | **(a) Dedicated `KILO_PARSER_CONTRACT_VERSION = '2'`** | Isolates checkpoint invalidation to affected Kilo harnesses, avoiding unnecessary re-parsing across unrelated healthy harnesses (e.g. Cursor, Pi, Devin). | **ANSWERED (a) → D4** |
| **Q5** | Model identifier resolution | (a) Normalize `modelID` string and `model` (string or `{ id, providerID }`); (b) Only read `modelID` | **(a) Normalize both `modelID` and `model`** | KiloCode/OpenCode SQLite records can store model as an object or string; normalizing avoids `[object Object]` or `unknown` in canonical records. | **ANSWERED (a) → D5** |
| **Q6** | Live DB hygiene | (a) Synthetic temporary fixture databases in tests; (b) Copy live DB | **(a) Synthetic fixture DBs only** | User instructions and repo hygiene rules mandate `sqlite3 -readonly` for live stores and synthetic fixtures for all tests. | **ANSWERED (a) → D6** |

---

## 3. Test-First Implementation Tasks

### Task T1: Unit Tests for Flat Token Validation & Markdown Parts (session-message)
- **Failing test first:** In `dash/src/providers/session-message.test.ts`, author tests:
  - Flat `tokens_input`, `tokens_output`, `tokens_reasoning` extraction.
  - Rejection of string numbers (`"100"`) or `NaN` via `finiteOrUndefined`.
  - Recognition of `type: 'markdown'` parts as substantive activity.
- **Implementation:** Update `dash/src/providers/session-message.ts`.

### Task T2: SQLite Session Parser KiloCode Fixture Tests
- **Failing test first:** In `dash/src/providers/kilo-code.test.ts` (or `dash/src/providers/sqlite-session-parser.test.ts`):
  - Author a test against a synthetic `kilo.db` holding session, message, and part tables with flat token counters and markdown parts.
  - Verify that `createSessionParser` produces `ParsedProviderCall` instances with expected input/output tokens, cost, and model.
- **Implementation:** Update `dash/src/providers/sqlite-session-parser.ts` to support markdown user parts and model object unpacking.

### Task T3: Parser Contract Version Bump & Checkpoint Invalidation
- **Failing test first:** In `dash/src/refresh/registry.test.ts`:
  - Assert that descriptor for `kilo-shared-runtime` and `kilo-vscode-legacy` returns `parserContractVersion: '2'`.
- **Implementation:** In `dash/src/refresh/registry.ts`, add `KILO_PARSER_CONTRACT_VERSION = '2'` and apply to Kilo harnesses.

### Task T4: End-to-End Refresh Integration Test for KiloCode
- **Failing test first:** In `dash/src/refresh/refresh.integration.test.ts`:
  - Create a test where a synthetic `kilo.db` is refreshed into `CanonStore`.
  - Verify that records land in the `records` table under source `codeburn/kilo-shared-runtime` with non-zero record count.
  - Verify that a pre-existing checkpoint with `parserContractVersion: '1'` and 0 records is invalidated and upgraded to `'2'` with correct records.
- **Implementation:** Verify end-to-end integration and run full refresh suite.

### Task T5: Verification, Docs Validation, and Closeout
- Run declared test gates:
  - `npm --prefix dash run typecheck`
  - `npm --prefix dash run lint`
  - `npm --prefix dash run test` (disclosing known-failing `migration.test.ts` and `cursor.test.ts` date-rot)
  - `/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release`
  - `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .`
  - `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .`
- Archive plan per KW-DOC-LIFECYCLE-003 before opening PR.

---

## 4. Execution Closeout

All tasks T1–T5 completed test-first and verified clean:
- **T1:** Unit tests in `dash/src/providers/session-message.test.ts` verify flat token validation (`tokens_input`, `tokens_output`, etc.), string/NaN rejection via `finiteOrUndefined`, and `type: 'markdown'` substantive activity.
- **T2:** Fixture tests in `dash/src/providers/kilo-code.test.ts` verify that SQLite sessions with flat tokens and markdown parts correctly parse into `ParsedProviderCall` instances.
- **T3:** Registry descriptor tests in `dash/src/refresh/registry.test.ts` verify `parserContractVersion === '2'` for `kilo-shared-runtime` and `kilo-vscode-legacy`.
- **T4:** Integration pipeline test in `dash/src/refresh/pipeline.test.ts` verifies that `kilo-shared-runtime` re-reads synthetic `kilo.db` sessions, invalidates legacy contract version `'1'` checkpoints, populates canonical records with full token counters, and reconciles `recordCount` in `source_checkpoint`.
- **T5:** Full verification gate suite passed:
  - `npm --prefix dash run typecheck` passed (0 errors)
  - `npm --prefix dash run lint` passed (0 errors)
  - `npm --prefix dash run test` passed (278/278 test files, 4,294 tests passed)
  - `npm --prefix dash run check:reachable` passed
  - `/Users/hal/.dotnet/dotnet build KyberWeave.sln -c Release` passed (0 warnings, 0 errors)
  - `docs validate .` passed (0 findings)
  - `docs drift .` passed (0 findings)
- **Durable decisions harvested:** Decisions D1–D6 harvested into [dash/architecture.md](../../dash/architecture.md) (parser contract invalidation, checkpoint recordCount reconciliation, flat token validation, and markdown part recognition), [dash/telemetry-inventory.md](../../dash/telemetry-inventory.md) (KiloCode shared runtime SQLite ingestion, contract version 2), and indexed in [docs/plans/README.md](../plans/README.md).
- Archived per KW-DOC-LIFECYCLE-003 to `docs/archive/plans/2026-10-02-issue-227-kilocode-zero-records.md`.
