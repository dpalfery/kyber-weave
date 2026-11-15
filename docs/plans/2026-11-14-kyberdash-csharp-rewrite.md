---
id: plans/2026-11-14-kyberdash-csharp-rewrite
title: Phased migration of the KyberDash collector, canonical store, and engine to C#
doc-type: plan
status: draft
component: KyberDash
owner: dpalfery
created: 2026-11-14
last-reviewed: 2026-11-14
development-mode: test-first
autonomy: autonomous
documentation-impact: extends-component
impact-components: [KyberDash, Distribution, ReviewCouncil]
decided-by:
  - adr/0020-kyberdash-one-time-fork
  - adr/0033-kyberdash-surfaces-are-display-layers
---

# Phased migration of the KyberDash collector, canonical store, and engine to C#

**Status: Draft, 2026-11-14.** Multi-slice architectural plan awaiting owner review before Slice 1 is carved for execution.
**Development mode:** `test-first`.
**Autonomy:** `autonomous` (initial architectural recommendations recorded in `## Decisions` for review).
**Scale:** `extends-component`, components `KyberDash`, `Distribution`, `ReviewCouncil`.

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
| D1 | Which language replaces Node.js/TypeScript for the collector and engine? | **C# on .NET 10** in a new core library (`src/KyberWeave.Core.Dash`) hosted by the CLI (`src/KyberWeave.Cli` / `kyberdash`). | Unifies the repository on the existing .NET 10 stack (`KyberWeave.Core`, `KyberWeave.Cli`, `KyberWeave.Mcp`, `KyberWeave.Arbiter`), streams JSON with `Utf8JsonReader` / `Span<T>` / `System.IO.Pipelines` without V8 heap inflation, eliminates Node SEA packaging, and matches the migration path anticipated by [ADR 0020](../adr/0020-kyberdash-one-time-fork.md). Rejected: Rust (high velocity tax across 46 evolving harness transcript formats and splits backend domain logic between C# and Rust) and Go (introduces a fourth primary language/toolchain into the repository). | autonomous: recommendation accepted (pending owner review) |
| D2 | How is compatibility with `dash/web/` and `dash/tray/` preserved during migration? | **Keep the `canon.db` SQLite schema (`SCHEMA_VERSION`) and `/api/kyber/*` HTTP/JSON contracts unchanged.** | Both UI surfaces are display layers behind `/api/kyber/*` ([ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md)), and existing parity/route tests under `dash/src/server/` pin exact response payloads and `not_measurable` semantics. Preserving the wire contract lets the C# backend act as a drop-in server replacement via `~/.kyberdash/server.json`. | autonomous: recommendation accepted (pending owner review) |
| D3 | How is the rewrite sequenced across pull requests? | **Five independent single-PR slices, starting with incremental per-session projection and `CanonStore` SQLite parity**, before porting ingestion, analyses, server/jobs, and fallback file readers. | One plan never spans multiple PRs (`KW-DOC-LIFECYCLE-003`). Making projection incremental in Slice 1 fixes algorithmic $O(\text{corpus})$ work first so later runtime improvements compound rather than porting full-corpus scans verbatim. Porting primary OTLP collection before the 41 fallback file readers follows [KyberDash architecture](../dash/architecture.md), where OTLP/hooks are primary and file synthesis is backup only. | autonomous: recommendation accepted (pending owner review) |
| D4 | How are scheduled refresh, import, and clean jobs executed in the C# engine? | **In-process bounded background services (`BackgroundService`, `Channel<T>`, `PeriodicTimer`) sharing one SQLite/file lease (`jobs.lock`)** instead of spawning child CLI processes. | Eliminates per-job runtime startup latency and multi-process RSS spikes while preserving cross-process mutual exclusion against manual terminal `kyberdash` CLI runs through the same refresh lease. | autonomous: recommendation accepted (pending owner review) |
| D5 | What ADR updates are required? | **Author a new ADR superseding Decision 5 of [ADR 0020](../adr/0020-kyberdash-one-time-fork.md)** when Slice 1/Slice 2 executes. | [ADR 0020](../adr/0020-kyberdash-one-time-fork.md) currently records Decision 5 ("The engine stays TypeScript") and explicitly states when to revisit it. Leaving Decision 5 un-superseded after moving the collector and store to C# would violate architectural governance. | autonomous: recommendation accepted (pending owner review) |

## Design harvest

| Durable item | Target component | Target doc-type | Target document |
|---|---|---|---|
| Decision moving the KyberDash collector and engine from TypeScript/Node SEA to C# on .NET 10 (superseding ADR 0020 Decision 5) | KyberDash | `adr` | Planned `docs/adr/0082-kyberdash-csharp-engine.md` |
| C# `CanonStore`, incremental dirty-session projection, `OtlpReceiver`, `IngestWriter`, and in-process `JobHost` design | KyberDash | `architecture` | [KyberDash architecture](../dash/architecture.md) |
| Local build, run, and verification instructions for the .NET KyberDash host | KyberDash | `runbook` | [KyberDash runbook](../dash/runbook.md) |
| Retirement of Node SEA binaries in favour of unified .NET single-file/AOT artifacts | Distribution | `reference` | [Distribution](../distribution.md), [Install](../install.md) |

