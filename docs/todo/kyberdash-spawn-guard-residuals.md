---
id: todo/kyberdash-spawn-guard-residuals
title: Close the known residuals of the KyberDash spawn guard and process ledger
doc-type: todo
component: KyberDash
status: draft
owner: dpalfery
last-reviewed: 2026-10-30
---

# Close the known residuals of the KyberDash spawn guard and process ledger

The [test process storm guard plan](../archive/plans/2026-10-30-test-process-storm-guard.md) put
every KyberDash self-spawn behind one chokepoint (`dash/src/process/spawn-guard.ts`), added an
end-of-run process ledger (`dash/tests/setup/process-ledger.ts`), and added two static spawn-site
audits. Its findings drain on 2026-10-30 left these residuals. None of them reopens the 2026-10
storm: each layer of that plan stops it alone. They are gaps in how precise the guard and the
audits are.

1. **The receiver child is labelled `job`.** `ReceiverHost` uses the shared `nodeSpawner`, which
   calls `guardedSpawn` with kind `job`, so the `otel --serve` child carries
   `KYBERDASH_SPAWN_ROLE=job`. The `receiver` kind exists and is refused the same way, but no path
   stamps it. Behaviour is the same (a `job` and a `receiver` may neither host nor spawn). Give
   `createNodeSpawner` a kind parameter and build the receiver host's default spawner with
   `receiver`, so the label, ADR 0047 decision 2 and the architecture diagram agree.
2. **The ledger forgives a child that exits within the settle window.** A ledger child that
   exits on its own within about 3 seconds of the end of the run is not reported. A test that
   leaks a short-lived child therefore passes. Decide whether a ledger child still running at
   teardown start should fail the run even when it exits during the settle window.
3. **The dash allow-list is per file and spawn-API pattern.** `dash/tests/spawn-sites.test.ts`
   keys entries by file and pattern, so a second site using an already-listed pattern in an
   already-listed file passes without a stated bound. The .NET audit
   (`tests/KyberWeave.Tests/ProcessSpawnSitesTests.cs`) already keys by file, normalised line text
   and count. Move the dash audit to the same key, or to a count per entry.
4. **Spawns reached through another module's API are not scanned.** Both audits match spawn calls
   textually. A test that starts a process by calling a production API defined in another file
   (for example a .NET test calling a production `ProcessRunner` user, or a dash test calling a
   production helper that spawns) is covered only by the guard and the ledger, not by the audit.
   Decide whether the audits should follow such calls, or record that the runtime layers own them.
5. **Windows.** `scripts/proc-guard.sh` runs on macOS and Linux only, so a Windows run has no
   absolute process cap. The ledger runs on Windows in ledger-entry-only mode: it checks its own
   entries by pid only (no descendant or orphan scan) and cannot tell a reused pid from its child, so
   teardown could signal an unrelated process that took a recorded pid. Decide whether Windows gets
   a watchdog (for example a PowerShell equivalent) and a start-time or command check before the
   ledger kills a pid there.
6. **The watchdog's orphan link can signal an unrelated process.** In `scripts/proc-guard.sh`
   (the post-run check, about lines 396 to 401), a re-parented process whose command line carries
   the repository root and that started within the run's age window is killed when the run's
   `seen` list does not name it. A detached process of the same user that carries the root on a
   path boundary and started after the run began therefore gets TERM and KILL. Suggest: require
   membership in `seen`, or match only a runner-tool pattern (`node_modules/vitest`,
   `node_modules/playwright`), and report every other candidate without signalling it.
7. **The ledger's root match has no leading boundary.** In `dash/tests/setup/process-ledger.ts`
   (about line 98), `namesRoot` checks the repository root with a trailing lookahead only, so a
   path that ends in the same suffix as the root also matches. Suggest a leading
   `(?<![\w.-])` lookbehind before the root, with a test for the suffix case.
8. **An undocumented test hook can disable signalling.** `scripts/proc-guard.sh` reads
   `KW_PROC_GUARD_KILL` (about lines 161 to 163) and, when it is set, records each signal instead of
   sending it. The script's header does not list this hook. If it were ever set in a real
   environment, per-pid signalling would be a no-op. Document it in the header, and refuse it
   outside the test suite or remove it.
9. **The watchdog's `seen` list grows without bound.** `scripts/proc-guard.sh` appends every
   descendant pid on each 0.5 s poll (about line 333) with no de-duplication, so the list grows for
   the length of the run. Keep it de-duplicated (`sort -u`, or an associative array).
10. **An unreproduced 1500-second silent hang under the watchdog.** On 2026-10-31,
    `scripts/proc-guard.sh run --timeout 1500 -- dotnet run --project src/KyberWeave.Cli -- review gates . --all`
    hung once. The watchdog killed it at the timeout (exit 124). The node count stayed flat at 5 to 6,
    and the gate `test` never finished. Two later full runs, one plain and one under a pty, finished
    30 of 30 gates in about 200 seconds, so the hang did not reproduce. Candidates, unverified:
    - the outer `dotnet run` restores or builds with no timeout of its own (a NuGet or MSBuild lock);
    - one gate is held to its 900-second default because `ProcessRunner` waits for the output pipes
      to close;
    - the agent harness had moved the guarded command to the background, because a foreground
      command over about 600 seconds is backgrounded. That is why runbook guidance now keeps
      `--timeout` at or below 540 from an agent tool.

    `review gates` prints only at the end, so a slow run and a stuck run look the same. Progress
    output is the follow-up. Reproduce with a pty and the same command, and record which phase
    stops.

This todo is complete when each item is either fixed, with a test, or recorded in ADR 0047 or
`docs/dash/architecture.md` as an accepted limit with its reason.
