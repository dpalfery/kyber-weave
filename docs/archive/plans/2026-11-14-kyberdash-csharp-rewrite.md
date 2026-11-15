---
id: plans/2026-11-14-kyberdash-csharp-rewrite
title: Phased migration of the KyberDash collector, canonical store, and engine to C#
doc-type: plan
status: archived
component: KyberDash
owner: dpalfery
created: 2026-11-14
last-reviewed: 2026-11-15
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash]
decided-by:
  - adr/0020-kyberdash-one-time-fork
  - adr/0033-kyberdash-surfaces-are-display-layers
---

# Phased migration of the KyberDash collector, canonical store, and engine to C#

**Status: Archived, 2026-11-15.** Multi-slice architectural plan awaiting owner review before Slice 1 is carved for execution.
**Development mode:** `test-first`.
**Autonomy:** `autonomous` (initial architectural recommendations recorded in `## Decisions` for review).
**Scale:** `extends-component`, component `KyberDash`.

## Problem and goal

**Background.** The KyberDash engine under `dash/src/` comprises 122,212 lines of non-test TypeScript running on Node.js (`dash/src/canon/` 24,027 lines, `dash/src/providers/` 23,072 lines, `dash/src/capture/` 13,594 lines, `dash/src/analysis/` 11,753 lines, `dash/src/ingest/` 11,724 lines, `dash/src/server/` 10,325 lines, `dash/src/synth/` 4,815 lines, `dash/src/cli/` 4,150 lines, `dash/src/otel/` 3,954 lines, and `dash/src/refresh/` 3,328 lines). Both the React web dashboard (`dash/web/`) and the Tauri 2 tray (`dash/tray/`) act strictly as display layers over one canonical SQLite store (`canon.db`) and one `/api/kyber/*` HTTP API seam ([ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md)). [ADR 0020](../adr/0020-kyberdash-one-time-fork.md) kept the engine in TypeScript primarily to preserve 46 existing transcript parsers and parity tests, while noting that the REST seam permits moving the engine to .NET later.

**Problem.** Running the collector and analytical processor in Node.js creates sustained CPU and memory pressure during normal usage:

- **V8 object graph and JSON inflation:** As documented in `dash/src/canon/sessions.ts`, a single 10,933-call session inflated parsed span payloads to 1.5 GB (3.5 GB V8 heap peak prior to payload projection) while running `o200k` tokenization, context-bucket reduction, schema ranking, and finding detectors on a single JavaScript event loop.
- **Full-corpus projection work:** `projectCanonicalStore` / `buildSessions` recomputes across the store rather than limiting projection work to sessions whose records, parser contracts, or pricing actually changed.
- **Multi-process runtime overhead:** `JobHost` (`dash/src/jobs/host.ts`) and `ReceiverHost` (`dash/src/jobs/receiver-host.ts`) spawn separate child Node CLI processes for refreshes, folder imports, cleans, and OpenTelemetry Protocol (OTLP) ingestion, multiplying resident memory and forcing the repository to maintain both Node Single Executable Application (SEA) packaging and `scripts/proc-guard.sh` worker caps.