## Architecture and Phased Slice Breakdown

### Component mapping

| Current TypeScript module (`dash/src/`) | Planned C# target (`src/KyberWeave.Core.Dash/`) | Primary performance and operational improvement |
|---|---|---|
| `canon/store.ts`, `store-rows.ts`, `store-schema.ts`, `store-migrations.ts` | `Store/CanonStore.cs`, `Store/CanonSchema.cs`, `Store/CanonMigrations.cs` | Prepared `Microsoft.Data.Sqlite` statements, zero-allocation column readers, WAL connection pooling, compiled row mappers |
| `canon/projection.ts`, `canon/sessions.ts`, `canon/tokens.ts` | `Projection/CanonicalProjectionScheduler.cs`, `Projection/SessionProjector.cs`, `Tokens/O200kTokenizer.cs` | Incremental dirty-session projection (`session_id`-level dirty markers), streaming payload attribute extraction via `Utf8JsonReader`, `Microsoft.ML.Tokenizers` `o200k_base` without V8 heap inflation |
| `otel/receiver.ts`, `otel/writer.ts`, `otel/aspire.ts`, `canon/ingest.ts`, `canon/adapters/*` | `Ingest/OtlpHttpEndpoints.cs`, `Ingest/BoundedIngestWriter.cs`, `Ingest/Adapters/*` | Kestrel `:4318` streaming `PipeReader` / protobuf decoding (`Google.Protobuf` / hand-rolled wire decoder), `Channel<IngestBatch>` bounded backlog enforcing 256 MiB / 64 MiB limits (`413`, `503 + Retry-After`) |
| `analysis/context.ts`, `schema.ts`, `timeline.ts`, `findings.ts`, `canon/cost.ts` | `Analysis/ContextAnalyzer.cs`, `Analysis/SchemaRanker.cs`, `Analysis/TimelineBuilder.cs`, `Analysis/FindingDetectors.cs`, `Pricing/CostCalculator.cs` | Value-type structs (`record struct`) for token/cost vectors, strict honest-unobservability tri-state metrics (`Measured<T>` / `NotMeasurableReason`), deterministic golden parity against `ParityDigest` |
| `server/routes.ts`, `server/bridge.ts`, `server/analytics-routes.ts`, `jobs/host.ts`, `mcp/*` | `Server/KyberApiEndpoints.cs`, `Jobs/InProcessJobHost.cs`, `Mcp/DashAnalyticsTools.cs` | Unified ASP.NET Core localhost server (`~/.kyberdash/server.json`), in-process scheduled `BackgroundService` replacing child Node spawns, in-process MCP stdio host |
| `synth/readers/*`, `providers/*`, `refresh/*` | `Synth/Readers/*`, `Refresh/HarnessRefreshService.cs` | Incremental file checkpointing with `FileStream` / `StreamReader` line streaming instead of whole-file string reads |

### Five-slice delivery roadmap

Each slice below will be executed under its own single-PR plan (created, executed, harvested, and archived when its PR merges):

1. **Slice 1 — Incremental Projection & `CanonStore` SQLite Core in C#**
   - Add per-session dirty tracking to projection scheduling so only modified sessions (`session_id` dirty table / write generation) are recomputed, stopping full-corpus scans on every ingest batch.
   - Create `src/KyberWeave.Core.Dash/KyberWeave.Core.Dash.csproj` (added to `KyberWeave.sln`) and port SQLite schema creation, idempotent migrations (`SCHEMA_VERSION`), WAL pragmas, read/write store operations, and compressed-content reads with byte-for-byte parity against existing TypeScript databases.
   - Exit gate: SQLite fixture cross-read/write round-trip tests in `tests/KyberWeave.Tests/` pass on databases created by the current `dash/src/canon/store.ts`.

2. **Slice 2 — Native .NET OTLP Collector, `IngestWriter` & Primary Harness Adapters**
   - Implement the Kestrel OTLP HTTP listener (`:4318`, `/v1/traces`, `/v1/logs`) decoding both OTLP JSON (`Utf8JsonReader`) and OTLP protobuf with strict 64 MiB request body and 256 MiB in-flight backlog bounds (`413`, `503` + `Retry-After: 1`), structured `deriveLogId` correlation, 64 KiB UTF-8-safe content truncation, and `quarantine` routing.
   - Port the OTel harness adapters (`claude-code`, `opencode`, `droid`, `copilot`, `gemini`, `antigravity`, `codex`, `pi`), `AspireSource`, and `capture/` hook spool ingestion.
   - Exit gate: Corpus fixture parity tests verifying identical `CanonStore` rows (`records`, `content_parts`, `ingest_log`, `quarantine`) for OTLP JSON, protobuf, and hook capture fixtures; benchmark memory stays under bounded channel ceilings on 10k-span bursts.

