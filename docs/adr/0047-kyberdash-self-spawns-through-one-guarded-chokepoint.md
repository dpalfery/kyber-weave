---
id: adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint
title: "KyberDash Self-Spawns Go Through Bounded Process Calls and Allow-Listed Sites"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-15
component: KyberDash
code-refs:
  - ProcessRunner
  - ProcessSpawnSitesTests
  - EngineOwner
  - TrySpawnDetachedEngine
  - JobsLease
decided-by: []
supersedes: []
---

# ADR 0047: KyberDash Self-Spawns Go Through Bounded Process Calls and Allow-Listed Sites

## Status

Accepted, 2026-10-30; updated 2026-11-15 after the C# engine cutover. Records the decision that
every OS process started from `KyberWeave` and from the KyberDash hosts in `dash/server/` goes through a bounded
`ProcessRunner.Run` call or an explicitly allow-listed spawn site verified by
`ProcessSpawnSitesTests`, while single-instance engine and background job ownership stay in-process
under OS locks (`EngineOwner` over `engine.sock` and `JobsLease` over `jobs.lock`). In the `dash/`
UI/test package, Playwright and Vitest runs stay bounded by `dash/tests/setup/process-ledger.ts`
and `scripts/proc-guard.sh`.

## Context

In the former TypeScript KyberDash engine, the web server self-spawned Node child processes for
scheduled refresh jobs, the OTLP receiver, and detached engine startup (`dash/src/process/spawn-guard.ts`).
On 2026-10-30 a Playwright boot chain ran away in that suite when an unguarded default target
re-ran the boot script itself, reaching 536 Node processes and crashing three developer machines.

Three structural fixes eliminated that failure mode permanently:
- **C# in-process hosting:** Scheduled maintenance (`JobSchedulerService` under `JobsLease`) and the OTLP
  receiver (`OtlpCollectorService`) run in-process inside the `kyberdash-api` host, eliminating recursive Node
  job and receiver child spawns altogether.
- **Explicit allow-listing for every C# spawn site:** Every `Process.Start`, `new Process`,
  `ProcessStartInfo`, `ProcessRunner.Run`, `ProcessRunner.RunInteractive`, and `BuildStartInfo` call
  across `src/` and `tests/` (outside `tests/KyberWeave.RepoPolicy.Tests/`) must be listed with a
  stated bound in `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`.
- **Process ledger and watchdog for `dash/` runs:** Vitest and Playwright runs use
  `dash/tests/setup/process-ledger.ts` and `scripts/proc-guard.sh` to enforce absolute process caps
  and reap orphaned descendants.

## Decision

1. **Every C# process call is bounded and allow-listed.** In the `dash/server` hosts, external process
   launches are limited to the OS browser opener (`Program.RunDetached`, 5 s timeout via
   `ProcessRunner.Run`), tray codesign/quit helpers (`TrayMenubarInstaller`, bounded timeouts via
   `ProcessRunner.Run`), and detached engine handoff (`Program.TrySpawnDetachedEngine`, which
   starts `kyberdash web --no-open` during `kyberdash ensure` and is bounded to one instance per
   `stateDir` by `EngineOwner` on `engine.sock` / `engine.json` plus a 6 s readiness poll).
2. **In-process singleton ownership replaces role/depth self-spawns.** `JobSchedulerService` coordinates
   refresh ownership through `JobsLease` (`jobs.lock`) and `OtlpCollectorService` runs in-process, so
   KyberDash never spawns `job` or `receiver` child processes. `EngineOwner` binds `engine.sock`
   atomically so at most one engine serves a state directory.
3. **Static spawn-site audit across C# (`ProcessSpawnSitesTests`).**
   `tests/KyberWeave.RepoPolicy.Tests/ProcessSpawnSitesTests.cs` scans `src/` and `tests/` (outside
   `tests/KyberWeave.RepoPolicy.Tests/`) for `Process.Start`, `new Process`, `ProcessStartInfo`,
   `ProcessRunner.Run`, `ProcessRunner.RunInteractive`, and `BuildStartInfo`. Every occurrence must
   match `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`, keyed by
   repository-relative file path, normalized line text, count, and explicit `bound`. Production
   `ProcessRunner.Run` calls kill the process tree on timeout (typically 5 s–300 s); the one
   intentional unbounded exception is `ProcessRunner.RunInteractive`, used for attended `squad run`.
