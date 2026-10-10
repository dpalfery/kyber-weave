---
id: rules/kyberdash-display-layer
title: The KyberDash tray and web dashboard are display layers
doc-type: rule
status: current
owner: dpalfery
last-reviewed: 2026-10-10
decided-by:
  - adr/0033-kyberdash-surfaces-are-display-layers
---

# The KyberDash tray and web dashboard are display layers

> **THE TRAY AND THE WEB DASHBOARD ARE DISPLAY LAYERS. NEITHER CONTAINS ISOLATED FEATURE
> LOGIC, SCHEDULERS OR JOBS. ALL LOGIC AND JOBS (REFRESH SCHEDULING, CLEAN, IMPORT, PAUSE)
> LIVE IN THE SHARED KYBERDASH APPLICATION (THE CLI ENGINE AND THE `kyberdash web` SERVER
> BUILT FROM IT) THAT BOTH SURFACES SHARE. BOTH SURFACES USE ONE DATASTORE (canon.db,
> resolved by dash/src/canon/paths.ts resolveCanonDbPath) AND ONE API LAYER
> (/api/kyber/*). NO AGENT OR DESIGN MAY VIOLATE THIS.**

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
- **Host the OTLP receiver.** Receiver lifetime belongs to the engine's `ReceiverHost`.
- **Import engine value code.** `dash/web/**` and `dash/tray/ui/src/**` may not import a
  *value* from `dash/src/**` beyond the short commented allowlist in
  `dash/src/architecture/display-layer.test.ts` (pure formatters and shared data tables).
  `import type` is always allowed: a type carries no behaviour. Test files and fixtures are
  exempt from both scans in the shell: a surface's test may import engine code to build a
  realistic fixture, and may use timers to drive a fake clock, because it is never shipped.
  The exemption is deliberate and asserted as such in the guard's own self-tests.
- **Compute domain results in the browser.** Cost-basis mismatch, token totals, finding
  ranking, coverage — anything the engine decides. A number the UI derives is a number that
  can disagree with the CLI.

## Where the logic lives

| Concern | Owner |
|---|---|
| Scheduled refresh, maintenance pass, manual/import/clean children, pause | `JobHost` (`dash/src/jobs/host.ts`) |
| The OTLP receiver child and its restart/backoff | `ReceiverHost` (`dash/src/jobs/receiver-host.ts`) |
| Which process is allowed to schedule at all | the jobs lease on `jobs.lock` (`dash/src/jobs/lease.ts`) |
| Folder import | `dash/src/refresh/folder-import.ts` |
| Database clean | `dash/src/clean/clean.ts` |
| Shared settings (`settings.folder_import.scheduled`, `settings.jobs.paused`, `settings.jobs.refresh_cadence_minutes`, `settings.receiver.hosted`) | `canon.db` metadata via `dash/src/settings/shared-settings.ts` |
| Serving them | the `kyberdash web` server (`dash/src/cli/web.ts`), which hosts both of the above |

`JobHost` and `ReceiverHost` are hosted by the `kyberdash web` server — the same binary as
the CLI, so a job child and a terminal-started command arbitrate through the same refresh
lock. At most one host holds `jobs.lock`, so at most one process schedules.

## Enforcement

`dash/src/architecture/display-layer.test.ts` is the guard: a source scan over
`dash/tray/src-tauri/src/**`, `dash/tray/ui/src/**` and `dash/web/src/**` that fails with the
offending file, line, construct and reason, and names this document. It runs three scans — Rust
scheduling, TypeScript engine value imports, and TypeScript timers — and each is checked on
in-memory fixtures first, so a green repository scan means "no violations", never "the scanner
found nothing". The one allowlisted timer is
`dash/web/src/lib/storeGeneration.ts`, on the read-only-poll grounds above; adding another
requires the same written argument. It runs under

```bash
npm --prefix dash run test
```

A rule enforced only by a reviewer's memory is a rule that decays. If a change requires
something the scan rejects, the fix is in the engine — or the scan's allowlist grows with a
written reason, never around the rule.

## If you are an agent about to edit `dash/tray` or `dash/web`

1. Is the behaviour you are adding something a *surface* decides? If it needs a store, a
   parser, a total, a ranking, or a timer — it belongs in `dash/src`, reached over
   `/api/kyber/*`.
2. Does it need a value import from `dash/src`? Only a pure formatter or shared data table
   qualifies; `import type` is free.
3. Are you adding a recurring timer, an argv literal, or a `Command::new`? Stop — `JobHost`
   and `ReceiverHost` already do this. A *poll* of read-only `/api/kyber/*` state is fine and
   belongs on `TIMER_ALLOWLIST` with that reason; a timer that starts or re-drives work is not.
4. Would the same answer have to be right in the CLI, the dashboard, and the tray? Put it
   in the engine once.
5. Run `npm --prefix dash run typecheck lint test check:reachable`, plus the tray's
   `cargo fmt --check && cargo clippy -- -D warnings && cargo test` when you touched Rust.

## Related

- [ADR 0033](../adr/0033-kyberdash-surfaces-are-display-layers.md) — the decision this rule
  records
- [ADR 0023](../adr/0023-kyberdash-report-model-and-tray-ownership.md) — the report model
  and Rust-side HTTP this rule keeps; its decision 3 is superseded
- [KyberDash architecture](../dash/architecture.md) — the surface layer, the API contract,
  and the tray
- [Rules index](README.md)
