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
under OS locks (`EngineOwner` over `engine.sock` and `JobsLease` over `jobs.lock`).

The 2026-11-15 update retires two safeguards this ADR originally introduced for the TypeScript
engine: the process ledger (`dash/tests/setup/process-ledger.ts`) and the
`scripts/proc-guard.sh` watchdog. Both existed to cap and reap the recursive Node child processes
that engine started, and both were removed with those children; the retired engine, ledger included,
is gone from the tree and lives in git history. The active safeguards are now `EngineOwner`,
`JobsLease`, the in-process `JobSchedulerService` and `OtlpCollectorService`, and the static
spawn-site audit in `ProcessSpawnSitesTests`.

## Context

In the former TypeScript KyberDash engine, the web server self-spawned Node child processes for
scheduled refresh jobs, the OTLP receiver, and detached engine startup (`dash/src/process/spawn-guard.ts`).
On 2026-10-30 a Playwright boot chain ran away in that suite when an unguarded default target
re-ran the boot script itself, reaching 536 Node processes and crashing three developer machines.
At that time the repository added three structural fixes:

- **Process ledger and watchdog for `dash/` runs (since retired):** Vitest and Playwright runs used
  `dash/tests/setup/process-ledger.ts` and `scripts/proc-guard.sh` to enforce absolute process caps
  and reap orphaned descendants.
- **In-process hosting (completed):** the C# engine replaced those spawns. `JobSchedulerService`
  (scheduled maintenance under `JobsLease`) and `OtlpCollectorService` (the loopback OTLP/HTTP
  receiver) run inside the `kyberdash web` process, so KyberDash starts no `job` or `receiver` child
  at all.
- **Static allow-listing for every C# spawn site:** every `Process.Start`, `new Process`,
  `ProcessStartInfo`, `ProcessRunner.Run`, `ProcessRunner.RunInteractive`, and `BuildStartInfo` call
  across `src/` and `tests/` (outside `tests/KyberWeave.RepoPolicy.Tests/`) must be listed with a
  stated bound in `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json`.

Once the second fix removed the last recursive spawn, the first had nothing left to guard: a run
starts no process tree of its own, so an absolute cap on that tree never trips on anything the
engine did. Keeping the wrapper only added a POSIX-only dependency, a fixed `npx`/`npm`/`cargo`
argv in every doc, and a second enforcement path for the full-suite rule that `review gates` and
`fast-loop.sh` already enforce.

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
4. **Commands run directly, with no wrapper.** Every `dash/` and `dash/tray/` command runs as
   `npm --prefix dash …`, `npx --prefix dash playwright test …` or
   `cargo … --manifest-path dash/tray/src-tauri/Cargo.toml …` from the repository root, and `.NET`
   commands run as `dotnet …`. The retired `scripts/proc-guard.sh` wrapper is gone. Full-suite runs
   are still reserved for the review council and CI, enforced by `kyber-weave review gates`
   (`full-suite: true`, `KW-REVIEW-033`) and `scripts/fast-loop.sh` (`--full` and `--scoped`,
   exit 89); see [ADR 0059](0059-full-test-runs-are-reserved-for-the-council-and-ci.md).
5. **Isolated Playwright boots.** Isolated E2E runs use
   `dash/playwright.isolated.config.ts` (`harness-collection.spec.ts`, `workers: 1`), and the spec
   boots and tears down its own C# `kyberdash` instance. The per-run process ledger was retired with
   the watchdog: with the Node children gone, a Vitest or Playwright run starts only its own test
   processes, so the static audit and each site's own bound are what remain
   (see [the runbook](../dash/runbook.md#running-tests-safely)).

### Alternatives considered

- **Retaining a Node subprocess self-spawn guard (`spawn-guard.ts`) and its ledger.** Obsoleted by the C# engine
  cutover: running `JobSchedulerService` and `OtlpCollectorService` in-process removes Node job/receiver children
  entirely, so neither the guard nor the ledger had a spawn left to police.
- **Keeping `scripts/proc-guard.sh` as a general process watchdog.** Rejected. It was POSIX-only, it
  required every documented command to be spelled as a wrapped argv, and its full-run refusal
  duplicated a rule `review gates` already enforces where a reviewer actually looks. A watchdog that
  guards a process tree the engine no longer creates is cost without coverage.
- **OS-level process limits (`ulimit -u`, `launchctl limit maxproc`).** Rejected. On macOS
  the limit counts every process the user owns, so a limit low enough to stop a storm also
  stops the owner's desktop applications, and restoring it needs the owner. The limit also
  kills the runaway only by refusing new processes, not by naming the cause.
- **Baseline-relative process caps.** Rejected for the retired watchdog, and out of this decision's
  scope. A baseline that is measured at the start of a run lets a developer's long-lived
  tools raise the level the storm must exceed.
- **Trust the test runner's kill.** Rejected for the retired watchdog. Runners signalled only the outer
  wrapper (`npx`), leaving descendants reparented to PID 1 when the run ended. The structural fix is
  what makes this moot: the engine starts no such descendants.

## Consequences

- **New process spawns fail the repo-policy suite until allow-listed.** Adding any `Process.Start`,
  `new Process`, `ProcessStartInfo`, or `ProcessRunner` call outside
  `tests/KyberWeave.RepoPolicy.Tests/` fails `ProcessSpawnSitesTests` until
  `tests/KyberWeave.RepoPolicy.Tests/Fixtures/spawn-sites-allowlist.json` records its file,
  normalized line, count, and explicit bound.
- **Indirect spawns through helper APIs are anchored at the helper.** The static scan matches
  process-creation tokens where they appear in source, so helper callers are bounded via the
  allow-listed helper entry.
- **There is no absolute process cap on a local run.** A run that hangs is stopped by the operator
  or by the tool's own timeout, and `review gates` still bounds each gate at its `timeout-seconds`.
  This ADR's remaining bounds are the allow-list and the per-call timeouts on each listed site.
- **Full-suite refusal no longer has a wrapper layer.** `review gates` refuses a `full-suite: true`
  gate with `KW-REVIEW-033` (blocking, exit 1, nothing started), and `fast-loop.sh --full`
  and `--scoped` exit 89 from a non-interactive shell; see
  [ADR 0059](0059-full-test-runs-are-reserved-for-the-council-and-ci.md).
- **Windows gets the same bound as every other platform.** The retired watchdog was POSIX-only, so
  Windows lacked its cap. The static audit runs on every platform, so the bound it gives is the same
  everywhere; nothing platform-specific remains.
- **The retired guard's own residuals are archived, not fixed.** Their findings are kept for
  provenance in
  [the archived spawn-guard residuals todo](../archive/todo/kyberdash-spawn-guard-residuals.md);
  they describe code that no longer ships.
- **Some Playwright specs have no isolated `beforeAll` boot.** `spine.spec.ts`, `deep-links.spec.ts`,
  and `numerals.spec.ts` run against `dash/playwright.config.ts` rather than
  `dash/playwright.isolated.config.ts`. Tracked in
  [the Playwright webServer specs todo](../todo/kyberdash-playwright-webserver-specs.md).
