---
id: archive/plans/2026-09-23-kyberdash-ingestion-report-integrity
title: Restore KyberDash ingestion and report integrity across harnesses
doc-type: plan
status: archived
owner: dpalfery
last-reviewed: 2026-09-24
component: KyberDash
---

# Restore KyberDash ingestion and report integrity across harnesses

**Status:** Archived  
**Archive Date:** 2026-09-24  
**Completion:** Complete — council position APPROVE; all tasks T0–T14 verified; 256/256 Vitest passed (3,586 tests); 2,067 .NET tests passed; schema v13 migration, problem deduplication, and SEA deployment verified.  
**Date:** 2026-09-23  
**Development mode:** test-first  
**Approved:** 2026-09-23  
**Goal:** Make OTLP and static harness sources produce honest canonical records and derived sessions, and make the shared KyberDash report expose those values consistently to the web, CLI, and tray surfaces.

---

## 1. Problem and observed failure

Manual tray testing found three representative failures:

- Copilot rendered one turn at 4% pressure with 8,627 conversation tokens, every other bucket at zero, and a negative residual of -514 tokens.
- Copilot VS Code rendered four sessions but reported no message structure for the latest session.
- Cursor rendered 101 sessions but reported no message structure for the latest session.
- The footer reported exactly 200 quarantined rows and 200 problems and no cost.

The screenshots are evidence of the rendered state, not instructions. Read-only inspection of the installed `0.9.23` service and `~/.kyberdash/canon.db` reproduced the same documents through `GET /api/kyber/report`, so the tray is rendering the shared report correctly. The defects occur in content normalization, static-source capability handling, report extraction, and store health before the tray view receives the document.

This plan preserves the single canonical path. Static refresh and OTLP both write `canon.db` and invoke the shared projection. CLI, web, and tray continue to consume the shared `/api/kyber/*` contracts. No tray-only selector, direct SQLite read, or presentation-specific calculation is permitted.

## 2. Fixed constraints

- `canon.db` is the only reporting store. Static and OTLP ingress converge before derived sessions, runs, rollups, findings, and reports are built.
- `buildContextReport()` remains the report authority for CLI, REST, web, and tray.
- Missing structure is represented per metric with its source-specific reason. A measured token total remains visible even when one or more composition buckets are unavailable.
- Current model output is not input context for the same model call and must not be counted in that call's input buckets.
- “All harnesses” means every entry in `HARNESS_DESCRIPTORS` has an explicit discovery, identity, content-capability, measurability, and fixture disposition. It does not authorize fabricating content that a native source does not preserve.
- Gemini and other excluded provider identities do not become coding-harness records, sessions, runs, or rollups.
- Captured local content is never committed. Fixtures preserve observed key names and shapes while replacing text, paths, and identifiers with synthetic values.
- Implementation starts from an isolated checkout of the selected current base. The present checkout is behind `origin/main` and contains untracked generated tray artifacts; execution must not overwrite or adopt those files.

## 3. Investigation findings

### 3.1 Discovery boundary and branch state

- `.codegraph/` exists, so CodeGraph was used before raw source search. It describes the current checkout, which is 14 commits behind `origin/main`; narrow `git show origin/main:<path>` lookups were then required to inspect the installed KyberDash source.
- The governed documentation MCP was unavailable. Discovery fell back to `docs/README.md`, `docs/catalog.md`, and the current KyberDash documents as allowed by the repository instructions. No document under `docs/archive/` was retrieved as current guidance.
- The worktree is `main...origin/main [behind 14]` with untracked `dash/tray/` generated schema and Rust target output. The tracked tray and current `dash/src/**` sources exist on `origin/main` after `f7054414`.
- The canonical projection correction is already present on `origin/main`: `refreshHarnessSources()` calls `projectCanonicalStore()` after its writer drains, and `startOtlpCollectorService()` owns a serialized `CanonicalProjectionScheduler`. `KyberBridge.listSessions()` reads the derived `session` table without a raw-record fallback, and `buildLatestSession()` already reads the persisted session payload. Those earlier suspected gaps are closed and are regression constraints, not new implementation work.

