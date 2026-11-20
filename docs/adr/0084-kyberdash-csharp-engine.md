---
id: adr/0084-kyberdash-csharp-engine
title: KyberDash collector, canonical store, and engine ported to C# on .NET 10
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-20
component: KyberDash
decided-by: []
supersedes: []
code-refs:
  - CanonSessionProjectorEngine
  - OtlpCollector
  - BoundedIngestWriter
  - DashEndpointMappings
  - JobSchedulerService
  - CanonSynth
  - CanonRefreshEngine
---

# ADR 0084: KyberDash collector, canonical store, and engine ported to C# on .NET 10

## Status

Accepted, 2026-11-15. Supersedes decision 5 of [ADR 0020](0020-kyberdash-one-time-fork.md) ("The engine stays TypeScript"). Decisions 1 through 4 of ADR 0020 and [ADR 0033](0033-kyberdash-surfaces-are-display-layers.md) remain in force.

## Context

Running the KyberDash collector and analytical processor in Node.js created sustained V8 heap and multi-process pressure:

- Full-corpus projection recomputed sessions across the canonical store instead of restricting work to sessions whose records or projection fingerprints changed.
- Large sessions inflated parsed span JSON into V8 object graphs while tokenizing and reducing context parts on one event loop.
- Background refreshes, folder imports, cleans, and OTLP reception spawned separate child Node CLI processes, multiplying resident memory and requiring separate Node Single Executable Application (SEA) builds alongside the `.NET 10` repository toolchain.

Decision 5 of [ADR 0020](0020-kyberdash-one-time-fork.md) anticipated moving the engine behind `/api/kyber/*` and `canon.db` to `.NET` without altering the display layers (`dash/web/` and `dash/tray/`).

## Decision

1. **The KyberDash collector, canonical SQLite store, analysis pipeline, HTTP/OTLP server, in-process job host, and fallback synthesizers are implemented in C# on .NET 10 under `dash/server/`, one Clean Architecture solution (`dash/server/KyberDash.sln`).**
   - The engine is layered: `KyberDash.Core` (shared primitives), `KyberDash.Domain` (canonical models and pure analysis), `KyberDash.Contracts` (service and repository interfaces), `KyberDash.Data` (the `sqlite3`-backed repositories), and `KyberDash.Application` (pipelines and hosted services). `KyberDash.Presentation.Tests` pins the dependency direction.
   - Storage (`CanonDbPath`, `CanonSchema`, `CanonCompressedColumn`, `CanonStoreRepository`) targets schema version 24 (`SCHEMA_VERSION = 24`) using bounded `ProcessRunner` invocations of the system `sqlite3` CLI (matching `CodeGraphResolverAdapter` and `SqliteAnalysisPersistence`).
   - Incremental projection (`CanonSessionProjectorEngine`, plus dirty-key tracking in `projection_dirty_sessions` and `projection_session_key_map`) recomputes only dirty session keys when schema, detector, parser, and pricing fingerprints match.
   - OTLP collection and primary harness normalization (`CanonValidation`, `CanonAdapters`, `CanonIngestPipeline`, `OtlpCollector`, `BoundedIngestWriter`) enforce 64 MiB request and 256 MiB pending backlog caps, 64 KiB UTF-8-safe content truncation, two-pass trace source inheritance, and `quarantine`/`problems`/`ingest_log` auditing.
   - Pricing, token memoization, context reduction, and findings (`CanonPricing`, `CachedTokenCounter`, `CanonContextAnalysis`, `CanonFindings`) keep `o200k` cache memoization and diagnostic schemas unchanged. The embedded pricing snapshots live under `dash/server/KyberDash.Data/Pricing/`.
   - Loopback transport and scheduling (`OtlpCollectorService` and `OtlpEndpointMappings`, `DashEndpointMappings`, `KyberBridgeEngine`, `JobSchedulerService`, `SettingsService`, `CleanService`, `CanonRollupsEngine`) run in-process over `jobs.lock` and atomic `server.json` publication while exposing the `/api/kyber/*`, `/api/events`, and `/v1/*` wire contracts.
   - Fallback synthesis, OTLP-precedence deduplication, streaming JSONL transcript reading, checkpointed commit units, and 14-day rolling content retention (`CanonSynth`, `CanonSynthDedup`, `TranscriptReaderRepository`, `CanonRefreshEngine`) complete parity across all local history paths.
2. **`dash/web/` and `dash/tray/` stay display layers with unchanged schemas and REST contracts** ([ADR 0033](0033-kyberdash-surfaces-are-display-layers.md)).
3. **The released KyberDash binaries build from `dash/server/` (`.NET 10` single-file publish), and the TypeScript engine (`dash/src/`) plus Node SEA packaging are retired.** Three presentation hosts share the engine: `KyberDash.Cli` (`kyberdash`: CLI router, reporter, diagnostics, menubar, e2e-seed launcher), `KyberDash.Api` (`kyberdash-api`: the loopback `/api/kyber/*` server and OTLP collector, embedding the packed `dash/dist-sea/web.json` as the `KyberDash.Api.web.json` resource), and `KyberDash.Mcp` (`kyberdash-mcp`: the stdio MCP analytics server).

## Alternatives Considered

- **Keep the engine in TypeScript/Node.js permanently.** Rejected: leaves multi-process child spawns, V8 JSON heap inflation, and dual-toolchain SEA maintenance in place.
- **Port to Go or Rust outside `.NET`.** Rejected: introduces a second backend binary runtime next to `KyberWeave.Cli`, `KyberWeave.Mcp`, and `KyberWeave.Core` rather than consolidating on `.NET 10`.
- **Use managed `Microsoft.Data.Sqlite` / `SQLitePCLRaw` inside `KyberWeave.Core`.** Rejected: keeps `SQLitePCLRaw.lib.e_sqlite3` (`GHSA-2m69-gcr7-jv3q`) out of the dependency graph by reusing the repository's `sqlite3` CLI `ProcessRunner` pattern.

## Consequences

- `dash/server/` is one solution split along Clean Architecture lines, and `KyberDash.Presentation.Tests` holds the assembly boundaries while every parity suite runs directly under `dotnet test` with no Node harness wrapper or process watchdog to install or run.
- `dash/` retains the React web dashboard (`dash/web/`), the Tauri tray (`dash/tray/`), Playwright UI end-to-end tests, and the packed web asset/smoke scripts (`dash/scripts/`). The retired TypeScript engine is not kept beside it: it is preserved by git history, so no build, scan or review target has to exclude a snapshot of code that cannot run.
- Release and local packaging produce self-contained `.NET 10` single-file binaries for `kyberdash`, `kyberdash-api`, and `kyberdash-mcp` across `linux-x64`, `linux-arm64`, `osx-x64`/`darwin-x64`, `osx-arm64`/`darwin-arm64`, and `win-x64` without Node SEA or `postject`.
