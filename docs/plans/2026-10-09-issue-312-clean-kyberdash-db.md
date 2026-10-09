---
id: plans/2026-10-09-issue-312-clean-kyberdash-db
title: "Clean the KyberDash database from the web dash and tray (#312)"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-09
development-mode: test-first
---

# Clean the KyberDash database from the web dash and tray (#312)

**Status: Draft — plan approved 2026-10-09 with orchestrator answers to Q-1…Q-10. Implementation not started.**

This plan addresses GitHub issue [#312](https://github.com/dpalfery/kyber-weave/issues/312): KyberDash data is ephemeral point-in-time telemetry with no way to clear bad or stale data (double-counting, mis-attribution, test noise) short of manual database surgery. The deliverable is a "Clean database" capability reachable from both the web dash and the tray, backed by one central engine method, with confirmation, no backups, ingestion paused for the duration, and automatic re-ingestion defaulting to the last 7 days.

**Development mode:** `test-first`. Every implementation task defines failing automated tests before modifying production code.

**Branch:** `dp-kyber-bot.grokbot.commandcode/issue-312-clean-kyberdash-db` from `main`, kept current by merging trunk in (never rebase or force-push).

---

## 1. Design

One central engine module — `dash/src/clean/clean.ts`, exporting `cleanDatabase(store, options, ports)` — holds all wipe and re-ingest logic. Two thin entry points call it:

1. **CLI** `kyberdash dash clean --db <path> (--all | --harness <id>...) [--reingest-weeks <n> | --no-reingest] --yes`, registered in `dash/src/cli/register.ts` beside `dash refresh`. Takes `acquireStoreRefreshLock` (exit 3 = busy, reusing `REFRESH_BUSY_EXIT_CODE`), exit 2 for usage errors before the store opens, 0/1 for success/failure. The tray spawns this exactly as it spawns `dash refresh` today (`REFRESH_ARGS`, `dash/tray/src-tauri/src/scheduler.rs`); exit 3 already renders as busy (`scheduler.rs`, `RefreshOutcome`).
2. **HTTP** `POST /api/kyber/clean` in `dash/src/server/routes.ts`, following the model-catalog-refresh pattern (exact path, 405 for non-POST, buffered body, `sendKyberJson`). The browser SPA can reach the engine only over HTTP. Body: `{ all?: boolean, harnesses?: string[], reingestWeeks?: number, confirm: true }`. Responses: 200 summary, 400 invalid body/unknown harness/no scope, 409 lock busy.

The production web-server bridge handle is read-only (`dash/src/server/bridge.ts`); the clean route opens a short-lived read-write `CanonStore(canonPath)` for the duration of the operation only — the same code the CLI runs — then closes it. No bridge-handle change.

**Ingestion pause:** new loopback admin routes on the OTLP receiver (`dash/src/otel/receiver.ts`, beside `/healthz`): `POST /v1/admin/pause {leaseMs}` and `POST /v1/admin/resume`. While paused, `/v1/traces` and `/v1/logs` return 503 + Retry-After (OTLP exporters retry, no data loss); `/healthz` stays 200 with a `paused` state so the tray's probe still classifies the receiver as hosted (`dash/tray/src-tauri/src/receiver.rs`). A lease TTL (default 10 min) auto-resumes if the cleaner crashes. A new `dash/src/clean/pause.ts` port probes healthz, verifies `service == "kyberdash-otlp"`, pauses, runs the clean, and resumes in a `finally`. If the receiver is ours but pause fails, the clean fails closed (exit 1 / 500). If the port is free or held by a foreign service, the clean proceeds under the refresh lock. The pause is load-bearing: the ingest writer has no `onError` (`dash/src/otel/service.ts`), so a flush colliding with the wipe transaction past the 5 s busy timeout rethrows through `setImmediate` and kills the receiver (`dash/src/otel/writer.ts`).

**Wipe semantics:** new `CanonStore.wipeHarnesses(canonicalIds)` and `wipeAll()` in `dash/src/canon/store.ts`, using the hand-rolled BEGIN/COMMIT/ROLLBACK idiom (no shared helper exists). One transaction deletes: `records` (canonical ids expanded to raw stored names via `normalizeHarnessName`, `dash/src/canon/measurability.ts` — e.g. `claude-desktop` → `claude-code`, `cursor-agent` → `cursor`), `record_provenance` and `source_checkpoint` (both keyed by `harness_id`, `dash/src/canon/source-state.ts`), the derived `session`/`run`/`execution`/`harness_rollup`/`problems` rows (harness-keyed), and `prediction` rows (via wiped `run_id`s). `finding` has no harness column; it is cleared by the post-wipe `projectCanonicalStore` pass (`dash/src/canon/projection.ts`), the sole full projection. Wipe-all additionally clears `quarantine`, `pending_logs`, `quarantined_logs`, and `enriched_logs`. Kept in all cases: `ingest_log`, `refresh_run` (operational audit), `metadata` (stamped with `last_clean_at` and scope), `token_cache`, and the model-window catalog. Per-harness wipe cannot scope `quarantine` rows (no harness column); this is disclosed in the confirm UI. No schema change.

**Re-ingestion:** automatic, inside the clean, under the same refresh lock: `refreshHarnessSources(store, descriptors, { historyWeeks })` with `historyWeeks` defaulting to **1** (last 7 days; the window is `[now − N×7 days, now]`, `dash/src/refresh/source-reader.ts`). `--reingest-weeks <n>` / `reingestWeeks` opts further back; `--no-reingest` skips. Per-harness cleans pass only the wiped harnesses' descriptors (the `descriptors` dependency seam already narrows, `orchestrator.ts`). Both surfaces disclose that OTLP-collected records have no source logs and will not return (honest unobservability, `docs/rules/honest-unobservability.md`).

**Confirmation UX:** Web — a "Clean database" control in `CoverageIngestPanel` (`dash/web/src/pages/ContextDoctor.tsx`, beside `ModelCatalogRefreshControl`) opening a dialog built on the `SessionInspectorDrawer` accessibility pattern (backdrop, `role="dialog"`, `aria-modal`, Escape), showing scope, record/session counts, and fixed disclosures (no backup, irreversible, OTLP data will not return); remote error strings are never rendered (the `refreshModelWindows` convention, `dash/web/src/lib/kyberApi.ts`). Tray — an inline two-step confirm (button → "Confirm clean"/"Cancel") in the settings view; no dialog plugin exists and none is added; errors surface via the existing `ipc-action-error` banner. The tray offers clean-all and clean-the-currently-selected-harness (multi-select stays web-only). CLI — `--yes` required; exit 2 without it, before the store opens.

**No backups, ever** (issue letter; backup-before-clean rejected in the issue's alternatives). Every confirm surface discloses irreversibility instead.

**ADR 0032** (`docs/adr/0032-kyberdash-user-initiated-clean.md`) records the user-initiated clean and narrowly supersedes ADR 0016 D6 ("do not rewrite or delete `records.raw`" / "never auto-deletes history") and ADR 0018 D3 ("never delete `records.raw`") for the explicit, user-confirmed clean only. ADRs 0016 and 0018 are not edited. The record is added to `docs/adr/README.md`.

---

## 2. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D1 | Central method surface (issue open item 1: HTTP endpoint, CLI, or both) | One engine module `dash/src/clean/clean.ts`; both `kyberdash dash clean` and `POST /api/kyber/clean` are thin callers | The web SPA can only reach the engine over HTTP; scripts and recovery need the CLI. Both delegate to `cleanDatabase`. | Orchestrator answer to Q-1 (C), plan approval 2026-10-09. Source: issue #312 "Single entry point: one central API and/or CLI method does the work. The web dash and the tray are both thin callers." |
| D2 | Whether tray and web share one endpoint | Tray spawns the `dash clean` CLI child exactly like Refresh now; it does not POST the HTTP route | Established tray pattern (scheduler `REFRESH_ARGS`, exit 3 busy already modeled); works during web-child restart backoff. The tray holds zero clean logic. | Orchestrator answer to Q-2 (B), plan approval 2026-10-09. Sources: [ADR 0023](../adr/0023-kyberdash-report-model-and-tray-ownership.md) D3/D4; issue #312 "The tray is an interface only ... holds no clean logic." |
| D3 | Exactly what "clean" deletes | Data-bearing tables for the scope; keep `ingest_log`, `refresh_run`, `metadata` (stamp `last_clean_at`/scope), `token_cache`, model-window catalog; wipe-all additionally clears `quarantine` and log-enrichment tables | `finding` has no harness column — cleared by the post-wipe projection. Per-harness wipe cannot scope `quarantine` rows (no harness column); disclosed in the confirm UI. | Orchestrator answer to Q-3 (A), plan approval 2026-10-09. Source: issue #312 Problem (target is "bad or stale data", not operational audit). |
| D4 | Ingestion pause mechanism | In-band receiver pause: admin routes, 503 + Retry-After while paused, healthz stays 200 with `paused:true`, lease-TTL auto-resume, resume in `finally`, fail closed if our receiver cannot pause | Load-bearing, not cosmetic: the writer path (`service.ts` / `writer.ts`) can crash the receiver on a busy-timeout collision with the wipe transaction. OTLP exporters retry on 503, so no data is lost. | Orchestrator answer to Q-4 (A), plan approval 2026-10-09. Sources: issue #312 "pause ingestion for the duration of the clean, then resume"; `dash/src/otel/service.ts`, `dash/src/otel/writer.ts` crash path. |
| D5 | Confirmation UX | Explicit confirm on every surface (web dialog, tray two-step, CLI `--yes`) with no-backup / irreversible / OTLP-won't-return disclosures | The data is re-derivable for file-backed harnesses, so typed-phrase confirmation over-weights; skipping confirmation contradicts the issue. | Orchestrator answer to Q-5 (A), plan approval 2026-10-09. Source: issue #312 "confirmation dialog before delete". |
| D6 | Checkpoint reset (issue open item 3) | Delete `source_checkpoint` + `record_provenance` for the wiped scope in the same transaction as the records | The exact inverse of `commitSourceUnit`. Invalidate-only preserves stale coverage floors (`checkpointIsReusable`) and surviving same-spanId rows are skipped (`orchestrator.ts`), so only deletion makes the 7-day re-ingest window apply. | Orchestrator answer to Q-6 (A), plan approval 2026-10-09. Sources: issue #312 open item 3; [ADR 0016](../adr/0016-kyberdash-harness-source-refresh.md) D6 schema-11 checkpoints. |
| D7 | Backup before clean | No backup, ever; every confirm surface discloses that OTLP-collected records are permanently gone | A sidecar would invite "restore from unknown schema" paths; the issue explicitly rejects backup-before-clean. | Orchestrator answer to Q-7 (A), plan approval 2026-10-09. Sources: issue #312 "No backup or snapshot: the data is ephemeral and backups are not needed"; Alternatives "Backup-before-clean with restore: rejected". |
| D8 | Re-ingestion trigger (issue open item 2) | Automatic, inside the clean, under the same refresh lock; default `historyWeeks = 1` (last 7 days); opt-in deeper via `--reingest-weeks` / `reingestWeeks`; `--no-reingest` to skip | Leaving surfaces empty after "clean" invites re-filed bugs. The web POST path must handle the long-running request sensibly (no hang without feedback in the UI). | Orchestrator answer to Q-8 (A), plan approval 2026-10-09. Source: issue #312 "Re-ingestion defaults to the last 7 days; the user can opt in to go further back" + "pause ... then resume". |
| D9 | Durability of the decisions | New ADR 0032 (narrowly superseding ADR 0016 D6 and ADR 0018 D3 for the explicit user-confirmed clean only) + this governed plan doc (archived by the finishing PR per KW-DOC-LIFECYCLE-003) + harvest into `docs/dash/architecture.md` and `docs/dash/runbook.md` | The wipe deletes `records` rows including `records.raw`, which runs against ADR 0018 D3 and ADR 0016 D6; per `docs/adr/README.md`, a decision that changes is recorded in a new ADR that supersedes the old one, following the ADR 0023 precedent. ADRs 0016/0018 are not edited. | Orchestrator answer to Q-9 (B), plan approval 2026-10-09. |
| D10 | Tray scope options | Tray offers clean-all and clean-the-currently-selected-harness; multi-select stays web-only | Cheap: the tray already persists `settings.harness`. Matches "wipe by harness (one or more), or wipe all" on both surfaces. | Orchestrator answer to Q-10 (B), plan approval 2026-10-09. Source: issue #312 scope. |

---

## 3. Tasks (test-first)

- **T1 — Store wipe (RED).** `dash/src/canon/store.test.ts` (or new `dash/src/clean/store-wipe.test.ts`): wipe-harness removes records + provenance + checkpoints including folded raw ids (`claude-desktop` under `claude-code`); other harness untouched; wipe-all table map (keeps `ingest_log`/`refresh_run`/`metadata`/`token_cache`/catalog); rollback on injected failure (pattern `store.test.ts:1078-1105`); `:memory:`/temp stores only, never the home store.
- **T2 — Store wipe (GREEN).** `CanonStore.wipeHarnesses` / `wipeAll` in `dash/src/canon/store.ts`; no schema change.
- **T3 — Clean orchestration (RED).** `dash/src/clean/clean.test.ts` + `pause.test.ts`: order pause → wipe → re-ingest (1 week default) → resume; resume in `finally` on failure; fail-closed when our receiver cannot pause; proceed when 4318 free/foreign; `--no-reingest` skips; per-harness passes narrowed descriptors.
- **T4 — Clean orchestration (GREEN).** `dash/src/clean/clean.ts` + `dash/src/clean/pause.ts`.
- **T5 — Receiver pause (RED + GREEN).** Receiver tests: paused state; 503 + Retry-After on `/v1/traces` while paused; resume; lease expiry (fake timers); healthz stays 200 with `paused`. Implementation in `dash/src/otel/receiver.ts`.
- **T6 — CLI (RED + GREEN).** `dash/src/cli/register.test.ts`: exit 2 usage before store opens (`--all` + `--harness` conflict, no scope, missing `--yes`); exit 3 when the lock is held with nothing written; success via injected deps. Update `cli-commands.test.ts` command list. Implementation in `dash/src/cli/register.ts`.
- **T7 — Server route (RED + GREEN).** 200 happy path on a temp `canon.db` (rows gone on disk; bridge serves empty after reconcile); 400/405/409 envelopes. Implementation: `POST /api/kyber/clean` in `dash/src/server/routes.ts` + bridge `cleanDatabase()`.
- **T8 — Web UI (RED + GREEN).** `CleanDatabase.test.tsx` (RED-first, `ModelWindowCatalogRefresh.test.tsx` pattern): dialog opens, disclosures present, confirm POSTs the correct body, in-flight disable, 409 busy and failure states without remote strings. New client fn in `dash/web/src/lib/kyberApi.ts` and control in the Context Doctor ingest panel; keep `check:reachable` green.
- **T9 — Tray (RED + GREEN).** Contract tests: authorized commands exactly seven; `clean_database` spawns the expected argv (fake runner); exit 3 → busy message; capability JSON updated. UI tests: two-step confirm, `invoke('clean_database')`, rejection → `ipc-action-error` banner.
- **T10 — Docs and closeout.** ADR 0032; `docs/dash/architecture.md` + `docs/dash/runbook.md` harvest; `docs validate` and `docs drift` zero findings; archive this plan in the finishing PR per KW-DOC-LIFECYCLE-003; full gate suite (`review gates . --out artifacts/gates.json`) green; inner-loop review; push and open the draft PR.

---

## 4. Closeout mapping

Durable decisions D1–D10 above are harvested on completion: D1–D8 (clean semantics, pause protocol, wipe table map, confirm contract, checkpoint deletion, re-ingest window) into `docs/dash/architecture.md` and operator-facing usage into `docs/dash/runbook.md`; D9 is ADR 0032 itself plus this plan's Decisions table. D10 (tray scope) is recorded here and in the runbook. No documentation waiver: the clean is a new user-facing capability with surviving operational invariants.