**Goal.** Migrate the KyberDash collector, canonical SQLite store, session projection, analysis pipeline, `/api/kyber/*` HTTP server, and background job runner to C# on .NET 10 while keeping `canon.db` schema compatibility and `/api/kyber/*` JSON contracts unchanged so `dash/web/` and `dash/tray/` require no display-layer rewrite. Deliver the migration in five single-PR slices (each carved into its own slice plan and archived on completion per the [plan inventory policy](README.md)).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Which language replaces Node.js/TypeScript for the collector and engine? | **C# on .NET 10** under `src/KyberWeave.Core/Dash/` (namespace `KyberWeave.Core.Dash`) hosted by the CLI (`src/KyberWeave.Cli` / `kyberdash`). | Unifies the repository on the existing .NET 10 stack (`KyberWeave.Core` as pinned by `CoreAssemblySplitTests`, `KyberWeave.Cli`, `KyberWeave.Mcp`, `KyberWeave.Arbiter`), streams JSON with `Utf8JsonReader` / `Span<T>` / `System.IO.Pipelines` without V8 heap inflation, eliminates Node SEA packaging, and matches the migration path anticipated by [ADR 0020](../adr/0020-kyberdash-one-time-fork.md). SQLite access uses the system `sqlite3` CLI through `ProcessRunner` (matching `CodeGraphResolverAdapter` and `SqliteAnalysisPersistence`) to avoid `SQLitePCLRaw.lib.e_sqlite3` advisory `GHSA-2m69-gcr7-jv3q`. | APPROVED: autonomous: recommendation accepted, 2026-11-14 (all 5 slices implemented on single branch per owner direction) |
| D2 | How is compatibility with `dash/web/` and `dash/tray/` preserved during migration? | **Keep the `canon.db` SQLite schema (`SCHEMA_VERSION`) and `/api/kyber/*` HTTP/JSON contracts unchanged.** | Both UI surfaces are display layers behind `/api/kyber/*` ([ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md)), and existing parity/route tests under `dash/src/server/` pin exact response payloads and `not_measurable` semantics. Preserving the wire contract lets the C# backend act as a drop-in server replacement via `~/.kyberdash/server.json`. | APPROVED: autonomous: recommendation accepted, 2026-11-14 (all 5 slices implemented on single branch per owner direction) |
| D3 | How is the rewrite sequenced across pull requests? | **Five independent single-PR slices, starting with incremental per-session projection and `CanonStore` SQLite parity**, before porting ingestion, analyses, server/jobs, and fallback file readers. | One plan never spans multiple PRs (`KW-DOC-LIFECYCLE-003`). Making projection incremental in Slice 1 fixes algorithmic $O(\text{corpus})$ work first so later runtime improvements compound rather than porting full-corpus scans verbatim. Porting primary OTLP collection before the 41 fallback file readers follows [KyberDash architecture](../dash/architecture.md), where OTLP/hooks are primary and file synthesis is backup only. | APPROVED: autonomous: recommendation accepted, 2026-11-14 (all 5 slices implemented on single branch per owner direction) |
| D4 | How are scheduled refresh, import, and clean jobs executed in the C# engine? | **In-process bounded background services (`BackgroundService`, `Channel<T>`, `PeriodicTimer`) sharing one SQLite/file lease (`jobs.lock`)** instead of spawning child CLI processes. | Eliminates per-job runtime startup latency and multi-process RSS spikes while preserving cross-process mutual exclusion against manual terminal `kyberdash` CLI runs through the same refresh lease. | APPROVED: autonomous: recommendation accepted, 2026-11-14 (all 5 slices implemented on single branch per owner direction) |
| D5 | What ADR updates are required? | **Author a new ADR superseding Decision 5 of [ADR 0020](../adr/0020-kyberdash-one-time-fork.md)** when Slice 1/Slice 2 executes. | [ADR 0020](../adr/0020-kyberdash-one-time-fork.md) currently records Decision 5 ("The engine stays TypeScript") and explicitly states when to revisit it. Leaving Decision 5 un-superseded after moving the collector and store to C# would violate architectural governance. | APPROVED: autonomous: recommendation accepted, 2026-11-14 (all 5 slices implemented on single branch per owner direction) |

## Design harvest

| ID | Durable item | Target component | Target doc-type | Target document |
|---|---|---|---|---|
| H1 | Decision moving the KyberDash collector and engine from TypeScript/Node SEA to C# on .NET 10 (superseding ADR 0020 Decision 5) | KyberDash | `adr` | [ADR 0084](../adr/0084-kyberdash-csharp-engine.md) |
| H2 | C# `CanonStore`, incremental dirty-session projection, `OtlpCollector`, `BoundedIngestWriter`, `KyberJobHost`, `KyberApiServer`, and `CanonSynth` design | KyberDash | `architecture` | [KyberDash architecture](../dash/architecture.md) |
| H3 | Local build and targeted parity test verification for `KyberWeave.Core.Dash` | KyberDash | `runbook` | [KyberDash runbook](../dash/runbook.md) |
| H4 | Follow-up CLI/MCP/tray entrypoint switchover and Node SEA packaging retirement | KyberDash | `todo` | [kyberdash-cli-switchover-and-node-sea-retirement.md](../todo/kyberdash-cli-switchover-and-node-sea-retirement.md) |

## Architecture and Phased Slice Breakdown

### Component mapping