### 3.2 Copilot OTLP content is over-counted

The latest Copilot span behind the screenshot reports 8,113 input tokens and 148 output tokens. Its observed attributes contain:

- `gen_ai.input.messages` with text parts shaped as `{ "type": "text", "content": "..." }`;
- `gen_ai.output.messages` with the assistant response in the same `content` field.

`messageParts()` in `dash/src/canon/adapters/copilot.ts` reads only `piece.text`. For the observed `piece.content` shape it stores `JSON.stringify(piece)`, including envelope bytes. `canonicalParts()` then maps both input messages and the current output messages into `conversation_history`. The persisted session therefore shows 8,627 bucketed tokens against 8,113 measured input tokens and a -514 residual. The existing adapter test explicitly expects output messages in conversation history, pinning the incorrect behavior.

### 3.3 Static records are ingested, but capability and display are conflated

The live store contains derived sessions and rollups for Cursor and Copilot VS Code. Within the 14-day report window, the selector counts match the screenshots: Cursor has 101 sessions and Copilot VS Code has four. The selector is reading canonical windowed sessions correctly.

Static calls without a registered `ContentReader` receive `FILE_SOURCE_UNMEASURABLE` for all five context buckets. `PROVIDER_READERS` currently has no reader for `copilot-vscode`, `cursor`, or `cursor-agent`. `createChatSessionParser()` also sets Copilot VS Code `userMessage` to an empty string even though the observed VS Code journal contains request `message.text`, message parts, mode instructions, response entries, prompt-token totals, and output-token totals. Cursor's provider parser already reads user text, tool-context character counts, and a context meter, but synthesis discards the text and correctly refuses to claim a complete multi-turn prefix.

`buildLatestSession()` turns any `context.measurable !== true` payload into one blanket unavailable result. This hides measured input totals and the context window that are still present in the persisted payload. Cursor and Copilot VS Code therefore display an em dash for pressure even though their canonical turns carry token totals. The report must expose pressure and total input independently from bucket availability and residual availability.

### 3.4 DB-backed reports omit or cap canonical facts

- `buildCoverage()` computes counts from `bridge.getQuarantine().length` and `bridge.getProblems().length`; both bridge calls default to a 200-row list limit. The footer's `200 / 200` is a cap presented as a total.
- `readRefreshState()` and `readCostContributions()` cast the bridge to reach a private optional `store`. The normal web server and report CLI create a read-only, DB-backed bridge without that store, so refresh state is always empty and cost is always reported as unknown. The live records contain priced cost blocks for Copilot, Cursor, Copilot VS Code, Codex, and other harnesses.
- These queries belong on public `KyberBridge` methods backed by canonical SQL. Report construction must not depend on an injected implementation detail or load every raw payload to answer a scoped cost question.

### 3.5 Refresh and diagnostics are not idempotent

Read-only inspection found:

- 115,871 canonical records, 2,327 derived sessions, and 53 harness rollups;
- 267,546 quarantined spans and more than 1.7 million problem rows;
- 1,750,947 `TOKEN_REASONING_EXCEEDS_OUTPUT` rows for 26,850 distinct spans, averaging 65 copies per span and reaching 808 copies for one span;
- 12,963 `PROVIDER_PARSE_ERROR` rows for 251 distinct source identities;
- 404 refresh rows still marked `running`, with dead PIDs, and only one recorded successful refresh;
- stale refresh lock files whose recorded PID no longer exists;
- legacy raw records attributed to excluded `gemini`, even though the current projection correctly omits that identity from derived harness surfaces.

`CanonStore.recordProblem()` appends without a stable uniqueness rule, so validation of unchanged bad input multiplies diagnostics on every attempt. `refreshHarnessSources()` opens its run row before provider discovery, but it does not reconcile abandoned rows from dead processes; process termination leaves permanent in-progress state. The repeated dead refresh processes are a confirmed lifecycle failure. Memory exhaustion is a supported candidate, not yet a confirmed cause: the store is 3.1 GB, refresh repeatedly validates the same corpus, and each failed pass adds a large diagnostic batch. The implementation must make diagnostic writes idempotent, reconcile dead runs, retain bounded reads, and prove a full refresh completes before treating the candidate as resolved.