3. **Slice 3 — Canonical Session Projection, Tokenizers, Pricing & Context Analyses in C#**
   - Port `buildSessions`, `analyzeContext`, `reduceContextParts`, `rankSchemas`, `buildTimeline`, `compareRuns`, `dedupeTwinTurns`, `buildFindingsYielding`, `buildHarnessRollup`, `buildRuns`, published pricing / copilot rates, model window catalogs, and `o200k` cached token counting.
   - Enforce honest-unobservability and composite-efficiency rules at compile time via strong types rather than runtime coercion.
   - Exit gate: Golden snapshot parity suite comparing C# `ContextReport` and `sessions` SQLite cache outputs against TypeScript outputs (`ParityDigest`) over multi-provider session corpora, plus benchmark verifying peak working set drops cleanly on large sessions (`10,000+` calls).

4. **Slice 4 — `/api/kyber/*` HTTP Server, In-Process `JobHost`, Capture CLI & MCP**
   - Port the `/api/kyber/*` routes (`routes.ts`, `bridge*.ts`, `analytics-routes.ts`, `capture-routes.ts`), `kyberdash mcp` read-only analytics tools, and `kyberdash` CLI subcommands (`web`, `report`, `clean`, `import-history`, `settings`, `capture`, `status`) onto the .NET host.
   - Replace Node child-spawning `JobHost` and `ReceiverHost` with in-process .NET `BackgroundService` workers coordinated through `jobs.lock` and `projection-lease`.
   - Switch `~/.kyberdash/server.json` startup and `dash/tray/` backend launch target to the .NET binary; run Playwright end-to-end tests (`dash/playwright.isolated.config.ts`) against the C# backend.

5. **Slice 5 — Fallback Transcript Synthesizers & Node SEA Retirement**
   - Port the remaining active fallback file readers (`dash/src/synth/readers/*` and active `dash/src/providers/*`) to streaming C# readers.
   - Retire Node SEA build/packaging from `scripts/install.sh` and `.github/workflows/release.yml`, keeping Node strictly for building static React assets (`dash/web/` and `dash/tray/ui/`) and running frontend/Tauri tests.

## Slice 1 Tasks (Ready to Carve for First Implementation PR)

When approved to begin implementation, carve Slice 1 into its own standalone Ready plan (`docs/plans/YYYY-MM-DD-kyberdash-csharp-slice1-store.md`) covering these four tasks:

- **T1: Incremental dirty-session projection in TypeScript baseline (`dash/src/canon/`):**
  - Track dirty `session_id` keys on `ingest` / `refresh` writes in `dash/src/canon/store.ts` and `dash/src/canon/projection.ts` so live `CanonicalProjectionScheduler` ticks project only affected sessions (unless schema/pricing/parser contract fingerprints changed), adding vitest coverage verifying unchanged sessions are not re-projected.
- **T2: Scaffold `src/KyberWeave.Core.Dash` (`net10.0`, `Microsoft.Data.Sqlite`):**
  - Create `src/KyberWeave.Core.Dash/KyberWeave.Core.Dash.csproj` honoring `TreatWarningsAsErrors` and `AnalysisMode=all`, register in `KyberWeave.sln`, and port `CanonSchema.cs` + idempotent `CanonMigrations.cs` matching `dash/src/canon/store-schema.ts` and `dash/src/canon/store-migrations.ts` at exact `SCHEMA_VERSION`.
- **T3: Port `CanonStore` core read/write/metadata/content-part storage in C#:**
  - Implement WAL setup, `ResolveCanonDbPath` (`dash/src/canon/paths.ts`), record/span inserts, metadata key/value access, UTF-8 64 KiB boundary truncation, compressed column helpers, and retention queries with explicit failure propagation (matching `FileFailure` / SQLite busy handling).
- **T4: Cross-runtime SQLite parity test suite (`tests/KyberWeave.Tests/`):**
  - Add `KyberWeave.Tests.Dash.CanonStoreParityTests` verifying schema DDL equivalence, migration idempotency on existing TypeScript-created `canon.db` fixtures, and bidirectional row/metadata/content round-trips (`dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj -c Release --filter "FullyQualifiedName~CanonStoreParityTests"`).

## Out of scope

- Rewriting `dash/web/**` React components or `dash/tray/**` Tauri shell beyond updating the tray's launched backend executable path in Slice 4.
- Altering `/api/kyber/*` JSON contracts, finding schemas, cost-basis rules ([secondary-cost-display](../rules/secondary-cost-display.md)), or unobservability semantics ([honest-unobservability](../rules/honest-unobservability.md)).
- Modifying `scripts/proc-guard.sh` process caps for existing `dash/` Node tests while TypeScript tests remain active.
