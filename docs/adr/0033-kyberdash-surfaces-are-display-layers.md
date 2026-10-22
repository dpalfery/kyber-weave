---
id: adr/0033-kyberdash-surfaces-are-display-layers
title: The KyberDash Tray and Web Dashboard Are Display Layers, and the Engine Hosts the Jobs
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-10-10
supersedes:
  - adr/0023-kyberdash-report-model-and-tray-ownership
---

# ADR 0033: The KyberDash Tray and Web Dashboard Are Display Layers, and the Engine Hosts the Jobs

## Status

Accepted, 2026-10-10, recording decisions D1–D8 of the
[folder-import plan](../archive/plans/2026-10-09-issue-319-folder-import-opt-in.md).

This record supersedes **decision 3** of
[ADR 0023](0023-kyberdash-report-model-and-tray-ownership.md) ("The tray owns the server,
the scheduled refresh and the receiver") and the Consequence that depended on it ("The tray
starts and restarts the processes it reads from; it does not share a server with a dashboard
the user started by hand"). ADR 0023's report model, its Rust-side HTTP decision, and its
refresh-lock/exit-3 decision all stand, and ADR 0023 is not edited beyond a status note —
the same shape as [ADR 0008](0008-kyberdash-single-canonical-store.md) superseding one
decision of ADR 0007. The always-on rule this record decides is
[`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md).

## Context

The tray grew into a second implementation of KyberDash. Its Rust core held a scheduler, a
refresh worker, a cadence setting, receiver hosting, and the argv for each engine command —
so the tray was not a display layer over one store and one API, it was a second operator of
both.

[Issue #319](https://github.com/dpalfery/kyber-weave/issues/319) made that visible. A user
cleaned the database; within five minutes the tray's own scheduler had re-imported the
folder history, while the dashboard showed the clean state. Nothing was broken, nothing was
racy — there were simply two schedulers with different views of what the user had asked for,
and the tray could not be told "off". Folder import had also always been on for every user
who had it, with no way to say no.

Two further costs were already visible. A cadence stored in tray-local state was invisible
to the dashboard and to the CLI, so the answer to "when did this last refresh, and who
decided that?" depended on which surface was asked. And the receiver was tied to the tray's
lifetime, so quitting the menu bar stopped collection.

## Decision

1. **The rule.** **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS
   ISOLATED FEATURE LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING,
   CLEAN, IMPORT, PAUSE) LIVE IN THE SHARED KYBERDASH APPLICATION (THE CLI ENGINE AND THE
   `kyberdash web` SERVER BUILT FROM IT) THAT BOTH SURFACES SHARE. BOTH SURFACES USE ONE
   DATASTORE (canon.db, resolved by `dash/src/canon/paths.ts` `resolveCanonDbPath`) AND ONE
   API LAYER (`/api/kyber/*`). NO AGENT OR DESIGN MAY VIOLATE THIS.** It is stated in full,
   with its boundary cases, in
   [`rules/kyberdash-display-layer`](../rules/kyberdash-display-layer.md).
2. **The `kyberdash web` server hosts the jobs.** `JobHost` (`dash/src/jobs/host.ts`) runs
   the scheduled refresh, the maintenance pass, and the manual/import/clean children;
   `ReceiverHost` (`dash/src/jobs/receiver-host.ts`) owns the OTLP receiver child. Both are
   in `dash/src/jobs/**` and both are hosted by the server started from
   `dash/src/cli/web.ts`.
3. **Jobs run as child processes of the same binary, under the refresh lock.** A job is
   `kyberdash dash refresh …` spawned by the server, so a crash or a long import cannot take
   the server down, and the in-child refresh lock (`dash/src/refresh/lock.ts`, exit code 3)
   keeps arbitrating with a terminal-started refresh exactly as ADR 0023 D4 requires.
4. **One jobs lease, one scheduler.** `dash/src/jobs/lease.ts` holds `jobs.lock` in the
   state directory; a host that cannot take the lease runs nothing and reports
   `running-elsewhere`. Two servers started by hand cannot both schedule.
5. **Attach, do not own.** The server publishes `~/.kyberdash/server.json` —
   `{ pid, url, apiVersion }` — once it is listening, and removes it on clean shutdown. The
   tray attaches to a published server if one is live and launches one otherwise; it kills
   only a server it launched itself. The server is removable from `server.json` precisely so
   no tray looks for a dead URL.
6. **Folder import is opt-in, off by default, and shared.** Scheduled folder import does not
   run for anyone until the shared setting `settings.folder_import.scheduled` is `on` in
   `canon.db` metadata (`dash/src/settings/shared-settings.ts`), settable from any surface.
   A separate one-off **Import folder history** action remains available and is always
   allowed; the setting gates only the *scheduled* run, never the explicit request.
7. **Pause pauses scheduled jobs only.** `settings.jobs.paused` stops the `JobHost` from
   scheduling. The receiver is not a scheduled job and keeps collecting: pausing jobs must
   not silently stop ingestion.
8. **Enforced by a guard, not by review.** `dash/src/architecture/display-layer.test.ts`
   scans the tray's Rust and both surfaces' TypeScript for schedulers, cadence timers, argv
   literals, process spawns and non-allowlisted engine value imports, and fails naming the
   file, line and construct. It runs in `npm --prefix dash run test`.

## Alternatives Considered

- **Keep receiver hosting in the tray.** Rejected: it is the same second-operator problem
  as the scheduler, and it ties data collection to a menu-bar process. ADR 0031's
  "the tray's existing receiver settings keep the receiver alive" reads as the engine's
  settings once the engine hosts it; no other part of ADR 0031 changes.
- **A separate `kyberdash daemon` process owning jobs.** Rejected: a third lifecycle, a
  third state directory entry, and another install surface, for a job set that is already
  owned by a process that has to be running for the dashboard to work at all. If the server
  is not running there is no surface to show stale data.

## Consequences

- Existing tray users stop getting scheduled folder refreshes on upgrade: the setting is
  off until someone turns it on. That is the intent of decision 6, and it is stated in the
  upgrade note rather than discovered.
- The refresh cadence resets to 5 minutes on upgrade, because it moves from tray-local state
  into shared settings. The old tray value is not migrated.
- The Windows tray build needs a compile check against the new Rust surface before the
  change ships; the tray no longer compiles the modules it stopped needing.
- Display copy says **unknown** where it used to offer a default — an unattached tray, an
  unreachable server, and an unrecorded cadence are unknown, never a plausible-looking
  value ([honest unobservability](../rules/honest-unobservability.md) applied to surfaces).
- `docs/dash/architecture.md`, `docs/rules/kyberdash-display-layer.md`, and the agent
  instruction files under `dash/` state the same boundary, so an agent editing a surface
  meets the rule before it meets the code.

## Related

- [ADR 0023](0023-kyberdash-report-model-and-tray-ownership.md) — one report model,
  Rust-side HTTP, and the refresh lock; decision 3 superseded here
- [ADR 0032](0032-kyberdash-user-initiated-clean.md) — the clean whose re-import this fixes
- [ADR 0031](0031-kyberdash-capture-command-owns-harness-telemetry-keys.md) — capture owns
  the telemetry keys
- [KyberDash display-layer rule](../rules/kyberdash-display-layer.md) — the always-on rule
- [KyberDash architecture](../dash/architecture.md) — surface layer and tray