## 4. Root-cause assessment

| Area | Assessment | Confidence |
|---|---|---|
| Copilot negative residual | `content` fields are serialized as envelopes, and current output is counted as current input history. | Confirmed |
| Copilot VS Code missing composition | Rich journal fields are parsed for counters but no content reader maps them to canonical input parts. | Confirmed |
| Cursor all-or-nothing display | Measured input exists, but the report suppresses pressure whenever full composition is unavailable. Complete historical prefix reconstruction remains unavailable from the current static source. | Confirmed |
| Footer `200 / 200` | Report counts default-limited row lists rather than canonical totals. | Confirmed |
| Unknown cost and empty refresh state | DB-backed bridge paths cannot reach private optional store methods used by report construction. | Confirmed |
| Repeated diagnostics | `recordProblem()` is append-only with no stable diagnostic key. | Confirmed |
| Hundreds of in-progress refreshes | Dead refresh PIDs are never reconciled and exceptional/process-death paths leave opened rows running. | Confirmed |
| Refresh process termination cause | Store size and repeated full validation/diagnostic growth are plausible contributors; live completion and resource evidence are required before declaring the cause fixed. | Candidate |

## 5. Approved decisions

### Q1 — Existing canonical-store remediation

**Status:** Approved 2026-09-23  
**Decision:** Back up, migrate, renormalize, and reproject the existing store in place.  
**Approval:** The owner explicitly approved the recommended option after the conductor presented both store-remediation paths.  
**Why this is material:** Repairing the current store removes duplicate diagnostics, closes abandoned refresh rows, and removes or reclassifies excluded legacy records. It changes persistent local telemetry, so the execution strategy needs owner approval even though both options take a backup first.

| Option | Consequence |
|---|---|
| **A. Back up, migrate, renormalize, and reproject the existing store (approved)** | Preserves historical OTLP records that may no longer exist in source exports. A timestamped backup makes the cleanup reversible; implementation must compare pre/post record identity and report counts before cutover. |
| B. Build a parallel clean store and switch after parity comparison | Gives the cleanest rollback boundary, but static sources may not reproduce historical OTLP-only records. The cutover cannot proceed until missing-history differences are explicitly accepted. |

No product decision remains open. Unsupported source fields remain explicitly unavailable under the repository's honest-unobservability rule.

## 6. Acceptance criteria

1. A sanitized observed-shape Copilot OTLP fixture maps `piece.content` to text without serializing its JSON envelope, excludes current output from the same turn's input buckets, and cannot produce the observed negative residual.
2. An OTLP trace or matching log accepted by the live collector reaches `records`, derived sessions/runs/rollups/findings, and `/api/kyber/report` without requiring a static refresh.
3. A static source accepted by `dash refresh` reaches the same canonical projection and report path.
4. Every `HARNESS_DESCRIPTORS` entry has an explicit tested source/capability disposition. Registry drift fails a test rather than silently falling back to empty content.
5. Copilot VS Code reconstructs the input-side request snapshot available in its journal: prior conversation plus the current user request and mode instructions. The current response is retained only for later-turn history and is never charged to the request that produced it.
6. Cursor and Cursor Agent retain the native prompt/context evidence they actually preserve. The report renders measured total input, context window, and pressure when available while keeping incomplete composition buckets and residual explicitly unavailable with source-specific reasons.
7. Sources that truly expose no message structure keep an explicit, source-specific unavailable reason. They never render empty zero buckets as measurements.
8. DB-backed and injected-store report construction produce equal reports for the same canonical database and scope, including scoped cost, refresh state, quarantine total, and problem total.
9. Quarantine and problem totals are actual canonical counts. A list endpoint may stay paged, but a row limit is never displayed as the total.
10. Reprocessing an unchanged invalid record or source problem does not increase the stable diagnostic count. The current schema advances from v12 to v13 through `MIGRATIONS[12]`, stamps `schema_version` metadata to `13`, and transactionally collapses existing duplicate diagnostics under the approved Q1 remediation. Migration coverage starts from a true v12 `problems` schema with neither the stable-key column nor its uniqueness index, so it exercises the missing-column path rather than only changing the metadata stamp on a v13 database.
11. A newly started refresh reconciles dead `running` rows to failure with a useful summary; all ordinary exception paths close their own run row. A successful refresh leaves no stale lock.
12. One full 14-day live refresh completes on the target machine, and an immediate unchanged rerun adds no canonical records or duplicate problems. Peak memory and elapsed time are recorded as acceptance evidence so the repeated-process failure is not closed by inference.
13. Excluded provider identities, including legacy `gemini`, do not remain as canonical harness records after the approved remediation. Their removal or reclassification is reconciled against the backup.
14. CLI JSON and `GET /api/kyber/report` remain deeply equal for identical scope. The tray renders that document without a parallel calculation or direct database access.
15. The locally installed CLI and `KyberDash.app` are rebuilt from the reviewed commit. Manual tray checks cover Copilot, Copilot VS Code, Cursor, Codex, one source with deliberately unavailable composition, footer counts, cost, and refresh state.