| Current TypeScript module (`dash/src/`) | Planned C# target (`src/KyberWeave.Core/Dash/`) | Primary performance and operational improvement |
|---|---|---|
| `canon/store.ts`, `store-rows.ts`, `store-schema.ts`, `store-migrations.ts`, `paths.ts`, `compressed-column.ts` | `CanonStore.cs`, `CanonSchema.cs`, `CanonDbPath.cs`, `CanonCompressedColumn.cs` | Bounded `sqlite3` CLI batches via `ProcessRunner` (avoiding `GHSA-2m69-gcr7-jv3q`), hex-literal safe parameterization, WAL setup, zero-dependency zlib columns |
| `canon/projection.ts`, `canon/sessions.ts`, `canon/twin-dedupe.ts`, `canon/tokens.ts` | `CanonSessionProjector.cs`, `CanonTokenCache.cs` | Incremental dirty-session projection (`projection_dirty_sessions` / `projection_session_key_map`), twin turn deduplication, SQLite-backed token count memoization, compressed `session_turn` persistence |
| `otel/receiver.ts`, `otel/writer.ts`, `canon/ingest.ts`, `canon/validate.ts`, `canon/adapters/*` | `CanonModels.cs`, `CanonValidation.cs`, `CanonAdapters.cs`, `CanonIngest.cs`, `OtlpCollector.cs`, `BoundedIngestWriter.cs` | Eight weighted harness adapters with trace-level source inheritance, 64 KiB UTF-8-safe truncation, OTLP JSON decoding, bounded 256 MiB / 64 MiB ingest backlog (`413`, `503 + Retry-After: 1`) |
| `analysis/context.ts`, `schema.ts`, `timeline.ts`, `analysis/outcomes.ts`, `analysis/findings.ts`, `canon/pricing.ts`, `canon/cost.ts` | `CanonContextAnalysis.cs`, `CanonFindings.cs`, `CanonPricing.cs` | Embedded LiteLLM/fallback rate tables with tiered prompt escalation, empirical outcomes, D5/D6/D8 diagnostic detectors, schema rent bounds, and hierarchical timeline trees |
| `server/routes.ts`, `server/bridge.ts`, `server/analytics-routes.ts`, `jobs/host.ts`, `mcp/*` | `LoopbackHttpHost.cs`, `OtlpHttpServer.cs`, `KyberApiServer.cs`, `KyberBridge.cs`, `KyberJobHost.cs`, `CanonSettings.cs`, `CanonClean.cs`, `CanonRollups.cs` | Unified loopback HTTP/1.1 + OTLP Protobuf server (`~/.kyberdash/server.json`), in-process `KyberJobHost` replacing child Node spawns |
| `synth/synth.ts`, `synth/dedup.ts`, `synth/readers/*`, `canon/retention.ts`, `canon/source-state.ts`, `refresh/*` | `CanonSynth.cs`, `CanonSynthDedup.cs`, `CanonTranscriptReaders.cs`, `CanonRefresh.cs` | Incremental file checkpointing (`source_checkpoint`, `record_provenance`), 14-day rolling retention purge, and `StreamReader` line-by-line JSONL streaming instead of whole-file string reads |

### Five-slice delivery roadmap

Each slice below will be executed under its own single-PR plan (created, executed, harvested, and archived when its PR merges):

1. **Slice 1 — Incremental Projection & `CanonStore` SQLite Core in C# (Implemented in current branch)**
   - Added per-session dirty tracking (`PROJECTION_DIRTY_SESSIONS_KEY` / `PROJECTION_SESSION_KEY_MAP_KEY`) across `records` writes in `dash/src/canon/store.ts`, `sessions.ts`, and `projection.ts` so unchanged session keys skip full payload re-projection when schema, detector, parser-contract, and pricing fingerprints are current.
   - Created `src/KyberWeave.Core/Dash/` (`CanonDbPath.cs`, `CanonSchema.cs`, `CanonCompressedColumn.cs`, `CanonStore.cs`) using `ProcessRunner` + `sqlite3` CLI batches (`SCHEMA_VERSION = 24`, `DETECTOR_VERSION = 5`, hex-encoded literals, WAL pragmas, and zlib compression parity) with allow-listed process bounds.
   - Verified with `CanonStoreParityTests` in `tests/KyberWeave.Tests/Dash/` and Vitest suites in `dash/src/canon/`.

2. **Slice 2 — Native .NET OTLP Collector, `BoundedIngestWriter` & Primary Harness Adapters (Implemented in current branch)**
   - Implemented `CanonModels.cs`, `CanonValidation.cs`, `CanonAdapters.cs`, `CanonIngest.cs`, `OtlpCollector.cs`, and `BoundedIngestWriter.cs` in `src/KyberWeave.Core/Dash/` covering OTLP JSON span/log/metric decoding, strict 64 MiB request body and 256 MiB in-flight backlog bounds (`413`, `503` + `Retry-After: 1`), 64 KiB UTF-8-safe content truncation, eight harness adapters (`claude-code`, `opencode`, `droid`, `copilot`, `gemini`, `antigravity`, `codex`, `pi`) with two-pass trace inheritance, and `quarantine` / `problems` / `ingest_log` routing.
   - Verified with `CanonIngestParityTests` in `tests/KyberWeave.Tests/Dash/` (Kestrel HTTP listener hosting moves into Slice 4 alongside `/api/kyber/*` endpoints).

