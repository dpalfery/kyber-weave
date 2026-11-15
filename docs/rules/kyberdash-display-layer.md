---
id: rules/kyberdash-display-layer
title: The KyberDash tray and web dashboard are display layers
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-11-15
decided-by:
  - adr/0033-kyberdash-surfaces-are-display-layers
---

# The KyberDash tray and web dashboard are display layers

> **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS ISOLATED FEATURE
> LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING, CLEAN, IMPORT, PAUSE)
> LIVE IN THE SHARED KYBERDASH C# APPLICATION (`dash/server/`, INCLUDING THE ENGINE, THE
> `kyberdash` CLI, AND THE `kyberdash web` SERVER) THAT BOTH SURFACES SHARE. BOTH SURFACES USE
> ONE DATASTORE (`canon.db`, resolved by `KyberDash.Domain.CanonDbPath.Resolve`) AND ONE API
> LAYER (`/api/kyber/*`). NO AGENT OR DESIGN MAY VIOLATE THIS.**

This rule governs `dash/web/**` and `dash/tray/**` — every KyberDash surface that is not the
engine. It is current guidance and carries the standing of the other
[rules](README.md); it is enforced mechanically
([Enforcement](#enforcement)), not by review diligence alone.

## Motivation

Two shells that each hold their own copy of engine behaviour are two places for it to be
wrong, and the divergence is invisible until a user compares two surfaces. The tray's own
scheduler is what produced
[issue #319](https://github.com/dpalfery/kyber-weave/issues/319): a user cleaned the
database from the tray, and within five minutes the tray's own cadence re-imported the
folder history while the dashboard showed a different, stale answer — one store, two
schedulers, two truths. The structural fix is to leave exactly one owner of behaviour: the
engine.

## What a surface may do

- **Render.** Draw the `ContextReport`, `ViewState`, and the JSON every `/api/kyber/*`
  route serves. Formatting text and laying out values is display work.
- **Invoke actions through the API.** A clean, an import, a pause, a settings change is an
  HTTP call to `/api/kyber/*` (from Rust, for the tray; from the browser, for the web
  dashboard). Asking the engine to act is not acting.
- **Open links and hand off.** Deep links, `open_view`, opening the dashboard in a browser.
- **Hold display preferences.** Which harness is selected, which window of days is shown,
  window geometry. These are view state, not product settings.
- **Poll read-only engine state to repaint.** A surface may re-read a *read-only*
  `/api/kyber/*` endpoint on a timer so an open view notices that the ground moved
  underneath it — `dash/web/src/lib/storeGeneration.ts` polls `GET /api/kyber/jobs` and
  invalidates the query cache only when the store generation changes. Reading is not
  scheduling: the poll starts nothing, decides no cadence, and cannot make work happen.
  What it must never do is *start, schedule or cadence-drive a job* — see below.
- **The tray alone:** launch or attach to the `kyberdash web` server named in
  `~/.kyberdash/server.json` (`{ pid, url, apiVersion }`), and kill only a server it
  launched itself. Attaching is not hosting.

## What a surface must never do

- **Schedule.** No cadence timer, no "is it due yet" check, no `DEFAULT_CADENCE`. A recurring
  timer in a shell is a scheduler by another name: a surface may *poll* read-only
  `/api/kyber/*` state (above), but it must never **start, schedule or cadence-drive a job**.
  Concretely, in `dash/web/src/**` and `dash/tray/ui/src/**` a `setInterval`, a callback that
  re-arms a timer, a cron-like construct, or a mutating (`POST`/`PUT`/`PATCH`/`DELETE`)
  request issued from inside a timer callback is a violation. A single, non-re-arming
  `setTimeout` — clearing the "copied" badge after two seconds — is a deferred repaint, not
  scheduling, and is allowed.
- **Run a job or spawn a CLI child for one.** No `refresh`, `clean`, `import-history`,
  `settings` or `otel` argv literal, and no process spawn outside the tray's supervisor.
- **Host the OTLP receiver.** Receiver lifetime belongs to the engine's `OtlpCollectorService`.
- **Import server/engine value code.** `dash/web/**` and `dash/tray/ui/src/**` remain pure UI
  clients over `/api/kyber/*`; the TypeScript engine (`dash/src/`) has been removed in favor of
  the C# engine in `dash/server/`. Test files and fixtures in the shells may build deterministic HTTP/API
  fixtures, because they are never shipped.
- **Compute domain results in the browser.** Cost-basis mismatch, token totals, finding
  ranking, coverage — anything the engine decides. A number the UI derives is a number that
  can disagree with the CLI.

## Where the logic lives

| Concern | Owner |
|---|---|
| Scheduled refresh, maintenance pass, manual/import/clean work, pause | `JobSchedulerService` (`dash/server/KyberDash.Application/Services/JobSchedulerService.cs`) |
| The OTLP receiver and HTTP ingestion endpoints | `OtlpCollectorService` (`dash/server/KyberDash.Application/Services/OtlpCollectorService.cs`) and `OtlpEndpointMappings` (`dash/server/KyberDash.Api/Endpoints/OtlpEndpointMappings.cs`) |
| Which process is allowed to schedule at all | the jobs lease on `jobs.lock` (`JobSchedulerService.cs`) |
| Folder import and checkpointed refresh | `CanonRefreshEngine` / `TranscriptIngestionService` (`dash/server/KyberDash.Application/CanonRefreshEngine.cs`, `dash/server/KyberDash.Application/Services/TranscriptIngestionService.cs`) |
| Database clean | `CleanService` (`dash/server/KyberDash.Application/Services/CleanService.cs`) |
| Shared settings (`settings.folder_import.scheduled`, `settings.jobs.paused`, `settings.jobs.refresh_cadence_minutes`, `settings.receiver.hosted`, `settings.dashboard.port`) | `canon.db` metadata via `SettingsService` (`dash/server/KyberDash.Application/Services/SettingsService.cs`) |
| Serving them | `DashEndpointMappings` in the `kyberdash-api` server (`dash/server/KyberDash.Api/Endpoints/DashEndpointMappings.cs`), started by `kyberdash web` (`dash/server/KyberDash.Cli/Program.cs`) |

`JobSchedulerService` and `OtlpCollectorService` run inside the `kyberdash web` process under shared file
and store locking, so background work and manual CLI commands arbitrate cleanly over the same
store. At most one host holds `jobs.lock`, so at most one process schedules.

## Enforcement

Engine behaviour and contract parity live under `dash/server/` and `dash/server.tests/`
(`CanonStoreRepositoryParityTests`, `CanonAnalysisTests`, `CanonIngestTests`,
`CanonSynthTests`, `ApplicationParityTests`, `MinimalApiEndpointTests`, and the
`ArchitectureBoundaryTests` that hold the Clean Architecture layering). Display surfaces under
`dash/web/` and `dash/tray/` are gated
by TypeScript strict checking (`npm --prefix dash run typecheck`), ESLint (`npm --prefix dash run lint`),
reachability (`npm --prefix dash run check:reachable`), UI component tests, and Rust/Tauri checks
for `dash/tray/src-tauri/`. Timer hygiene in the browser remains scoped to read-only store generation
polling in `dash/web/src/lib/storeGeneration.ts`.

If a change requires new domain derivation, folder discovery, or scheduling, put it in the C#
engine and expose it through `/api/kyber/*` — never in `dash/web/` or `dash/tray/`.

## If you are an agent about to edit `dash/tray` or `dash/web`

1. Is the behaviour you are adding something a *surface* decides? If it needs a store, a
   parser, a total, a ranking, or a timer — it belongs in `dash/server/`, reached
   over `/api/kyber/*`.
2. Are you adding a recurring timer, an argv literal, or a `Command::new`? Stop — `JobSchedulerService`
   and `OtlpCollectorService` already own scheduling and ingestion. A read-only poll of `/api/kyber/*`
   for repainting is display work; a timer that starts or re-drives engine work is not.
3. Would the same answer have to be right in the CLI, the dashboard, and the tray? Put it
   in the C# engine once (`dash/server/`).
4. Run `npm --prefix dash run typecheck`, `npm --prefix dash run lint`, `npm --prefix dash run check:reachable`,
   plus `npm --prefix dash run test:related -- <files>` for the files you changed (paths relative
   to `dash/`). The whole dash suite (`npm --prefix dash run test`) is the review council's and CI's;
   no dev agent runs it. When you touched Rust, run the tray's targeted
   `cargo fmt --manifest-path dash/tray/src-tauri/Cargo.toml --check`,
   `cargo clippy --manifest-path dash/tray/src-tauri/Cargo.toml -- -D warnings` and
   `cargo test --manifest-path dash/tray/src-tauri/Cargo.toml '<name>'` from
   [dash/tray/AGENTS.md](../../dash/tray/AGENTS.md). Run each directly from the repository root
   (see [dash/AGENTS.md](../../dash/AGENTS.md#tests-that-start-processes)).

## Related

- [ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md) — the decision this rule
  records
- [ADR 0023](../adr/0023-kyberdash-report-model-and-tray-ownership.md) — the report model
  and Rust-side HTTP this rule keeps; its decision 3 is superseded
- [KyberDash architecture](../dash/architecture.md) — the surface layer, the API contract,
  and the tray
- [Rules index](README.md)