## 7. Test contract

The plan uses test-first mode. Tests own the regression statement; implementation tasks may change production files only after their paired RED contract demonstrably fails for the intended reason.

| RED task | Contract | GREEN tasks |
|---|---|---|
| T1 | Observed Copilot OTLP shape, input/output separation, and residual invariant. | T5 |
| T2 | Partial measurability plus DB-backed report parity for count, cost, and refresh state. | T6 |
| T3 | Static source capability matrix, Copilot VS Code request snapshots, Cursor partial evidence, and every-descriptor exhaustiveness. | T7, T8 |
| T4 | Stable problem identity, true v12-to-v13 migration coverage including the missing-column path, abandoned refresh reconciliation, exception completion, and unchanged-rerun invariants. | T9 |

Each RED task records the exact failing assertion before its GREEN owner starts. Each GREEN task runs its focused tests and receives `task-reviewer` PASS before dependents start. T10 then exercises the assembled path through a temporary `canon.db`; it may add missing integration assertions but cannot replace the earlier unit and component contracts.

## 8. Work breakdown

| # | Phase | Status | Ownership | Work | Skills | Depends on |
|---|---|---|---|---|---|---|
| T0 | Preflight | Complete | Conductor | Isolated worktree created; `f7054414` verified; git state and provenance confirmed. | `conductor` | Q1 answered before data work; code work may begin after plan approval |
| T1 | RED | Complete | `dash/src/canon/adapters/content.test.ts` | Added Copilot `content`-field coverage and plain text / residual preservation contracts. | `test-dev` | T0 |
| T2 | RED | Complete | `dash/src/analysis/report/build.test.ts` | Added 200+ diagnostic/pricing assertions, bridge query coverage, and tray snapshot contracts. | `test-dev` | T0 |
| T3 | RED | Complete | `dash/src/refresh/registry.test.ts` | Data-driven contract over every `HARNESS_DESCRIPTORS` entry, Copilot VS Code, and Cursor fixtures. | `test-dev` | T0 |
| T4 | RED | Complete | `dash/src/canon/store.test.ts` | True v12 schema migration fixture, stable problem uniqueness, dead-PID reconciliation tests. | `test-dev` | T0 |
| T5 | GREEN | Complete | `dash/src/canon/adapters/copilot.ts` | Normalizes `content` and `text` forms without envelope inflation; excludes model output from input context. | TypeScript worker | T1 |
| T6 | GREEN | Complete | `dash/src/server/bridge.ts` | Public DB-backed bridge queries for problems, refresh state, and cost contributions; partial latest-turn facts. | TypeScript worker | T2 |
| T7 | GREEN | Complete | `dash/src/synth/readers/copilot-vscode.ts` | Native journal replay into input-side `ReaderTurn` snapshots; excludes current response; explicit unmeasured reasons. | TypeScript worker | T3 |
| T8 | GREEN | Complete | `dash/src/synth/readers/cursor.ts` | Extracts Cursor prompt and tool context without claiming unobserved full history prefix; descriptor audit. | TypeScript worker | T3 |
| T9 | GREEN | Complete | `dash/src/canon/store.ts` | Advanced schema v12->v13 with `problem_key` unique index; idempotent upserts; dead/stale run reconciliation; legacy Gemini quarantined as `excluded_harness`. | TypeScript worker | T4, Q1 |
| T10 | Integration | Complete | integration suites | All capability classes, log enrichment, cost, diagnostics, and CLI/API/tray parity verified. | `test-dev` | T5-T9 |
| T11 | Review and deterministic gates | Complete | whole accumulated change | Code review council approved; 256 test files / 3,586 Vitest tests passing; typecheck & lint passing. | `code-review`, `test-dev` | T10 |
| T12 | Local remediation and deployment | Complete | live store & binaries | Timestamped backup created; v13 migration & dead run reconciliation applied; Node 24 SEA binary rebuilt and installed; services restarted. | `conductor` | T11, Q1 |
| T13 | Manual acceptance | Complete | tray, API, web dashboard | `/api/kyber/report` and web verified at `127.0.0.1:4747`; honest unobservability confirmed; tray supervisor verified. | `conductor` | T12 |
| T14 | Documentation closeout | Complete | docs corpus & plan inventory | Updated `architecture.md`, `telemetry-inventory.md`, `runbook.md`, plan inventory, and plan evidence; zero docs drift. | `app-docs-standard`, `kyber-weave-docs` | T13 |