3. **Slice 3 — Canonical Session Projection, Token Cache, Pricing & Context Analyses in C# (Implemented in current branch)**
   - Implemented `CanonPricing.cs` (embedded LiteLLM/fallback snapshots, Copilot credits, `GPT-6 Luna` and `Grok 4.6` tier escalation, `ContextLimitOf`), `CanonTokenCache.cs` (`token_cache` SQLite memoization above 512 chars), `CanonContextAnalysis.cs` (`ReduceContextParts`, `AnalyzeContext`, `RankSchemas`, `BuildTimeline`), `CanonFindings.cs` (empirical outcomes, D5/D6/D8 validation, and all seven finding detectors), and `CanonSessionProjector.cs` (`DedupeTwinTurns`, `BuildSessionSummary`, `ProjectIncremental`, plus `session`/`session_turn`/`finding` persistence in `CanonStore.cs`).
   - Verified with `CanonAnalysisParityTests` in `tests/KyberWeave.Tests/Dash/`.

4. **Slice 4 — `/api/kyber/*` HTTP Server, In-Process `JobHost`, Capture CLI & MCP (Core server & job host implemented in current branch)**
   - Ported the loopback HTTP/1.1 transport, OTLP Protobuf/HTTP receiver, settings/clean orchestrators, run/harness rollups, in-process `KyberJobHost`, `KyberBridge`, and `KyberApiServer` routes (`/api/kyber/*` and `/api/events` SSE) into `src/KyberWeave.Core/Dash/`, with CLI/MCP host wiring and tray switchover staged for final host integration.
   - Replace Node child-spawning `JobHost` and `ReceiverHost` with in-process .NET `KyberJobHost` workers coordinated through `jobs.lock` and atomic `server.json` publication.
   - Verified with `CanonServerParityTests` in `tests/KyberWeave.Tests/Dash/`.

5. **Slice 5 — Fallback Transcript Synthesizers, Deduplication, Streaming Readers & Checkpointed Refresh (Implemented in current branch)**
   - Implemented `CanonSynth.cs`, `CanonSynthDedup.cs`, `CanonTranscriptReaders.cs`, `CanonRefresh.cs`, and extended `CanonStore.ServerQueries.cs` (`CommitSourceUnit`, `PurgeContentBefore`, `source_checkpoint`, `record_provenance`, `refresh_run`) plus `KyberJobHost` 14-day rolling retention integration.
   - Verified with `CanonSynthParityTests` in `tests/KyberWeave.Tests/Dash/` (Node SEA packaging changes remain deferred under out-of-scope release build configuration until downstream binary switchover).

## Slice 1 Tasks (Completed)

- **T1: Incremental dirty-session projection in TypeScript baseline (`dash/src/canon/`):**
  - Track dirty session keys (`projection_dirty_sessions`, `projection_session_key_map`) on `records` writes in `dash/src/canon/store.ts`, `sessions.ts`, and `projection.ts` (with `snapshotPricingState()` aliases and price overrides folded into `pricingFingerprint()`), and added Vitest coverage in `dash/src/canon/projection.test.ts` verifying unchanged sessions are not reloaded.
- **T2 & T3: C# `KyberWeave.Core.Dash` foundation (`src/KyberWeave.Core/Dash/`):**
  - Ported `CanonDbPath.cs`, `CanonSchema.cs`, `CanonCompressedColumn.cs`, and `CanonStore.cs` under `KyberWeave.Core` (preserving the ADR 0057 assembly split pinned by `CoreAssemblySplitTests` and avoiding `SQLitePCLRaw.lib.e_sqlite3` advisory `GHSA-2m69-gcr7-jv3q` via bounded `ProcessRunner` + `sqlite3` CLI batches registered in `spawn-sites-allowlist.json`).
- **T4: C# parity unit test suite (`tests/KyberWeave.Tests/Dash/CanonStoreParityTests.cs`):**
  - Added `CanonStoreParityTests` verifying path precedence, exact schema version 24 refusal message parity, normalizer edge cases, RFC 1950 zlib round-trips, table initialization, metadata compare-and-set, and newer-schema refusal.

