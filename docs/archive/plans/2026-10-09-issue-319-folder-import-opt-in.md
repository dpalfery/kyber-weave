---
id: plans/2026-10-09-issue-319-folder-import-opt-in
title: "KyberDash folder import becomes opt-in, and the tray and web dashboard become pure display layers over one shared application (#319)"
doc-type: plan
status: complete
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-10
development-mode: test-first
---

# KyberDash folder import becomes opt-in, and the tray and web dashboard become pure display layers over one shared application (#319)

**Status: Complete 2026-10-10.** Approved at revision 3 on 2026-10-10 (decision AP1, **approve and execute**), then executed to completion: tasks T1–T23 all delivered, each task audited against its contract row, and the whole change reviewed by four lenses with the findings fixed. Durable decisions are harvested — see [Closeout](#10-closeout). Decisions R1, D1–D5 and Q2–Q6 are recorded below; Q1 is superseded by R1. Archived per KW-DOC-LIFECYCLE-003.

**Development mode:** `test-first` (default; no opt-out supplied). Every implementation task has a RED contract test that fails before production code changes.

**Issue:** [#319](https://github.com/dpalfery/kyber-weave/issues/319). It replaces the re-ingest item of [#318](https://github.com/dpalfery/kyber-weave/issues/318). Revision 2 adds the user's display-layer rule (R1) and the scope that follows from it: move scheduling and jobs out of the tray, give the web dashboard the same Pause / Refresh / wipe / import controls through the same API, find why a tray wipe left history visible in the dashboard, and make the rule impossible for an agent to miss.

**Branch:** created by the conductor from `main` and kept current by merging trunk (never rebase or force-push).

> **DISPLAY-LAYER RULE (R1, non-negotiable).** The tray and the web dashboard are **display layers**. Neither contains isolated feature logic, schedulers or jobs. **All** logic and jobs — refresh scheduling, clean, import, pause — live in the shared KyberDash application (the CLI engine and the `kyberdash web` server built from it) that both surfaces share. Both surfaces use **one datastore** (`canon.db`) and **one API layer** (`/api/kyber/*`). Every task in this plan, and every agent executing it, conforms to this rule.

---

## 1. Problem and goal

**Problem 1 — folder history comes back after a wipe.** On `main` (`c0399ca`), two paths pull folder history back into `canon.db` right after a user wipes it:

1. **Immediate.** `cleanDatabase` (`dash/src/clean/clean.ts:74-76`) defaults `reingestWeeks` to `DEFAULT_CLEAN_REINGEST_WEEKS = 1`. The CLI (`dash/src/cli/register.ts:409-414`) and `POST /api/kyber/clean` (`dash/src/server/routes.ts:1153`, `KyberBridge.cleanDatabase` at `dash/src/server/bridge.ts:3783`) therefore re-import a week of folder history by default. The web dialog (`dash/web/src/components/maintenance/CleanDatabaseControl.tsx:68-70`) never sends `reingestWeeks`, so it always re-imports.
2. **Scheduled.** The tray's `Scheduler` (`dash/tray/src-tauri/src/scheduler.rs`) runs `REFRESH_ARGS = ["dash", "refresh"]` at startup and every 5 minutes (`DEFAULT_CADENCE`, `scheduler.rs:20`). No `--trigger` is passed, so every tray run imports two weeks of every folder source (`DEFAULT_HISTORY_WEEKS = 2`, `dash/src/refresh/orchestrator.ts:42`). The tray clean passes `--no-reingest` (`runtime.rs:78-93`), but the next tick re-imports anyway, as [ADR 0032](../../adr/0032-kyberdash-user-initiated-clean.md) Consequences records.

**Problem 2 — the tray holds a scheduler and jobs (R1 violation).** See [§3.2](#32-tray-and-web-feature-logic-audit-scope-item-2). The scheduler, the refresh worker, the cadence setting and the OTLP receiver hosting all live in the tray's Rust core, and the tray drives jobs by spawning CLI children instead of calling the shared API. [ADR 0023](../../adr/0023-kyberdash-report-model-and-tray-ownership.md) decision 3 and the runbook currently codify this.

**Problem 3 — a tray wipe still shows history in the dashboard.** See [§3.1](#31-datastore-and-api-sharing-scope-item-1).

**Goal.**

- No automatic folder import, either immediately after a wipe or on a schedule. This is the default for everyone (D1).
- Folder import becomes an explicit opt-in in two ways (D3): a one-off **Import folder history** action with a week window (default 1, max `MAX_CLEAN_REINGEST_WEEKS` = 52), standalone and inside the clean flow; and a separate shared setting that enables recurring scheduled folder refresh.
- **One shared application owns every job** (R1, Q4): the refresh scheduler, the folder-import gate, the maintenance pass, pause, clean and import. The tray and the web dashboard only display its state and invoke its API.
- **One datastore, one path resolver, one API** (R1, A13): every process resolves `canon.db` the same way; every surface action goes through `/api/kyber/*`.
- The web dashboard gains the controls the tray has: **Pause / Resume**, **Refresh data**, wipe all or per harness, **Import folder history**, and the shared settings — all through the same API calls the tray makes.
- After a wipe from either surface, every open dashboard tab and the tray stop showing pre-wipe data (A14, A15).
- The display-layer rule is stated prominently in the canonical docs and every agent-facing instruction file that governs `dash/` work, and a guard test fails the build when a surface regains feature logic (A17, A18).

**Done when:**

- After a wipe from any surface, the store holds only new OTel data across several scheduled job-host intervals, and no open tab or tray view still shows pre-wipe records or a pre-wipe coverage window.
- The tray crate has no scheduler, no refresh worker, no receiver hosting and no CLI-child job spawns; its only process duty is launching or attaching to the shared server (Q4).
- Pause, Refresh, wipe (all and per harness), import and the shared settings work identically from the tray and the web, through the same routes.
- The display-layer guard test (T21) passes, and fails when a scheduler, a feature-command spawn or an engine-logic import is reintroduced into a surface.
- Docs: ADR 0032 and ADR 0016 are edited in place (Q3); the display-layer rule is recorded in new ADR 0033 (superseding ADR 0023 decision 3 and its no-shared-server Consequence) and the governed rule doc `docs/rules/kyberdash-display-layer.md` (Q6); the rule callout appears in `docs/dash/architecture.md`, the rules index, root `AGENTS.md`, `dash/AGENTS.md` and `dash/tray/AGENTS.md`.

**Residual intake gap.** The planner had no shell, so `gh issue view` could not run and no live state of the user's machine (environment variables, running processes, the real `canon.db`) could be read. The text of issues #319 and #318 was retrieved through WebFetch as a summary in revision 1. The §3.1 root cause is therefore established from code, not from the user's machine; T22 reproduces it on a temp store.

---

## 2. Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| R1 | Architecture rule for KyberDash surfaces | **The tray and the web dashboard are display layers.** Neither contains isolated feature logic, schedulers or jobs. All logic and jobs (refresh scheduling, clean, import, pause) live in the shared CLI / root application that both surfaces share. Both use one datastore and one API layer. No agent or design may violate this. | User's own words, stated as non-negotiable. It supersedes revision 1's tray-scheduler framing (Q1, A3, A5, A8) and conflicts with ADR 0023 decision 3 ("The tray owns the server, the scheduled refresh and the receiver"), which Q6 resolves. | ANSWERED. User decision via conductor, 2026-10-09. |
| D1 | Default scope of "folder import off" | Folder import is OFF by default for everyone, not only after a wipe. Absent setting = off. | Behavior change on upgrade: existing users stop getting scheduled folder refreshes until they enable the setting. The runbook and release note say so (T23). | ANSWERED. User decision via conductor, 2026-10-09. |
| D2 | Where the shared setting lives | canon.db `metadata` table (`CanonStore.getMetadata` / `setMetadata`, `dash/src/canon/store.ts:3279-3291`) | `wipeAll` keeps `metadata` (`store.ts:2084-2124`), so a wipe never resets the user's choice. No schema change. Extended by A1/A16 to every shared setting. | ANSWERED. User decision via conductor, 2026-10-09. |
| D3 | Opt-in model | Both: (1) a one-off import action with a week window (default 1, max `MAX_CLEAN_REINGEST_WEEKS`), and (2) a separate setting that enables recurring scheduled folder refresh. The one-off action never changes the setting. | Answers the issue's open items "time-bounded?" (yes, for the one-off) and "does enabling resume scheduled refreshes?" (only the separate setting does). | ANSWERED. User decision via conductor, 2026-10-09. |
| D4 | Does the setting gate the manual CLI? | No. A manual CLI `dash refresh` (trigger `cli`) always runs folder sources. Every refresh started by the shared job host, the tray or the web dashboard consults the setting (Q2, A3). | Keeps ADR 0016 D1's public command contract intact for terminal users and scripts. | ANSWERED. User decision via conductor, 2026-10-09; wording aligned to Q2 and R1. |
| D5 | Development mode | `test-first` | Default. No explicit user opt-out was supplied. | ANSWERED. Default per plan contract; conductor confirmed, 2026-10-09. |
| Q1 | When the setting is off, should the tray's scheduled refresh still run? | **Superseded by R1.** The tray no longer schedules anything. The surviving technical question — what a scheduled tick does while folder import is off — is answered by A4 inside the shared job host. | The user reframed Q1 into R1. Revision 1's evidence still holds and drives A4: `purgeExpiredContent` has exactly one production caller, inside `refreshHarnessSources` (`orchestrator.ts:138`), so dropping scheduled ticks entirely would stop the [ADR 0018](../../adr/0018-kyberdash-content-retention-purge.md) 14-day purge. | SUPERSEDED. User reframing via conductor, 2026-10-09. |
| Q2 | Does the tray's **Refresh now** honor the setting, or always import like the CLI? | **(a)** Every tray-started refresh — `--trigger tray` and `--trigger scheduled` — follows the folder-import setting. The explicit **Import folder history** action is the only surface path that imports while it is off. | Under R1 the web dashboard's **Refresh data** is the same action through the same route, so it follows the setting too (A3). The bare CLI stays unconditional (D4). | ANSWERED. User decision via conductor, 2026-10-09. |
| Q3 | How is the folder-import decision recorded durably? | **(b)** Edit [ADR 0032](../../adr/0032-kyberdash-user-initiated-clean.md) in place; **no new ADR for this change.** Also edit [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md) in place where it contradicts the new model. | **Rationale recorded for the in-place edit:** the user prefers one current record per subject over a supersession chain for a narrow default change. Repository precedent (ADR 0023, ADR 0032 superseding single sentences) is deliberately not followed here. Mitigation: each edited ADR gets a dated "Amended 2026-10-xx by [this plan]" line in its Status section naming what changed (ADR 0032 D1, D6 and the tray Consequence; ADR 0016 D1's refresh-trigger sentence and the rejected "UI-triggered refresh before the CLI contract exists" alternative), and this plan's archived Decisions table keeps the prior wording discoverable alongside git history. ADR 0032's title drops "Automatic Re-Ingest". This answer covers the folder-import change only; the display-layer rule is Q6. | ANSWERED. User decision via conductor, 2026-10-09. |
| Q4 | Where do scheduling, jobs and receiver hosting live, and what may the tray still do? | **(a)** The `kyberdash web` server process is the shared root application: it hosts the job host (cadence, startup run, pause, folder-import gate, maintenance pass), runs each job as a child of the same binary under the existing refresh lock, and hosts the OTLP receiver. The tray only launches or attaches to that one process. Options considered: **(a)** the `kyberdash web` server process is the shared root application. It hosts the job host (cadence, startup run, pause, folder-import gate, maintenance pass), runs each job as a child of the same binary under the existing refresh lock, and hosts the OTLP receiver. The tray's only non-display duty is launching or attaching to that one process. **(b)** As (a), but receiver hosting stays in the tray for now and moves in a follow-up todo. **(c)** A new dedicated background process (`kyberdash daemon`) owns jobs and the receiver; the web server and the tray both talk to it. | **Recommended: (a).** It satisfies R1 in one change: one process owns every job and service, and both surfaces call the same `/api/kyber/*`. Under (b) a known R1 violation ships (receiver supervision, health probing and retry backoff stay in `receiver.rs`). Under (c) there is a third process, a second API boundary and a new IPC contract for no user-visible gain. Under (a), something still has to start the server when only the tray is running; launching or attaching to it is a launcher duty, not feature logic, and the rule doc says so explicitly. A plain `kyberdash web` without the tray also runs scheduled jobs, so the behavior is the same whichever surface the user starts. | ANSWERED. User decision via conductor, 2026-10-09 (chose recommended option a). |
| Q5 | What does **Pause** pause? (The tray has no Pause button on `main`; both surfaces gain one.) | **(a)** Pause pauses scheduled jobs only. While paused the job host starts no scheduled refresh until Resume; manual Refresh, Import and Clean still run; OTLP ingestion continues; the content-retention purge keeps running. Persisted as `settings.jobs.paused` in canon.db. Options considered: **(a)** pause scheduled jobs: the job host starts no scheduled refresh until Resume; manual Refresh, Import and Clean still work; OTLP ingestion continues; the content-retention purge keeps running. **(b)** pause everything: scheduled jobs and OTLP ingestion (the receiver sheds with 503 until Resume). **(c)** pause OTLP ingestion only. | **Recommended: (a).** It is lossless and persistent (a canon.db setting survives restarts and wipes). Under (b) and (c) an open-ended receiver pause outlives exporter retry budgets, so telemetry is dropped; the existing pause port is lease-bound (10 minutes, `CLEAN_PAUSE_LEASE_MS`) precisely to avoid that. Keeping the purge running under (a) preserves ADR 0018 retention while paused. Note: #318 lists a "pause removal" item; under (a) the clean's receiver pause is untouched, so there is no overlap. | ANSWERED. User decision via conductor, 2026-10-09 (chose recommended option a). |
| Q6 | How is the display-layer rule (R1) recorded? | **(a)** New ADR 0033 "KyberDash surfaces are display layers over one shared application", superseding ADR 0023 decision 3 and its Consequence "does not share a server with a dashboard the user started by hand", plus the governed rule doc `docs/rules/kyberdash-display-layer.md` carrying the always-on statement. ADR 0023 receives only the supersession status line. Options considered: **(a)** a new standalone ADR 0033 "KyberDash surfaces are display layers over one shared application" that supersedes ADR 0023 decision 3 and its Consequence "does not share a server with a dashboard the user started by hand", **plus** a governed rule doc `docs/rules/kyberdash-display-layer.md` carrying the always-on statement; **(b)** the rule doc plus an in-place edit of ADR 0023; **(c)** an in-place edit of ADR 0023 only. | **Recommended: (a).** R1 reverses an accepted architectural decision (tray ownership) that constrains all future surface work and was expensive to reach; that is what an ADR records, and Q3's "no new ADR" was scoped to the folder-import default. The rule doc is where an agent looks for always-on guidance ([rules index](../../rules/README.md)); the ADR records why. Under (c) the rule exists only inside a decision record, which agents do not load for routine `dash/` edits. ADR number 0033 confirmed unused on 2026-10-09. | ANSWERED. User decision via conductor, 2026-10-09 (chose recommended option a). |
| A1 | Shared setting keys and encoding | canon.db `metadata` keys, read and written only through the central settings module (A2): `settings.folder_import.scheduled` (`on`/`off`, default off), `settings.jobs.paused` (`on`/`off`, default off), `settings.jobs.refresh_cadence_minutes` (integer 1–1440, default 5), `settings.receiver.hosted` (`on`/`off`, default = today's tray default; read by the server's receiver host per Q4). Absent or unrecognized values read as the default; folder import fails closed to off. | One store, one key namespace, survives wipes (D2). A malformed folder-import value fails closed, the safe direction for a privacy- and size-relevant import. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A2 | The central modules every surface calls | `dash/src/settings/shared-settings.ts` (typed read/write/validate of A1 keys); `dash/src/refresh/folder-import.ts` (`folderSourcesAllowed(trigger, store)`, `importFolderHistory(store, { weeks?, harnesses?, trigger }, deps)`, `runMaintenancePass(store, now)`); `dash/src/jobs/host.ts` (`JobHost`: cadence, startup run, pause, status, single-host lease); `dash/src/canon/paths.ts` (`resolveCanonDbPath`). CLI commands and HTTP routes are thin callers. | `importFolderHistory` validates weeks (1..`MAX_CLEAN_REINGEST_WEEKS`, default 1) and calls `refreshHarnessSources` with narrowed descriptors (the `portsForClean` pattern). `runMaintenancePass` runs `purgeExpiredContent` then `projectCanonicalStore`. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A3 | Refresh triggers | `REFRESH_TRIGGERS` gains `web`. The job host spawns `dash refresh --trigger scheduled`; `POST /api/kyber/refresh` spawns `--trigger tray` or `--trigger web` from the request's `surface`. `folderSourcesAllowed` returns true for `cli` and the setting for `scheduled`, `tray` and `web` (Q2, D4). | No new CLI flag. The run log names the real initiator. #318's "trigger labels for web-initiated runs" item shrinks to the clean/import paths this plan does not relabel. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A4 | What a folder-skipped refresh does and records | It holds the refresh lock, runs `runMaintenancePass`, writes **no** `refresh_run` row, prints one line naming the skip and how to enable it, and exits 0. A scheduled tick while paused (Q5) runs only the maintenance pass. | Keeps the ADR 0018 purge alive (Q1 evidence). A skipped run that recorded a window would falsely claim folder coverage ([honest unobservability](../../rules/honest-unobservability.md)). | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A5 | How the tray reads state and invokes actions | **Only through the loopback HTTP API**, using the Rust core's existing `reqwest` client (ADR 0023 D2). No CLI children for jobs. The tray renders `GET /api/kyber/report`, `GET /api/kyber/jobs` and `GET /api/kyber/settings`; a fetch failure renders "unknown", never a default. | R1 "one API layer". The server's guard admits a loopback request with no `Origin` header (`dash/src/cli/web.ts:271-278`), so native POST/PUT from Rust need no allowlist change. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A6 | CLI surface | **`dash import-history`** `[--weeks <n>]` (default 1, max 52) `[--harness <id...>]`: takes the refresh lock; exits 2 on usage errors before the store opens, 3 when busy, 1 on failure, 0 on success; never changes the setting. **`dash settings [show\|set <key> <value>] [--json]`** reads or writes A1 keys through the central module. **`dash clean`** no longer re-imports by default; `--reingest-weeks <n>` opts in; `--no-reingest` stays accepted as a no-op. **`dash refresh`** gains the trigger gate (A3/A4). | Exit codes mirror `REFRESH_BUSY_EXIT_CODE`. One generic `dash settings` command replaces revision 1's `dash folder-import`, because pause and cadence are shared settings too. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A7 | HTTP surface (the one API both surfaces use) | `GET /api/kyber/settings` → all A1 values; `PUT /api/kyber/settings` (partial body, validated, 200/400). `GET /api/kyber/jobs` → `{ refresh: { state, lastSuccessAt, lastFailure, nextDueAt }, paused, storeGeneration }`. `POST /api/kyber/refresh` `{ surface: 'tray'\|'web' }` → 202 started / 409 busy. `POST /api/kyber/import-history` `{ weeks?, harnesses? }` → 202 / 400 / 409. `POST /api/kyber/clean` unchanged in shape; omitting `reingestWeeks` now means no import. | Same body cap, parse-then-act and "no remote error strings in the page" conventions as `/api/kyber/clean`. Refresh and import return 202 and run as job-host children, so a long import never holds an HTTP request open (retires revision 1's long-request risk). Writes use short-lived read-write `CanonStore` handles; the bridge handle stays read-only. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A8 | Clean with **Import folder history** checked | Both surfaces send one `POST /api/kyber/clean` with `reingestWeeks: n`; the server runs the import inside the clean through `importFolderHistory`, narrowed to the cleaned scope. The standalone action is `POST /api/kyber/import-history`. | Revision 1's tray-side chaining (`clean` child then `import-history` child) is withdrawn: it was sequencing logic in the tray. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A9 | Scope of the one-off import | The standalone action imports all harnesses unless `harnesses` is given; the CLI also accepts `--harness`. The import inside a clean is narrowed to the cleaned scope. | Matches the existing clean re-ingest narrowing (`portsForClean`). | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A10 | Behavior when scheduled folder import is turned on | Job-host and surface refreshes resume today's model: incremental, checkpointed, 2-week window (`DEFAULT_HISTORY_WEEKS`). Both surfaces disclose that the next tick imports up to two weeks. | No new window knob; a deeper one-off pull is the import action's job (D3). | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A11 | How the job host runs a job | As a child process of the same binary (`process.execPath`, plus the entry script in a source checkout), e.g. `dash refresh --trigger scheduled`, observing exit 0/1/2/3. The server keeps serving while the child parses. Children are killed on server shutdown. | Process isolation keeps a CPU-heavy parse off the server's event loop, and reuses the refresh lock and exit-code contract unchanged. The child is the same CLI the user can run, so there is still one implementation. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A12 | One job host, one server | The server writes `~/.kyberdash/server.json` (`pid`, `url`, `apiVersion`) when listening and removes it on exit. The tray attaches to a live, version-compatible server named there before launching its own. Only one process runs scheduled jobs: the job host takes a PID-liveness lease `~/.kyberdash/jobs.lock` (same mechanism as `refresh.lock`); a second server serves the API but reports `jobs.hostedElsewhere`. | Retires ADR 0023's Consequence "it does not share a server with a dashboard the user started by hand": both surfaces now share one API process. Two schedulers can never double-run even if two servers start. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A13 | One path resolver (RC4) | `resolveCanonDbPath(explicit?)` = explicit `--db` ?? `KYBER_CANON_DB` ?? `~/.kyberdash/canon.db`, used by `register.ts` `resolveDbPath`, `otel/service.ts:97-99`, `KyberBridge` (`bridge.ts:1001-1005`) and `cli/report.ts`. | Today the bridge honors `KYBER_CANON_DB` but the CLI and the receiver ignore it, so the dashboard can read a different file than the tray wipes. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A14 | Open views learn about a wipe (RC2) | `GET /api/kyber/jobs` carries `storeGeneration` (from `last_clean_at` plus the records high-water mark). The web app polls it (10 s) and calls `queryClient.invalidateQueries()` when it changes; the tray repaints from its next poll. | Today only the web's own clean invalidates its cache (`CleanDatabaseControl.tsx:74`); a tray wipe leaves an open tab on pre-wipe data (`staleTime: 30_000`, `refetchOnWindowFocus: false`, `dash/web/src/main.tsx:36`). Polling and repainting are display duties. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A15 | Coverage after a wipe (RC3) | The coverage read (`bridge.ts:2405-2461`) ignores `refresh_run` rows that started before `last_clean_at`, so a wiped store reports no folder window instead of the pre-wipe one. `ingest_log` and `refresh_run` themselves are still never wiped (ADR 0032 D8). | Honest unobservability: the footer must not claim coverage of data that was deleted. This gives `last_clean_at` a reader, which overlaps #318's "unread `last_clean_at` stamp" item; #318 should now keep the stamp. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A16 | Settings that leave the tray | Refresh cadence and receiver hosting move from the tray's JSON `TraySettings` to canon.db (A1). The tray JSON keeps display preferences only (harness selection, window days). Existing tray cadence values are not migrated: cadence resets to 5 minutes, disclosed in the release note. | A one-time migration would be tray-side logic for a value most users never changed. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A17 | Enforcement of R1 | A guard test `dash/src/architecture/display-layer.test.ts` (Vitest, runs in `npm --prefix dash run test`) scans the surfaces and fails on: in `dash/tray/src-tauri/src/**`, a `scheduler` module or cadence timer, any process spawn other than the server launcher, or argv literals for `refresh`, `clean`, `import-history`, `settings` or `otel`; in `dash/tray/ui/src/**` and `dash/web/src/**`, any non-`import type` import from `dash/src/**` other than an explicit allowlist of pure display formatters and `view-paths.json`. The tray Rust contract (T15) also asserts the `Runtime` owns no scheduler and spawns only the server. | A source scan is coarse, so the allowlist is short, named and commented with the reason for each entry; adding to it is a reviewed change. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A18 | Where the rule is stated | A large bold callout (as at the top of this plan) in: the rule doc `docs/rules/kyberdash-display-layer.md` and ADR 0033 (Q6); `docs/dash/architecture.md` (top of the surface layer and tray sections); `docs/dash/runbook.md` (surface list); root `AGENTS.md` (a Non-negotiables bullet and the KyberDash "Where to go next" row); new `dash/AGENTS.md`; new `dash/tray/AGENTS.md`. Portable product agent files (`products/kyber-squad/agents/tauri-dev.md`, `react-dev.md`) are **not** edited. | Nested `AGENTS.md` files are what harnesses load when an agent works under `dash/`. The product agents are deployed into other repositories and must stay free of host facts (the portable-artifacts policy of the archived 2026-09-27 plan). | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A19 | One plan or two | One plan. The tray-side folder-import work would otherwise be built into a scheduler that R1 deletes. | The task graph keeps the engine, server, tray and web streams independent where files allow. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| A20 | Web wipe per harness | Already shipped by #312 (`CleanDatabaseControl` offers all or a multi-select of observed harnesses). This plan pins it with tests and moves the control into the shared Maintenance panel next to Pause / Refresh / Import. | No new wipe logic; scope stays in `cleanDatabase`. | APPROVED. Architect recommendation 2026-10-09; confirmed by the user's explicit **approve and execute** via conductor, 2026-10-10 (AP1). |
| AP1 | Approve and execute this plan? | **Approved.** The user explicitly chose **approve and execute** in chat on 2026-10-10, approving the full plan at revision 3: the Decisions table, the `test-first` Test contract (§4), the task graph (§5), risks, scope and verification gates, and confirming architect decisions A1–A20. | Approval covers revision 3 exactly. Any change to an approved Test contract row, the development mode, or a decision returns this plan to Draft and reopens approval of the affected part. Documentation validation and drift passed at revision 3 with only the expected `KW-DOC-LIFECYCLE-003` (an open plan in `docs/plans/`, cleared by the archiving PR); the finalization pass had no shell, so the checks were not rerun on this status-only edit. | APPROVED. User decision via conductor, 2026-10-10 (chose "approve and execute"). |

---

## 3. Investigation findings

Sources: CodeGraph (`codegraph_explore`) and `docs_explore` against `root=/Users/david_palfery/git/Personal/claude/kyber-weave rev=c0399ca`. Narrow follow-up reads covered `register.ts`, `routes.ts`, `bridge.ts:960-1090` and `2405-2465`, `runtime.rs:60-130`, `175-200`, `280-520`, `scheduler.rs:1-110`, `clean/pause.ts`, ADR 0032, and the rules index. No shell was available, so no live state of the user's machine was read.

### 3.1 Datastore and API sharing (scope item 1)

**Same datastore by default — yes, but resolved four different ways.**

| Process (who starts it) | Path resolution | Honors `KYBER_CANON_DB`? |
|---|---|---|
| `kyberdash web --no-open` (tray, `supervisor.rs:129` `SERVER_ARGS`) → `KyberBridge` | `bridge.ts:1001-1005`: `canonPath ?? KYBER_CANON_DB ?? ~/.kyberdash/canon.db` | Yes |
| `kyberdash dash refresh` / `dash clean` (tray, `scheduler.rs:28`, `runtime.rs:78-93`) | `register.ts:84-86` `resolveDbPath`: `--db ?? ~/.kyberdash/canon.db` | **No** |
| `kyberdash otel` receiver (tray, `receiver.rs:30` `RECEIVER_ARGS`) | `otel/service.ts:97-99`: `dbPath ?? ~/.kyberdash/canon.db` | **No** |
| `POST /api/kyber/clean` (web) | `new CanonStore(this.canonPath)` (`bridge.ts:3800`) | Yes |

The tray passes no `--db` to any child. With `KYBER_CANON_DB` unset, all four open `~/.kyberdash/canon.db`. With it set in the server's environment (the e2e boot sets it, `dash/e2e/refresh-filters-boot.ts:17`), the dashboard reads one file while the tray wipes and refreshes another.

**Same API — not guaranteed.** The tray reads `GET /api/kyber/report` from the server it supervises. A browser tab may point at a different `kyberdash web` the user started by hand; ADR 0023 Consequences states the tray "does not share a server with a dashboard the user started by hand". Both servers read the same file by default, so this alone does not explain stale data, but it violates R1's one-API rule (A12).

**Root cause, most probable first (established from code; live state not verified):**

- **RC1 — the tray scheduler re-imports within five minutes (primary).** The tray clean passes `--no-reingest`, and the wipe deletes every `source_checkpoint` row (ADR 0032 D5). The next tray tick (`Runtime::tick`, `runtime.rs:299-323`; `DEFAULT_CADENCE` 5 minutes, `scheduler.rs:20`) or the next tray start (`refresh_startup`, `runtime.rs:567-586`) runs bare `dash refresh`. With no checkpoints, it re-parses every folder source and re-inserts up to two weeks (`DEFAULT_HISTORY_WEEKS`) of history for every harness. That is "lots of historical data", produced on purpose by the tray's own scheduler. ADR 0032 Consequences documents it ("the next scheduled refresh re-ingests on its own cadence").
- **RC2 — an open dashboard tab is never told about a tray wipe.** React Query runs with `staleTime: 30_000` and `refetchOnWindowFocus: false` (`dash/web/src/main.tsx:36`, `App.tsx:62`) and no `refetchInterval` anywhere in `dash/web/src`. The only `invalidateQueries()` call is in the web's own clean dialog (`CleanDatabaseControl.tsx:74`). A tab open during a tray wipe keeps rendering cached pre-wipe responses until a remount or reload.
- **RC3 — surviving audit tables still describe pre-wipe data.** `wipeAll` (`store.ts:2084-2124`) keeps `refresh_run`, `ingest_log`, `metadata`, `token_cache` and the model-window catalog by design. The coverage footer reads the latest successful `refresh_run.history_weeks` (`bridge.ts:2405-2461`) with no `last_clean_at` filter, so after a wipe the dashboard still advertises the pre-wipe folder window, and collector-activity views still list pre-wipe ingest.
- **RC4 — conditional path split.** Only when `KYBER_CANON_DB` is set for the server: the dashboard reads a different file than the tray wipes (table above).

**Ruled out from code:** the bridge's read-only handle follows the file (`reconcile`, `bridge.ts:1070+`), and an in-place `DELETE` is visible on its next query; the only server-side memo (`identitiesMemo`, `bridge.ts:992`) is keyed on a records generation fingerprint. No second datastore was found: every `/api/kyber/*` route reads `canon.db` through the bridge, and no server route reads harness folders or the legacy session cache directly.

**Fixes in this plan:** RC1 → D1/Q2/A3/A4 plus the job host move (Q4); RC2 → A14; RC3 → A15; RC4 → A13. T22 reproduces RC1–RC3 on a temp store.

### 3.2 Tray and web feature-logic audit (scope item 2)

**ARCHITECTURAL VIOLATIONS of R1 found in the tray:**

| # | Violation | Where | Plan |
|---|---|---|---|
| V1 | **Tray-owned scheduler and job runner.** Cadence (`DEFAULT_CADENCE`), `is_due`, `tick`, `run_now`, `begin_refresh` / `finish_refresh`, `observe_report` (RunningElsewhere), the startup refresh, the background refresh worker (`PendingRefresh`, `start_background_refresh`) and cancel-on-quit. | `dash/tray/src-tauri/src/scheduler.rs` (whole module); `runtime.rs` `tick` (299-323), `refresh_now` (363-378), `refresh_startup` (567-586), `start_background_refresh` (591+) | Move to `JobHost` (T7/T8); delete from the tray (T15/T16). |
| V2 | **Tray-owned scheduling configuration.** The refresh cadence is persisted in the tray's JSON `TraySettings` and applied with `scheduler.set_cadence`. | `settings.rs`; `runtime.rs` `set_settings` (393-420) | Move to canon.db (A1, A16). |
| V3 | **Jobs invoked by spawning CLI children instead of the shared API.** Clean and refresh are `kyberdash dash clean|refresh` children through `refresh_runner`. The logic itself lives in the CLI, but the tray chooses, sequences and interprets jobs outside the one API layer. | `runtime.rs` `clean_database` (465-493), `CleanScope::clean_argv` (78-93); `scheduler.rs` `REFRESH_ARGS` | Replace with loopback HTTP calls (A5, T16). |
| V4 | **Tray-hosted OTLP receiver.** Spawning `kyberdash otel`, health probing, retry backoff and the `host_receiver` setting. | `receiver.rs` (`RECEIVER_ARGS`, `Receiver`, `HealthProbe`); `runtime.rs` `poll_receiver_inner`; `settings.rs` | Move to the server's `ReceiverHost` (Q4 = a; T8/T12); delete from the tray (T16). |
| V5 | **Codified in docs.** ADR 0023 decision 3 ("The tray owns the server, the scheduled refresh and the receiver"), ADR 0032 D1/Consequences (tray spawns the clean child; next scheduled refresh re-ingests), and the runbook's surface list ("owns the refresh cadence and the optional OTLP receiver"). | `docs/adr/0023-…`, `docs/adr/0032-…`, `docs/dash/runbook.md` | T23 per Q3 and Q6. |

**Boundary, not a violation (Q4 = a):** `supervisor.rs` launches and restarts `kyberdash web --no-open`. This launcher duty is the tray's single permitted non-display role, narrowed by A12 to "attach if running, else launch".

**Display duties that stay in the tray:** the report cache and stale banner (`api.rs` `ReportCache`), the harness selector and window-days preference, `open_view` URL checking, the popover IPC, and polling the API for state.

**Web dashboard:** no scheduler, timer or job found (`dash/web/src` has no `setInterval`); clean goes through `POST /api/kyber/clean`. **One minor violation, V6:** `SessionCostPanel.tsx:3,202` imports the engine's `sumCosts` and computes the cost-basis mismatch client-side. `renderCost` (pure formatting) and `view-paths.json` (route constants) are display-safe. T19/T20 move the mismatch into the session payload.

### 3.3 Existing code the plan builds on

- **Central clean** (`dash/src/clean/clean.ts`). `CleanScope.reingestWeeks`: `undefined` → 1 week, `null` → skip, `n` → n weeks. `portsForClean.reingest` calls `refreshHarnessSources(..., { historyWeeks, trigger: 'cli' })`. `MAX_CLEAN_REINGEST_WEEKS = 52`.
- **Receiver pause** (`dash/src/clean/pause.ts`). Loopback pause/resume with a 10-minute lease; used only by the clean.
- **CLI** (`dash/src/cli/register.ts`). `dash refresh` accepts `--history-weeks` (default 2) and a hidden `--trigger` (`cli|tray|scheduled`), takes `acquireStoreRefreshLock`, exits 3 when busy. `dash clean` requires `--yes` and exactly one of `--all` / `--harness`.
- **Server.** `routes.ts` serves only `/api/kyber/*`; `POST /api/kyber/clean` has a body cap, `parseCleanBody` and 409 `CLEAN_BUSY`. No settings, jobs, refresh or import route exists.
- **Orchestrator** (`dash/src/refresh/orchestrator.ts`). Each run opens a `refresh_run` row, runs one job per descriptor, drains the writer, then runs `purgeExpiredContent` and one `projectCanonicalStore` — the only production caller of `purgeExpiredContent`.
- **Tray.** `authorized_commands()` lists 7 commands (`runtime.rs:175-185`) alongside the capability JSON. UI: `viewState.ts`, `components/CleanDatabase.tsx`, `SettingsView.tsx`, `Actions.tsx`; tests in `Popover.test.tsx`, `commands.test.tsx`.
- **Web.** `CleanDatabaseControl.tsx` on `ContextDoctor.tsx:562` (all or multi-select harness wipe; disclosure promises a 7-day re-ingest); `kyberApi.ts` `cleanDatabase`; tests in `dash/web/src/pages/CleanDatabase.test.tsx`.
- **Docs.** ADR 0032 D1, D6 and its tray Consequence; ADR 0016 D1 and its rejected "UI-triggered refresh before the CLI contract exists" alternative; ADR 0023 D3 and its server-sharing Consequence; `docs/dash/runbook.md` surface list and §2b; `docs/dash/architecture.md` surface layer, "Local harness-source refresh" and clean sections. No `dash/AGENTS.md` exists.

---

## 4. Test contract (test-first)

Runner notes: `npm --prefix dash run test -- <path>` runs the dash Vitest suite (it includes `dash/web/src/**` tests). The tray UI uses `npm --prefix dash/tray/ui run test -- <path>`. Rust tests run from `dash/tray/src-tauri`. Tests use temp or `:memory:` stores, temp `HOME`/state dirs, fake clocks and fake spawners only — never the home `canon.db`, `server.json` or `jobs.lock`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T1/T2 | `dash/src/canon/paths.test.ts` (new) | `npm --prefix dash run test -- src/canon/paths.test.ts` | `resolveCanonDbPath()` gives explicit > `KYBER_CANON_DB` > `~/.kyberdash/canon.db`. With `KYBER_CANON_DB` set to a temp file and no `--db`: `KyberBridge().canonPath`, the `dash refresh` / `dash clean` store path (through `createStore`), and `startOtlpCollectorService` all resolve the same file. | Module missing; CLI and receiver ignore the env var on `main`. | Same file passes unmodified. |
| T3/T4 | `dash/src/settings/shared-settings.test.ts`, `dash/src/refresh/folder-import.test.ts` (new) | `npm --prefix dash run test -- src/settings/shared-settings.test.ts src/refresh/folder-import.test.ts` | **Settings:** every A1 key reads its default when absent or malformed; valid values round-trip; cadence outside 1–1440 is rejected; a wipe-all keeps every value. **Gate:** `folderSourcesAllowed` gives `cli` → true and `scheduled` / `tray` / `web` → the setting; `REFRESH_TRIGGERS` includes `web`. **Import:** defaults to 1 week; rejects 0, 53 and non-integers before any store write; passes narrowed descriptors; leaves the setting unchanged. **Maintenance:** purges expired content, projects, writes no `refresh_run` row. | Modules missing. | Passes unmodified. |
| T5/T6 | `dash/src/clean/clean.test.ts` | `npm --prefix dash run test -- src/clean/clean.test.ts` | Omitting `reingestWeeks` → no import, `reingested: false`, `historyWeeks: null`. An explicit `n` imports through `importFolderHistory` with the cleaned scope. Pause, wipe, project, resume order unchanged. Settings unchanged after any clean. | The existing default-1-week assertion is flipped first and fails on `main`. | Passes. |
| T7/T8 | `dash/src/jobs/host.test.ts`, `dash/src/jobs/receiver-host.test.ts` (new) | `npm --prefix dash run test -- src/jobs` | **Schedule:** with a fake clock and spawner, the host runs `dash refresh --trigger scheduled` at start and every `refresh_cadence_minutes`; a cadence change through settings applies on the next tick. **Pause (Q5 = a):** while `jobs.paused` is on, no scheduled refresh child starts, the maintenance pass (content-retention purge) still runs, manual `runNow('web')` / `runNow('tray')`, import and clean still start, and the receiver host keeps the OTLP receiver running. **Busy/failure:** exit 3 → `running-elsewhere`, exit 1 → `failed` with the last success time kept, one job at a time. **Lease:** a second host with a live `jobs.lock` holder runs nothing and reports `hostedElsewhere`; a dead holder is reclaimed. **Shutdown:** `close()` kills the running child. **Receiver (Q4 = a):** the server's `ReceiverHost` hosts `kyberdash otel` when `receiver.hosted` is on, stops it when off, backs off on repeated failure. | Modules missing. | Passes. |
| T9/T10 | `dash/src/cli/register.test.ts`, `dash/src/cli/cli-commands.test.ts` | `npm --prefix dash run test -- src/cli/register.test.ts src/cli/cli-commands.test.ts` | **Refresh:** setting off → bare `dash refresh` still calls `refreshHarnessSources` (D4); `--trigger scheduled`, `tray` and `web` do not, run the maintenance pass, print the skip line, exit 0; setting on → they import. **Clean:** `dash clean --all --yes` does not import; `--reingest-weeks 2` does; `--no-reingest` accepted. **Import:** `dash import-history` exits 2 on bad `--weeks` before the store opens, 3 when locked with nothing written, 0 on success with the setting unchanged. **Settings:** `dash settings show/set [--json]` round-trips every A1 key and exits 2 on an unknown key or bad value. | New cases fail on `main`. | Passes. |
| T11/T12 | `dash/src/server/settings-route.test.ts`, `jobs-route.test.ts`, `import-history-route.test.ts` (new); `clean-route.test.ts`, `clean-bridge.test.ts`, `coverage-after-clean.test.ts` (new), `dash/src/cli/web.test.ts` | `npm --prefix dash run test -- src/server src/cli/web.test.ts` | **Settings:** `GET`/`PUT /api/kyber/settings` round-trip; 400 on invalid values; 405/413 like clean. **Jobs:** `GET /api/kyber/jobs` reports state, `nextDueAt`, `paused`, `storeGeneration`; `storeGeneration` changes after a clean. **Refresh:** `POST /api/kyber/refresh {surface}` → 202 and the host spawns `--trigger tray|web`; 409 when busy; 400 on an unknown surface. **Import:** 202 / 400 / 409. **Clean:** a body without `reingestWeeks` yields `reingested: false`. **Coverage:** after a wipe, coverage reports no folder window although pre-wipe `refresh_run` rows exist. **Server lifecycle:** `startWebServer` starts the job host and the receiver host (Q4 = a), writes `server.json` in a temp state dir, removes it and stops children on close. A loopback request with no `Origin` may PUT/POST; a cross-origin one is rejected. No remote error string appears in any body. | Routes 404, defaults and coverage assertions fail on `main`. | Passes. |
| T13/T14 | `dash/web/src/pages/CleanDatabase.test.tsx`, `dash/web/src/pages/Maintenance.test.tsx` (new), `dash/web/src/lib/storeGeneration.test.ts` (new) | `npm --prefix dash run test -- web/src/pages/CleanDatabase.test.tsx web/src/pages/Maintenance.test.tsx web/src/lib/storeGeneration.test.ts` | **Clean:** wipe-all and per-harness multi-select both post the right scope; an unchecked **Import folder history** box and weeks input (1–52, default 1); without the box no `reingestWeeks` is sent; the disclosure no longer promises an automatic re-ingest. **Maintenance panel:** **Pause/Resume** PUTs `jobs.paused` and the paused state discloses that only scheduled jobs stop (manual actions, OTLP ingest and retention purge continue, Q5 = a); **Refresh data** POSTs `/api/kyber/refresh {surface:'web'}` and shows running/busy; **Import folder history** POSTs `/api/kyber/import-history`; the scheduled-folder toggle and cadence PUT `/api/kyber/settings` and the toggle discloses the 2-week window (A10). Every control renders server state, and "unknown" on a fetch failure. **Store generation:** a changed `storeGeneration` triggers `invalidateQueries()`; an unchanged one does not. | Elements, hook and API calls absent. | Passes; `npm --prefix dash run check:reachable` stays green. |
| T15/T16 | `dash/tray/src-tauri/tests/runtime_contract.rs`, `api.rs` unit tests | `cd dash/tray/src-tauri && cargo test` | **No jobs in the tray:** the `Runtime` has no scheduler; across start, tick and every command it spawns only the server launcher (or none when `server.json` names a live compatible server). **Commands → HTTP:** `refresh_now` → `POST /api/kyber/refresh {surface:'tray'}`; `clean_database {scope, importWeeks?}` → `POST /api/kyber/clean`; `import_folder_history {weeks, harness?}` → `POST /api/kyber/import-history` (weeks 1–52 validated before any request); `set_shared_settings {patch}` → `PUT /api/kyber/settings`; `set_settings` persists display preferences only. `authorized_commands()` and the capability JSON list exactly 9 commands. **View state:** `ViewState.jobs` and `ViewState.sharedSettings` come from the loopback GETs and are null on fetch failure; 409 maps to the existing busy message. | Contract fails on `main` (scheduler present, CLI children spawned, 7 commands). | `cargo fmt --check`, `cargo clippy -- -D warnings`, `cargo test` pass. |
| T17/T18 | `dash/tray/ui/src/Popover.test.tsx`, `commands.test.tsx`, `components/Maintenance.test.tsx` (new) | `npm --prefix dash/tray/ui run test` | **Pause/Resume** invokes `set_shared_settings {jobsPaused}`, shows the paused state from `ViewState.jobs`, and while paused **Refresh now**, **Import folder history** and **Clean** stay enabled (Q5 = a). **Refresh now** unchanged in UI, now reflecting server job state. **Clean confirm** shows an unchecked import box and weeks input, and invokes `clean_database {scope, importWeeks?}`. **Import folder history** invokes `import_folder_history`. **Settings view** shows scheduled folder refresh, cadence and receiver hosting from `sharedSettings`, "unknown" when null, and invokes `set_shared_settings`. A rejection shows the existing `ipc-action-error` banner. | Elements and commands absent. | `npm --prefix dash/tray/ui run typecheck` and `test` pass. |
| T19/T20 | `dash/web/src/components/SessionCostPanel.test.tsx`, `dash/src/server/session-cost-mismatch.test.ts` (new) | `npm --prefix dash run test -- web/src/components/SessionCostPanel.test.tsx src/server/session-cost-mismatch.test.ts` | The session payload carries the cost-basis mismatch problem computed server-side; the panel renders it from the payload and no longer imports `sumCosts`. | Payload field absent. | Passes. |
| T21 | `dash/src/architecture/display-layer.test.ts` (new) | `npm --prefix dash run test -- src/architecture/display-layer.test.ts` | **R1 guard (A17).** Fails on any tray scheduler module or cadence timer, any tray process spawn other than the server launcher, any tray argv literal for `refresh`/`clean`/`import-history`/`settings`/`otel`, and any non-type import from `dash/src/**` in `dash/web/src` or `dash/tray/ui/src` outside the commented allowlist. Each failure message names the file, the construct and the rule doc. | Fails on `main` (V1, V3, V4, V6). | Passes after T16, T18 and T20 with no production change of its own. |
| T22 | `dash/src/jobs/wipe-acceptance.integration.test.ts` (new) | `npm --prefix dash run test -- src/jobs/wipe-acceptance.integration.test.ts` | **Acceptance oracle.** Temp store with fixture folder sources, settings absent, server started with a fake-clock job host: wipe all through `POST /api/kyber/clean`; insert OTLP records through the ingest path; advance three cadence ticks and one `POST /api/kyber/refresh {surface:'web'}`. Assert zero folder-sourced records, the OTLP records present, expired content purged, no `refresh_run` row added, coverage reports no folder window, and `storeGeneration` changed. Turn scheduled folder import on, advance one tick: folder records return. | Fails on `main` (routes missing; a scheduled refresh imports). | Passes. |
| T23 | Docs (no-test task) | `dotnet run --project src/KyberWeave.Cli --no-build -c Release -- docs validate . --merge-ready` and `... docs drift .` | Documentation only; replaced by read-only verification: both commands at zero findings, plus a check by inspection that the R1 callout appears in every A18 location. | Not applicable (no product behavior). | Zero findings from both commands. |

Each GREEN task uses the same contract test without weakening any assertion. Changing an approved row returns this plan to Draft.

---

## 5. Tasks

| Task | Objective | Files / symbols | Acceptance | Depends on | Required skills |
|---|---|---|---|---|---|
| T1 RED | Path-resolver contract tests (RC4) | `dash/src/canon/paths.test.ts` | T1 row fails | — | test-dev |
| T2 GREEN | Implement A13 and route every resolver through it | `dash/src/canon/paths.ts` (new); `register.ts` `resolveDbPath`; `otel/service.ts:97-99`; `bridge.ts` constructor; `cli/report.ts` | T1 passes; `typecheck`, `lint` | T1 | TypeScript/Node (dash engine) |
| T3 RED | Shared-settings and folder-import contract tests | `dash/src/settings/shared-settings.test.ts`, `dash/src/refresh/folder-import.test.ts` | T3 row fails | — | test-dev |
| T4 GREEN | Implement A1/A2/A3: settings module, trigger gate, `importFolderHistory`, `runMaintenancePass`, `web` trigger | `dash/src/settings/shared-settings.ts`, `dash/src/refresh/folder-import.ts` (new); `dash/src/canon/refresh-run.ts` (`REFRESH_TRIGGERS`); `refreshHarnessSources` reused, not forked | T3 passes | T3 | TypeScript/Node (dash engine) |
| T5 RED | Flip the clean default; route opt-in import through the central method | `dash/src/clean/clean.test.ts` | T5 row fails on `main` | T4 | test-dev |
| T6 GREEN | `cleanDatabase` defaults `reingestWeeks` to null; `portsForClean.reingest` delegates to `importFolderHistory`; update the `CleanScope` docblock | `dash/src/clean/clean.ts` | T5 passes | T5 | TypeScript/Node (dash engine) |
| T7 RED | Job-host and receiver-host contract tests (Q4 = a), including scheduled-only pause (Q5 = a) | `dash/src/jobs/host.test.ts`, `dash/src/jobs/receiver-host.test.ts` | T7 row fails | — (contract fixed by A1, A4, A11, A12, Q4, Q5) | test-dev |
| T8 GREEN | Implement `JobHost` (A11/A12; pause gates scheduled refreshes only, maintenance pass and manual jobs unaffected, Q5 = a) and `ReceiverHost` ported from the tray's receiver policy (Q4 = a) | `dash/src/jobs/host.ts`, `dash/src/jobs/receiver-host.ts`, `dash/src/jobs/lease.ts` (new; reuses `refresh/lock.ts` PID-liveness helpers) | T7 passes | T7, T4 | TypeScript/Node (dash engine) |
| T9 RED | CLI contract tests: refresh gate, clean default, `import-history`, `settings` | `dash/src/cli/register.test.ts`, `cli-commands.test.ts` | T9 row fails | T4 | test-dev |
| T10 GREEN | Implement A4/A6 in the CLI | `dash/src/cli/register.ts`; `dash/src/clean/report.ts` skip wording | T9 passes | T9, T2, T6 | TypeScript/Node (dash engine) |
| T11 RED | Server contract tests: settings, jobs, refresh, import, clean default, coverage after clean, server lifecycle | `dash/src/server/*-route.test.ts` (new and existing), `coverage-after-clean.test.ts`, `dash/src/cli/web.test.ts` | T11 row fails | T4 | test-dev |
| T12 GREEN | Implement A7/A12/A14/A15: routes, bridge writers, coverage filter, `storeGeneration`, job host and receiver host started by the server, `server.json` | `dash/src/server/routes.ts`, `dash/src/server/bridge.ts`, `dash/src/cli/web.ts` | T11 passes | T11, T2, T6, T8 | TypeScript/Node (dash engine) |
| T13 RED | Web tests: clean opt-in and per-harness, Maintenance panel (Pause/Resume, Refresh data, Import, settings), store-generation invalidation | `dash/web/src/pages/CleanDatabase.test.tsx`, `Maintenance.test.tsx`, `dash/web/src/lib/storeGeneration.test.ts` | T13 row fails | — (contract fixed by A7) | test-dev |
| T14 GREEN | Web client functions, Maintenance panel and generation hook — display and API calls only | `dash/web/src/lib/kyberApi.ts`, new `lib/storeGeneration.ts`, `components/maintenance/CleanDatabaseControl.tsx`, new `components/maintenance/MaintenancePanel.tsx` (Pause/Resume, Refresh data, Import folder history, shared settings), `pages/ContextDoctor.tsx` (panel placement), `main.tsx` or `App.tsx` (hook mount) | T13 passes; `check:reachable` | T13 | react |
| T15 RED | Tray Rust contract: no scheduler, server-only spawns, commands as HTTP calls, 9 commands, view state from the API | `dash/tray/src-tauri/tests/runtime_contract.rs`, `api.rs` tests | T15 row fails | — (contract fixed by A5, A7, A12) | test-dev |
| T16 GREEN | Strip jobs from the tray: delete `scheduler.rs` and receiver hosting; commands call the API; supervisor attaches via `server.json`; settings keep display preferences only | `dash/tray/src-tauri/src/`: `scheduler.rs` (delete), `receiver.rs` (delete; hosting moved to the server, Q4 = a), `runtime.rs`, `api.rs` (POST/PUT helpers), `supervisor.rs` (attach), `settings.rs`, `ipc.rs`, `lib.rs`, capability JSON | T15 passes; fmt/clippy clean | T15 | tauri-rust |
| T17 RED | Tray UI tests: Pause/Resume, clean import opt-in, Import folder history, shared settings view | `dash/tray/ui/src/Popover.test.tsx`, `commands.test.tsx`, `components/Maintenance.test.tsx` | T17 row fails | — (IPC contract fixed by the T15 row) | test-dev |
| T18 GREEN | Tray UI — display and command invocation only | `dash/tray/ui/src/viewState.ts`, `components/CleanDatabase.tsx`, `components/SettingsView.tsx`, `components/Actions.tsx`, new `components/Maintenance.tsx`, `Popover.tsx` | T17 passes; typecheck | T17, T16 (the TS `ViewState` mirrors the Rust serialization) | react |
| T19 RED | V6 tests: server-computed cost-basis mismatch | `dash/web/src/components/SessionCostPanel.test.tsx`, `dash/src/server/session-cost-mismatch.test.ts` | T19 row fails | — | test-dev |
| T20 GREEN | Compute the mismatch in the session payload; the panel renders it | `dash/src/server/bridge.ts` (session payload), `dash/web/src/lib/kyberApi.ts` (type), `dash/web/src/components/SessionCostPanel.tsx` | T19 passes | T19, T12, T14 | TypeScript/Node (dash engine), react |
| T21 | Display-layer guard test (RED on `main` first, then GREEN with no production change of its own) | `dash/src/architecture/display-layer.test.ts` | T21 row | — (GREEN after T16, T18, T20) | test-dev |
| T22 | Wipe acceptance oracle (RED on `main` first, then GREEN with no production change of its own) | `dash/src/jobs/wipe-acceptance.integration.test.ts` | T22 row | T10, T12 | test-dev |
| T23 | Docs and closeout | **Rule (Q6 = a):** new `docs/rules/kyberdash-display-layer.md` and its row in `docs/rules/README.md`; new ADR 0033 "KyberDash surfaces are display layers over one shared application" and its `docs/adr/README.md` row, superseding ADR 0023 decision 3 and its no-shared-server Consequence; ADR 0023 gets a supersession line in its Status section naming ADR 0033 (no other edit). **Q3:** ADR 0032 edited in place (title, D1, D6, Consequences, dated amendment line); ADR 0016 edited in place (D1 trigger sentence, rejected-alternative note, dated amendment line). **Callouts (A18):** `docs/dash/architecture.md` (surface layer, tray, refresh, clean, routes table, tray command count), `docs/dash/runbook.md` (surface list, §2b, import, settings, Pause, the D1 and A16 upgrade notes), root `AGENTS.md` (Non-negotiables bullet, KyberDash row), new `dash/AGENTS.md`, new `dash/tray/AGENTS.md`; `docs/catalog.md` if its KyberDash entry names tray ownership. This plan's closeout mapping, then archive per KW-DOC-LIFECYCLE-003. | Zero findings from `docs validate --merge-ready` and `docs drift`; R1 callout present in every A18 location | T2–T22 | app-docs-standard, architecture-decision-record |

### Dependency graph and concurrency

```text
T1 -> T2
T3 -> T4 -> T5 -> T6
T7 ; {T7, T4} -> T8
T4 -> T9 ; {T9, T2, T6} -> T10
T4 -> T11 ; {T11, T2, T6, T8} -> T12
T13 -> T14
T15 -> T16
T17 ; {T17, T16} -> T18
T19 ; {T19, T12, T14} -> T20
T21 (RED now) ; GREEN after {T16, T18, T20}
{T10, T12} -> T22
{T2 .. T22} -> T23
```

- **Wave 1:** T1, T3, T15, T21.
- **Wave 2:** T2, T4, T7, T17.
- **Wave 3:** T5, T8, T9, T11.
- **Wave 4:** T6, T13, T16, T19.
- **Wave 5:** T10, T12, T14, T18.
- **Wave 6:** T20, T22.
- **Wave 7:** T21 GREEN confirmation, then T23.

**MAX_CONCURRENCY audit: 4.**

- No two tasks in the same wave edit the same file. `bridge.ts` is edited by T2 → T12 → T20 in sequence; `register.ts` by T2 → T10; `kyberApi.ts` by T14 → T20.
- T10 and T12 wait for T6 because both call the changed `CleanScope` default, and for T2 because both use the resolver.
- T12 waits for T8 because the server starts the job host.
- T18 waits for T16 because the TS `ViewState` must match the Rust serialization.
- RED tasks for the tray (T15, T17) and the web (T13) code only against the contracts fixed in A5, A7 and A12, so they need no engine task first.

---

## 6. Risks

- **Upgrade surprise (D1, A16).** Every existing user loses scheduled folder refresh, and any custom tray cadence resets to 5 minutes. Mitigation: runbook and release note (T23); both surfaces show the setting state, and "unknown" when it cannot be read.
- **Tray depends on the server for everything.** With jobs out of the tray, a dead server means no refresh and no actions. Mitigation: the supervisor still launches or restarts the server; the tray shows the server phase it already shows; actions fail with the existing error banner rather than silently.
- **Two servers.** A user-started `kyberdash web` plus the tray. Mitigation: A12 (`server.json` attach, `jobs.lock` single host); T7 and T15 cover both sides.
- **Coverage-window honesty.** Mitigation: A4 and A15, asserted in T3, T11 and T22.
- **Lock contention.** `import-history`, clean, scheduled and surface refresh share `acquireStoreRefreshLock`; exit 3 and HTTP 409 both mean busy.
- **Guard-test brittleness (A17).** A source scan can flag a harmless construct. Mitigation: a short, named, commented allowlist; failure messages cite the rule doc.
- **#318 overlap.** #318's tray clean threading becomes moot (the tray clean is an HTTP call); its lock consolidation touches `bridge.ts`; its "unread `last_clean_at`" item is resolved differently here (A15 reads it); its "pause removal" item does not overlap, because Q5 = (a) leaves the clean's receiver pause untouched. Whichever lands second merges trunk and resolves overlaps; the conductor should tell #318's owner.
- **Pre-existing dash test flakes** disclosed by earlier plans (`migration.test.ts` DROP COLUMN, the Cursor date floor). Disclose them; do not fix them here.

## 7. Out of scope

- #318's other items: refresh-lock consolidation, the writer `onError`, and the clean's receiver-pause removal.
- Any change to the OTLP receiver's ingest path, the retention window, or `DEFAULT_HISTORY_WEEKS`.
- Migrating existing tray cadence values (A16).
- Launching the server without the tray or a terminal (OS login items); the tray stays the launcher.

## 8. Verification gates

- **dash:** `npm --prefix dash run typecheck`, `lint`, `test` (includes T21) and `check:reachable`.
- **Tray UI:** `npm --prefix dash/tray/ui run typecheck` and `test`.
- **Tray Rust:** `cd dash/tray/src-tauri && cargo fmt --check && cargo clippy -- -D warnings && cargo test`.
- **Docs:** `docs validate . --merge-ready` and `docs drift .` at zero findings.
- **Full suite:** `dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`.
- **Release loop:** `./scripts/update-loop.sh`, because the tray's process ownership and the `kyberdash web` startup change.

## 9. Review and closeout

- **Review:** run the code-review council once over the whole change before the PR, with the R1 rule doc named as a review input.
- **docs-dev closeout (T23):** map each durable decision to its destination:
  - R1, Q6 → `docs/rules/kyberdash-display-layer.md`, ADR 0033 (superseding ADR 0023 D3), plus the A18 callouts;
  - D1–D4, Q2, A1–A4, A6–A10 → ADR 0032 and ADR 0016 (edited in place, Q3) and `docs/dash/architecture.md`;
  - Q4, Q5, A5, A11–A17, A20 → `docs/dash/architecture.md` (surface layer, job host, API table) and ADR 0033;
  - operator usage → `docs/dash/runbook.md`.
- **No documentation waiver.** This is a user-facing behavior and architecture change with surviving invariants.
- **Archive:** the finishing PR archives this plan per KW-DOC-LIFECYCLE-003 and moves its row in the plan index.

---

## 10. Closeout

### Outcome

Every task T1–T23 was delivered in development mode `test-first`, and each was audited
against its contract row in §4 before the next began — no row was weakened to pass. The
whole change then went through one whole-change review pass by four lenses, and the
findings were fixed rather than deferred. The plan's `## Decisions` table above is
unchanged from the approved revision 3.

### Durable facts and where they now live

| Decision | Destination |
|---|---|
| R1, Q6 (the display-layer rule itself) | [`docs/rules/kyberdash-display-layer.md`](../../rules/kyberdash-display-layer.md) — the always-on governed rule, listed in the [rules index](../../rules/README.md) — and [ADR 0033](../../adr/0033-kyberdash-surfaces-are-display-layers.md), which supersedes ADR 0023 decision 3 and its no-shared-server Consequence. |
| D1–D4, Q2, A1–A4, A6–A10 (folder import is opt-in; the shared settings; the trigger gate; the clean default) | [ADR 0032](../../adr/0032-kyberdash-user-initiated-clean.md) and [ADR 0016](../../adr/0016-kyberdash-harness-source-refresh.md), both **amended in place** on 2026-10-10 per Q3, each with a dated amendment line naming what changed; plus `docs/dash/architecture.md`. |
| Q4, Q5, A5, A11–A17, A20 (`JobHost` / `ReceiverHost` in the shared application; children under the refresh lock; one jobs lease; `server.json` attach; one path resolver; `storeGeneration`; coverage after a wipe; the guard test) | [ADR 0033](../../adr/0033-kyberdash-surfaces-are-display-layers.md) decisions 2–8, and `docs/dash/architecture.md` (surface layer, job host, `/api/kyber/*` route table). |
| The A16 settings move into canon.db, read by the engine | [ADR 0031](../../adr/0031-kyberdash-capture-command-owns-harness-telemetry-keys.md), amended in place on 2026-10-10 so its receiver-hosting sentence reads as the engine's setting rather than the tray's. |
| Operator-facing behaviour: pause semantics, the opt-in import action, the shared settings, the upgrade notes for D1 and A16 | [`docs/dash/runbook.md`](../../dash/runbook.md). |
| The rule an agent meets before it meets the code (A18) | Root [`AGENTS.md`](../../../AGENTS.md) (Non-negotiables bullet and the KyberDash row), and the new [`dash/AGENTS.md`](../../../dash/AGENTS.md) and [`dash/tray/AGENTS.md`](../../../dash/tray/AGENTS.md); the KyberDash entry in [`docs/catalog.md`](../../catalog.md). |

No documentation waiver was claimed: this change left surviving operational and
behavioural invariants that future engineers need to consult, so canonical documentation
was created or updated in every case above rather than waived.

### Verification run

The declared gates of §8, all green: dash `typecheck`, `lint`, `test` (which includes the
T21 display-layer guard and the T22 wipe-acceptance oracle) and `check:reachable`; tray UI
`typecheck` and `test`; tray Rust `cargo fmt --check`, `cargo clippy -- -D warnings` and
`cargo test`; and `docs validate . --merge-ready` and `docs drift .` at zero findings after
the archive move. The full suite
(`dotnet run --project src/KyberWeave.Cli -- review gates . --out artifacts/gates.json`)
was run for the whole change. `./scripts/update-loop.sh` exercised the new
`kyberdash web` startup and update path.

### Deliberately deferred follow-ups

These were identified during the work and consciously left out of scope. They are recorded
here rather than as todos; none of them is authorized work, and each stays open until a
user picks it up.

- **JobHost does not re-verify its jobs lease on each tick**, so a host displaced by a
  takeover keeps spawning jobs rather than noticing it is no longer the host.
- **`status` conflates "lock unavailable" with "hosted elsewhere"**, so a caller cannot
  tell a contended lock from a lease held by another process.
- **`clean` silently drops unknown ids in a MIXED scope** — only an all-unknown scope
  throws, so a partially-valid harness list wipes less than the caller asked for without
  saying so.
- **A second host pair can be built for one state dir** when the injected seams differ
  (tests, and any future non-default construction), because identity is not keyed on the
  state dir alone.
- **`enterSingleFlight` ignores `waitMs: 0` in-process**; the hosted-services registry
  works around this rather than the primitive honouring the flag.
- **`main.ts` decides whether to print a usage error by watching stderr**, which is a
  fragile signal; it should be replaced with an explicit error-code marker.
- **`GET /settings` and `/jobs` do not cap or drain request bodies**, unlike the POST
  routes beside them.
- **Tray `ViewState` still carries optional legacy `refresh` / `receiver` fields**, kept
  only because approved tests use them; remove the fields and their fixtures.
- **The Windows tray build was not compiled in this environment.** The `OpenProcess` /
  `CreateProcess` paths in `supervisor.rs` are Windows-gated. Run
  `cargo check --target x86_64-pc-windows-msvc`, or let CI do it, before relying on that
  path.
- **#318 interplay.** This change makes #318's tray-clean threading moot (the tray clean is
  an HTTP call now) and resolves its "unread `last_clean_at`" item by reading that stamp
  for coverage-after-clean, so #318's owner must reconcile rather than reimplement.