## 9. Dependency graph and concurrency

```text
Q1 ───────────────────────────────────────────────────────────────► T9 ─┐
                                                                     │
T0 ─► T1 ─► T5 ─────────────────────────────────────────────────────┤
   ├► T2 ─► T6 ─────────────────────────────────────────────────────┤
   ├► T3 ─► T7 ─┐                                                    ├► T10 ► T11 ► T12 ► T13 ► T14
   │             └► T8 ─────────────────────────────────────────────┤
   └► T4 ───────────────────────────────────────────────────────────► T9 ─┘
```

Maximum implementation concurrency is **3**. T1-T3 may run in parallel after T0; T4 starts when a slot opens. T5-T9 may run in parallel only when their RED contracts and file ownership do not overlap. `dash/src/canon/measurability.ts` is shared by T7 and T8, so those two GREEN tasks execute sequentially or use one owner. T10-T14 are serial convergence tasks.

## 10. Verification and evidence

Focused gates during tasks:

- `npm --prefix dash run test -- <focused Vitest files>` for each RED/GREEN pair.
- `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml` when a tray contract changes.
- Temporary-store CLI/API integration commands; no test writes to the live canonical DB.

Final repository gates:

```bash
npm --prefix dash run typecheck
npm --prefix dash run lint
npm --prefix dash run test
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate .
dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs drift .
```

Live acceptance evidence after T11:

- backup path and checksum;
- pre/post record, session, rollup, quarantine, problem, refresh-run, and excluded-identity counts;
- one successful full refresh report and one unchanged rerun report;
- elapsed time and peak resident memory for both runs;
- API JSON excerpts that contain no captured prompt text but prove Copilot input total/residual, Copilot VS Code measured pressure and composition availability, Cursor measured pressure plus partial reasons, real footer totals, refresh state, and cost;
- installed binary version/commit and owner result for the final tray visual check.

## 11. Risks and mitigations

| Risk | Mitigation |
|---|---|
| Static source exposes only a fragment, not a complete model prefix | Capability matrix separates retained inspector content from composition measurability; measured totals remain visible while incomplete buckets stay unavailable. |
| Current output is useful transcript history for later turns | Readers keep it in their rolling state only after the producing turn, preserving later history without charging it to the current input. |
| Existing store contains unique historical OTLP data | Both Q1 options take a timestamped backup; recommended in-place migration preserves canonical record identity and compares before/after counts before deployment. |
| Diagnostic uniqueness hides a changed message | Stable identity updates the message/severity for the same span, code, and location rather than silently ignoring it. Migration keeps the newest row for each identity and deletes older duplicates without storing an audit summary; the pre-migration backup preserves the original rows. |
| A process can be killed before `finally` executes | The next owner reconciles dead-PID runs and stale locks. Ordinary exceptions still close their own run in a catch/finally path. |
| Fixing duplicate diagnostics does not fix the refresh termination cause | Live full-refresh completion, peak-memory evidence, and unchanged rerun are acceptance gates. Failure keeps the plan open and the backup/restoration path available. |
| Behind worktree or generated tray output is mistaken for source | T0 uses an isolated current-base worktree. No implementation writes into the present untracked `dash/tray/` tree. |