## Slice 2, Slice 3 & Slice 4 Tasks (Completed)

- **Slice 2 (`src/KyberWeave.Core/Dash/` & `tests/KyberWeave.Tests/Dash/CanonIngestParityTests.cs`):**
  - Implemented `CanonModels.cs`, `CanonValidation.cs`, `CanonAdapters.cs`, `CanonIngest.cs`, `OtlpCollector.cs`, `BoundedIngestWriter.cs`, and extended `CanonStore.cs` with record, quarantine, problem, and ingest-log persistence; verified parity with `CanonIngestParityTests`.
- **Slice 3 (`src/KyberWeave.Core/Dash/` & `tests/KyberWeave.Tests/Dash/CanonAnalysisParityTests.cs`):**
  - Embedded `Dash.litellm-snapshot.json` and `Dash.pricing-fallback.json` in `KyberWeave.Core.csproj`; implemented `CanonPricing.cs`, `CanonTokenCache.cs`, `CanonContextAnalysis.cs`, `CanonFindings.cs`, `CanonSessionProjector.cs`, and extended `CanonStore.cs` with session (`session_turn`), finding, token-cache, and dirty-projection persistence; verified parity with `CanonAnalysisParityTests`.
- **Slice 4 (`src/KyberWeave.Core/Dash/` & `tests/KyberWeave.Tests/Dash/CanonServerParityTests.cs`):**
  - Added bounded OTLP Protobuf decoding (`DecodeTracesProtobuf`, `DecodeLogsProtobuf`, `DecodeMetricsProtobuf`) to `OtlpCollector.cs`, added metric queueing to `BoundedIngestWriter.cs`, and implemented `LoopbackHttpHost.cs`, `OtlpHttpServer.cs`, `CanonStore.ServerQueries.cs`, `CanonSettings.cs`, `CanonClean.cs`, `CanonRollups.cs`, `KyberJobHost.cs`, `KyberBridge.cs`, and `KyberApiServer.cs`; verified parity with `CanonServerParityTests`.
- **Slice 5 (`src/KyberWeave.Core/Dash/` & `tests/KyberWeave.Tests/Dash/CanonSynthParityTests.cs`):**
  - Implemented `CanonSynth.cs` (44 provider conventions, exclusive reasoning fold, Cursor pairing exclusion, 64 KiB `tool.invoke` synthesis), `CanonSynthDedup.cs` (OTLP/file collapse and `DEDUP_DISAGREEMENT` audit problems), `CanonTranscriptReaders.cs` (streaming Claude Code and Codex JSONL readers), and `CanonRefresh.cs` + `CanonStore.ServerQueries.cs` (`CommitSourceUnit` checkpointing, 14-day `PurgeExpiredContent`, and `RefreshHarnessSources`), with `KyberJobHost` retention sweep wiring; verified parity with `CanonSynthParityTests`.

## Out of scope

- Rewriting `dash/web/**` React components or `dash/tray/**` Tauri shell beyond updating the tray's launched backend executable path in Slice 4.
- Altering `/api/kyber/*` JSON contracts, finding schemas, cost-basis rules ([secondary-cost-display](../rules/secondary-cost-display.md)), or unobservability semantics ([honest-unobservability](../rules/honest-unobservability.md)).
- Modifying `scripts/proc-guard.sh` process caps for existing `dash/` Node tests while TypeScript tests remain active.

## Closeout mapping

| ID | Destination | ADR or waiver |
|---|---|---|
| D1, D5, H1 | [ADR 0084](../../adr/0084-kyberdash-csharp-engine.md); [ADR 0020](../../adr/0020-kyberdash-one-time-fork.md) | [ADR 0084](../../adr/0084-kyberdash-csharp-engine.md) |
| D2, D3, D4, H2 | [KyberDash architecture](../../dash/architecture.md) | [ADR 0084](../../adr/0084-kyberdash-csharp-engine.md) |
| H3 | [KyberDash runbook](../../dash/runbook.md) | no ADR — ADR waiver: operator build and verification commands for the C# engine decided in ADR 0084 |
| H4, RESIDUAL | [kyberdash-cli-switchover-and-node-sea-retirement.md](../../todo/kyberdash-cli-switchover-and-node-sea-retirement.md) | no ADR — ADR waiver: tracks follow-up CLI/Tray entrypoint cutover and Node SEA build retirement |
