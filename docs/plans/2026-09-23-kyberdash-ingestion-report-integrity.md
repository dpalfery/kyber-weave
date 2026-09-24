---
id: plans/2026-09-23-kyberdash-ingestion-report-integrity
title: Restore KyberDash ingestion and report integrity across harnesses
doc-type: plan
status: current
owner: dpalfery
last-reviewed: 2026-09-23
component: KyberDash
---

# Restore KyberDash ingestion and report integrity across harnesses

**Status:** Ready  
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

| # | Phase | Ownership | Work | Skills | Depends on |
|---|---|---|---|---|---|
| T0 | Preflight | Conductor | Create an isolated worktree from the approved current base containing `f7054414` and later KyberDash fixes; preserve the present behind branch and untracked `dash/tray/` outputs. Confirm installed `0.9.23` provenance against the selected base. | `conductor` | Q1 answered before data work; code work may begin after plan approval |
| T1 | RED | `dash/src/canon/adapters/content.test.ts`, sanitized fixture under `dash/src/canon/adapters/__fixtures__/` | Add observed Copilot `content`-field coverage; assert current output is absent from input parts, plain text is preserved, and bucketed input does not exceed reported input for the fixture. Update the existing test that currently requires output in conversation history. | `test-dev` | T0 |
| T2 | RED | `dash/src/analysis/report/build.test.ts`, `dash/src/server/kyber-bridge.test.ts`, `dash/src/cli/report-api-parity.test.ts`, tray UI report fixture/tests | Seed more than 200 diagnostics and priced records in a temporary canonical DB. Assert real totals, refresh state, scoped cost, independent measured pressure with unavailable buckets, DB/store parity, CLI/API parity, and faithful tray rendering. | `test-dev` | T0 |
| T3 | RED | `dash/src/refresh/registry.test.ts`, `dash/src/synth/provider.test.ts`, `dash/src/refresh/orchestrator.test.ts`, sanitized source fixtures | Add a data-driven contract over every `HARNESS_DESCRIPTORS` entry. Add Copilot VS Code journal and Cursor database fixtures proving the native evidence each reader may retain, input-side turn boundaries, and explicit unavailable reasons for incomplete fields. | `test-dev` | T0 |
| T4 | RED | `dash/src/canon/store.test.ts`, `dash/src/refresh/orchestrator.test.ts`, refresh CLI/integration tests | Prove a repeated identical diagnostic has one stable row, an injected refresh exception closes the current run as failure, a later run reconciles a dead PID row, and an unchanged rerun has zero diagnostic growth. Build the duplicate/stale migration fixture with the true v12 `problems` schema: the stable-key column and uniqueness index are absent, rather than a v13 database whose metadata alone was changed to 12. Assert that opening it exercises the missing-column path and stamps `schema_version` to `13`. | `test-dev` | T0 |
| T5 | GREEN | `dash/src/canon/adapters/copilot.ts` | Teach `messageParts()` the observed `content` and `text` forms without envelope inflation. Separate input-message normalization from response retention so `canonicalParts()` exposes only input-resident content to context analysis. Preserve tool-result bucketing and reported aggregate-token rules. | none; TypeScript worker selected by conductor | T1 |
| T6 | GREEN | `dash/src/server/bridge.ts`, `dash/src/analysis/report/build.ts`, report types/renderers only where the contract requires | Add public, DB-backed bridge queries for diagnostic totals, refresh state, and scoped cost contributions. Remove private-store casts. Extract partial latest-turn facts so total input, context window, and pressure remain measured independently from bucket and residual availability. Keep one `ContextReport` for every surface. | none; TypeScript worker selected by conductor | T2 |
| T7 | GREEN | new Copilot VS Code reader under `dash/src/synth/readers/`, `dash/src/providers/copilot.ts`, `dash/src/synth/provider.ts`, `dash/src/canon/measurability.ts` | Replay journal requests into input-side `ReaderTurn` snapshots keyed by native request id. Map current message and mode instructions, carry prior responses only on later requests, exclude the current response, register the reader for `copilot-vscode`, and declare only the buckets the format can actually reconstruct. | none; TypeScript worker selected by conductor | T3 |
| T8 | GREEN | Cursor/Cursor Agent reader or explicit provider-to-reader-turn seam under `dash/src/synth/readers/`; `dash/src/refresh/registry.ts`; `dash/src/canon/measurability.ts` | Retain available Cursor prompt and tool-context evidence without claiming a complete historical prefix. Add an explicit capability disposition for every descriptor and make `auditProviderRegistry()` fail on an unaccounted source/reader/measurability combination. Keep unavailable reasons source-specific. | none; TypeScript worker selected by conductor | T3 |
| T9 | GREEN | `dash/src/canon/store.ts`, schema migration, `dash/src/canon/refresh-run.ts`, `dash/src/refresh/orchestrator.ts` | Advance the current schema from v12 to v13 and implement stable problem identity in `MIGRATIONS[12]`; collapse duplicate rows transactionally before enforcing uniqueness, and stamp `schema_version` metadata to `13`. Add idempotent problem upsert/update behavior; reconcile `running` rows whose PID is dead; close refresh rows on all catchable failure paths; preserve streaming/bounded corpus reads. Add cleanup/renormalization support required by the approved Q1 option, including excluded legacy identities. | none; TypeScript worker selected by conductor | T4, Q1 |
| T10 | Integration | canonical ingest/projection/report integration tests, existing OTLP service tests, static refresh integration tests | Seed representative OTLP and static fixtures through their production entry points into one temporary store; assert projection and report behavior for every capability class, log enrichment reprojection, selector counts, cost, diagnostics, and CLI/API/tray parity. Retain the existing scheduler failure/retry and shutdown-drain contracts. | `test-dev` | T5-T9 |
| T11 | Review and deterministic gates | whole accumulated change | Run one full `code-review` council over the accumulated change, then the declared Dash typecheck, lint, and test gates. Attribute any generated/lock changes and fix review findings before deployment. | `code-review`, `test-dev` | T10 |
| T12 | Local remediation and deployment | approved `~/.kyberdash/canon.db` strategy, installed CLI, `/Users/dave/Applications/KyberDash.app` | Stop tray-owned children cleanly, make the timestamped backup, execute the approved migration/cutover, compare pre/post identity and report evidence, run one full refresh plus unchanged rerun with elapsed-time/peak-memory capture, rebuild/install the reviewed binaries, and restart services. Do not delete the backup during this plan. | `conductor` | T11, Q1 |
| T13 | Manual acceptance | tray, loopback API, web dashboard | Compare `/api/kyber/report`, web, and tray for the acceptance harnesses. The owner performs the final macOS status-item visual check if automation cannot attach to the status item; record the result without weakening automated parity gates. | `playwright` for web/API where useful | T12 |
| T14 | Documentation closeout | `docs/dash/architecture.md`, `docs/dash/telemetry-inventory.md`, `docs/dash/runbook.md`, this plan and `<plan-index>` | Record verified source capabilities, partial-measurability semantics, public bridge report reads, diagnostic/run lifecycle, observed live-refresh evidence, and recovery steps. Mark the plan Complete only after all gates and manual acceptance, harvest durable decisions if needed, then archive it through the governed lifecycle. | `app-docs-standard`, `kyber-weave-docs` | T13 |

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
| Diagnostic uniqueness hides a changed message | Stable identity updates the message/severity for the same span, code, and location rather than silently ignoring it. Migration retains an audit summary of collapsed rows. |
| A process can be killed before `finally` executes | The next owner reconciles dead-PID runs and stale locks. Ordinary exceptions still close their own run in a catch/finally path. |
| Fixing duplicate diagnostics does not fix the refresh termination cause | Live full-refresh completion, peak-memory evidence, and unchanged rerun are acceptance gates. Failure keeps the plan open and the backup/restoration path available. |
| Behind worktree or generated tray output is mistaken for source | T0 uses an isolated current-base worktree. No implementation writes into the present untracked `dash/tray/` tree. |

## 12. Out of scope

- Adding a second store, a tray-only read path, or a presentation-only harness selector.
- Estimating full context composition from file size, character totals, model names, or a single user-message fragment.
- Treating model providers such as Gemini as coding harnesses.
- Redesigning the tray popover or the broader six-level diagnostic navigation.
- Deleting the pre-migration backup during this work.