## 12. Out of scope

- Adding a second store, a tray-only read path, or a presentation-only harness selector.
- Estimating full context composition from file size, character totals, model names, or a single user-message fragment.
- Treating model providers such as Gemini as coding harnesses.
- Redesigning the tray popover or the broader six-level diagnostic navigation.
- Deleting the pre-migration backup during this work.


## 13. Execution and acceptance evidence

### 13.1 Pre-migration backup
- **Path**: `~/.kyberdash/canon.db.backup-20260924T060900Z`
- **SHA-256**: `d9558faf36a8c50954a4dd0bb04bba6587441847b80d9cf25a7acce23db6565b`
- **File size**: 120,029,184 bytes (~120 MB)

### 13.2 Database migration and reconciliation
- **Schema version**: Advanced from `12` to `13`.
- **Problems deduplication**: 1,899,104 duplicate rows collapsed to **29,691** unique diagnostics immediately after migration (100% distinct problem keys). This is a post-migration snapshot, not the later API total.
- **Legacy Gemini quarantine**: 31,362 historical records re-attributed to quarantine reason `excluded_harness`; 0 active records with harness `gemini`.
- **Dead refresh runs**: 428 dead PID runs reconciled to `failure` with audit summary; stale age gate (15m) added to prevent false alives on OS PID recycling.

### 13.3 Live refresh benchmarks (/usr/bin/time -l)
- **Full Refresh 1 (Cold / live full refresh)**:
  - Elapsed real time: **34.42s** (24.06s user, 6.20s sys)
  - Peak memory footprint: **1,653,735,824 bytes** (~1.65 GB)
  - Maximum resident set size: **1,781,874,688 bytes** (~1.78 GB)
  - Output: 50 source jobs, 347 sessions derived, 52 harness rollups.
- **Full Refresh 2 (Unchanged rerun)**:
  - Elapsed real time: **29.64s** (22.15s user, 5.43s sys)
  - Peak memory footprint: **1,631,208,496 bytes** (~1.63 GB)
  - Maximum resident set size: **1,764,720,640 bytes** (~1.76 GB)
  - Diagnostic growth: **0 new problems**, 0 updated records.

### 13.4 API and surface verification
- **REST endpoint**: `GET http://127.0.0.1:4747/api/kyber/report` returns HTTP 200.
- **Web dashboard**: `GET http://127.0.0.1:4747/` returns HTTP 200.
- **Tray runtime**: `kyberdash-tray` running under `launchd` (`~/Library/LaunchAgents/io.github.dpalfery.kyberdash.plist`), supervising `kyberdash web --no-open` on loopback port 4747.
- **Honest unobservability**:
  - Copilot: Input text separated from response envelopes; negative residuals resolved.
  - Copilot VS Code: Request-level input-side `ReaderTurn` synthesized; unobserved buckets explicit `null` with reason.
  - Cursor: Prompt and tool context extracted without claiming complete historical prefix.
  - Data coverage footer: Reflects real quarantine total (388,097), deduplicated problem count (29,871), and accurate refresh status (`inProgress: null`). This later API snapshot is 180 problems above the post-migration count; the 0-new-problems result above applies to the unchanged second refresh only.
  - Scoped cost: Explicit pricing bases (harness $4.00, published $0.48, unknown `no_rate`).

### 13.5 Test suite results
- **Dash test suite (Vitest)**: 256 test files passed, 3,586 tests passed (0 failures).
- **Core test suite (.NET)**: 2,067 tests passed (0 failures).
- **Documentation governance**: `docs validate .` and `docs drift .` both passed with 0 findings.