4. **Test isolation and Playwright rules.** All `dash/` and `dash/tray/` npm/npx/cargo invocations
   from agent shells run through `scripts/proc-guard.sh run --`, which enforces `--max-node` /
   `--max-tree` caps and reaps the process tree on exit 86 or leaks on exit 87. Isolated E2E runs
   use `dash/playwright.isolated.config.ts` (`harness-collection.spec.ts`, `workers: 1`, which
   boots and tears down its own C# `kyberdash` instance in `beforeAll`).
5. **Process ledger for JS/Playwright suites.** Vitest and Playwright setup files register spawned
   PIDs under `dash/tests/setup/process-ledger.ts` and `KYBERDASH_SPAWN_LEDGER_FILE`, reaping any
   survivors at teardown (see [the runbook](../dash/runbook.md#running-tests-safely)).

### Alternatives considered

- **Retaining a Node subprocess self-spawn guard (`spawn-guard.ts`).** Obsoleted by the C# engine
  cutover: running `JobSchedulerService` and `OtlpCollectorService` in-process removes Node job/receiver children
  entirely.
- **OS-level process limits (`ulimit -u`, `launchctl limit maxproc`).** Rejected. On macOS
  the limit counts every process the user owns, so a limit low enough to stop a storm also
  stops the owner's desktop applications, and restoring it needs the owner. The limit also
  kills the runaway only by refusing new processes, not by naming the cause.
- **Baseline-relative process caps.** Rejected for the watchdog, and out of this decision's
  scope. A baseline that is measured at the start of a run lets a developer's long-lived
  tools raise the level the storm must exceed. Absolute caps are in
  [the runbook](../dash/runbook.md#running-tests-safely).
- **Trust the test runner's kill.** Rejected. Runners signalled only the outer wrapper (`npx`),
  leaving descendants reparented to PID 1 when the run ended.

## Consequences

- **New process spawns fail the repo-policy suite until allow-listed.** Adding any `Process.Start`,
  `new Process`, `ProcessStartInfo`, or `ProcessRunner` call outside
  `tests/KyberWeave.RepoPolicy.Tests/` fails `ProcessSpawnSitesTests` until
  `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` records its file,
  normalized line, count, and explicit bound.
- **Indirect spawns through helper APIs are anchored at the helper.** The static scan matches
  process-creation tokens where they appear in source, so helper callers are bounded via the
  allow-listed helper entry (historically discussed in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md)).
- **The JS ledger forgives a child that exits within about 3 seconds.** Teardown waits that long
  for a pool worker to finish exiting before reporting a survivor. Tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **Full test runs take turns on one machine.** The watchdog carries an opt-in test slot, machine-wide
  for one user account (`proc-guard.sh run --slot`). Load, not runaway processes, is the problem it
  answers: overlapping full runs slow each other and flake load-sensitive classes. The slot is an
  atomic `mkdir` lock directory under `~/.cache/kyber-weave/test-slots` (macOS has no `flock`),
  bounded by `--slot-wait` (exit 88) without counting against `--timeout`. The same wrapper refuses
  full test runs from a developer agent unless `--council` is passed; see
  [ADR 0059](0059-full-test-runs-are-reserved-for-the-council-and-ci.md).
- **Windows has no POSIX watchdog.** `scripts/proc-guard.sh` is POSIX-only; the JS ledger runs in
  ledger-entry-only mode on Windows. Tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **Some Playwright specs have no isolated `beforeAll` boot.** `spine.spec.ts`, `deep-links.spec.ts`,
  and `numerals.spec.ts` run against `dash/playwright.config.ts` rather than
  `dash/playwright.isolated.config.ts`. Tracked in
  [the Playwright webServer specs todo](../todo/kyberdash-playwright-webserver-specs.md).
