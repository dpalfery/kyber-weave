---
id: adr/0047-kyberdash-self-spawns-through-one-guarded-chokepoint
title: "KyberDash Self-Spawns Go Through One Guarded Chokepoint, with Role and Depth Stamped on Every Child"
doc-type: adr
status: current
owner: dpalfery
last-reviewed: 2026-11-01
component: KyberDash
code-refs:
  - guardedSpawn
  - assertMayHost
  - allowRealSpawns
  - resolveJobSpawnTarget
  - ensureEngine
decided-by: []
supersedes: []
---

# ADR 0047: KyberDash Self-Spawns Go Through One Guarded Chokepoint, with Role and Depth Stamped on Every Child

## Status

Accepted, 2026-10-30. Records the decision that every process KyberDash starts from its own
code (a job child, a receiver child, an engine child, and the install-time tool calls that
run inside the bundle) passes through one module, `dash/src/process/spawn-guard.ts`. The
module stamps a role and a depth on each child, refuses spawns that a role or the test
environment forbids, and records each child it starts.

## Context

KyberDash starts its own children. The web server hosts a `JobHost` that runs
`kyberdash dash refresh` on a schedule, a `ReceiverHost` that runs the OTLP receiver, and
`ensureEngine` starts a detached engine. Each of those children can host a dashboard, which
starts a `JobHost`, which starts another child.

On 2026-10-30 this chain ran away in the test suite. Two Playwright boot scripts called the
dashboard entry point with no spawner injected, so the default spawn target re-ran the boot
script itself (`argv[1]`). Each boot booted a dashboard, whose job host spawned another boot.
Every link stayed alive as a listening server, and the test runner's kill reached only the
`npx` wrapper. The chain reached 536 node processes and crashed three developer machines.

The defect had three independent causes, and each one is a separate fact that a future
change could reintroduce:

- the spawn target accepted any `argv[1]`, so a non-CLI entry could be re-run;
- a child process could host a dashboard and spawn again, with nothing that said it was a child;
- a test process could start a real child with no opt-out required, so a test that forgot
  to inject a spawner ran for real.

KyberDash's self-spawn call sites sit in several modules under `dash/src` (`jobs/host.ts`,
`jobs/receiver-host.ts`, `engine/ensure.ts`, `install/node-deps.ts`). Before this decision
each one decided its own spawn options and nothing else.

## Decision

1. **One chokepoint.** Every KyberDash self-spawn calls `guardedSpawn(kind, program, args,
   options, spawnFn?)` in `dash/src/process/spawn-guard.ts`. `kind` is one of `job`,
   `receiver`, `engine` or `tool`. The module is the only place that imports
   `child_process` spawn for a self-spawn; a static test fails any other source file that does.
   External diagnostic and browser tools (`lsof`, `ss`, `netstat`, `open`, `xdg-open`, the
   Antigravity process queries) are not self-spawns and stay direct calls, each with its own
   bound.
2. **Role and depth are stamped on every child.** The child environment carries
   `KYBERDASH_SPAWN_ROLE` (the `kind`) and `KYBERDASH_SPAWN_DEPTH` (the caller's depth plus
   one; absent means 0). Four rules follow:
   - a process whose role is `job` or `receiver` never builds a job host or a receiver host
     (`assertMayHost` throws `SpawnedChildMayNotHostError`) and never makes a guarded spawn;
   - an `engine` process may host, because its job host spawns `job` children, but it never
     starts another engine;
   - any guarded spawn from depth 2 or deeper is refused. The longest legitimate chain is
     user or tray (0), then engine (1), then job or receiver (2);
   - an unreadable depth value is refused, not treated as zero.

   `ensureEngine` refuses to start or attach an engine from any spawned child, whatever its
   role, so a child started for one job never replaces the engine.
3. **The entry point is checked before a self-spawn.** A self-spawn target is valid only when
   the process is the single-file binary, or `argv[1]` resolves through `realpath` to the
   package's `src/launcher.ts` or `dist/cli.js`, or there is no `argv[1]` and the `kyberdash`
   name on `PATH` is used. Anything else is refused with a reason that names the path, and
   nothing is spawned. An injected program is taken as given.
4. **Test processes fail closed.** `KYBERDASH_SPAWN_GUARD=deny` is set by the vitest setup
   file and by the isolated Playwright config. Under `deny`, `guardedSpawn` refuses every
   spawn and records a hit, and the setup file fails the test that caused it. A test that
   needs a real child calls `allowRealSpawns(reason)` with a reason that states the bound.
   The opt-in lasts for that test only. Production never sets the variable.
5. **The allow-list is the audit.** Every spawn site in `dash/` (`child_process` spawn, exec
   and fork families, `new Worker`, Playwright `webServer`) is listed in
   `dash/tests/spawn-sites.allowlist.ts` with a non-empty `bound`. A site that is not listed,
   or an entry that no longer matches a site, fails `dash/tests/spawn-sites.test.ts`.
   The .NET side scans two roots, `tests/KyberWeave.Tests` (C# and shell) and `src/` (C#),
   for `Process.Start`, `new Process`, `ProcessStartInfo`, `ProcessRunner.Run` and
   `BuildStartInfo`. Its allow-list, `tests/KyberWeave.Tests/Fixtures/spawn-sites-allowlist.json`,
   keys each entry by a root-prefixed file, normalized line text and count. Production code
   carries a bound too: a shared executor default of 60 s, with longer limits for the
   operations that need them, and `ProcessRunner` kills the process tree at its timeout. One
   deliberate exception:
   `ProcessRunner.RunInteractive`, which runs `squad run` attended by a user, has no timeout by
   design.
6. **Every child is recorded.** Each child `guardedSpawn` starts is kept in memory and, when
   `KYBERDASH_SPAWN_LEDGER_FILE` names a file, appended to it. The end-of-run ledger uses that
   record to find and kill survivors (see the runbook).

### Alternatives considered

- **Per-site checks.** Each caller refuses on its own role and entry. Rejected: the rules
  would live in several places, and the next new spawn site would have none of them. The
  defect came from exactly that gap.
- **OS-level process limits (`ulimit -u`, `launchctl limit maxproc`).** Rejected. On macOS
  the limit counts every process the user owns, so a limit low enough to stop a storm also
  stops the owner's desktop applications, and restoring it needs the owner. The limit also
  kills the runaway only by refusing new processes, not by naming the cause.
- **Baseline-relative process caps.** Rejected for the watchdog, and out of this decision's
  scope. A baseline that is measured at the start of a run lets a developer's long-lived
  tools raise the level the storm must exceed. Absolute caps are in
  [the runbook](../dash/runbook.md#running-tests-safely).
- **Guard only the job host.** Rejected. The receiver and the engine also spawn, and the
  engine is the only child that may host, so a job-only rule leaves the same recursion open
  through the other two paths.
- **Trust the test runner's kill.** Rejected. The runner signalled only the `npx` wrapper,
  and the tree reparented to PID 1 when the run ended.

## Consequences

- **Every new self-spawn goes through `guardedSpawn`.** A new spawn site costs one call and one
  allow-list entry with a bound that a reviewer can check. A site that cannot state a bound
  is a finding, not a note.
- **Tests that need a real child opt in by name.** `allowRealSpawns` shows up in review. Tests
  that exercise a spawner inject a recording spawn function instead.
- **Environment variables carry the rules across processes.** A real-process test's
  grandchildren inherit `deny`, and a child inherits its role and depth. Anything that
  rebuilds the environment without those keys drops the guard, so the environment copy in
  `guardedSpawn` is the place to check when a new spawn path is added.
- **The allow-list is coarse.** An entry covers a file and a spawn-API name, not one line.
  A second unbounded `execFile` added to a file that already has an allowed one is not reported
  by the static scan. Review must read each new site against the entry's bound.
- **Indirect spawns through production APIs are not scanned.** Both audits match a spawn only
  where its API name appears in the file. A caller that reaches a spawn through another module's
  API is found at that module, not at the call site, and the allow-list entry for that module
  must carry the bound. The residuals are tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **The ledger forgives a child that exits within about 3 seconds.** Teardown waits that long
  for a pool worker to finish exiting before it reports a survivor, so a child that is still
  alive at the end of a run and gone within the window is not reported. Tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **The guard is only as strict as the environment it runs in.** A process started outside the
  test runner, with `deny` unset, is allowed to spawn. The watchdog and CI leftover step cover
  that case; this decision does not.
- **Full test runs take turns on one machine.** The watchdog also carries an opt-in, machine-wide
  test slot (`proc-guard.sh run --slot`). Load, not runaway processes, is the problem it answers:
  several harnesses on one machine each started a full `dotnet test` run, and the overlapping runs
  slowed each other and flaked the load-sensitive classes. The slot is an atomic `mkdir` lock
  directory under `~/.cache/kyber-weave/test-slots`, because macOS has no `flock`. A queued caller
  names the holder, the wait is bounded by `--slot-wait` with its own exit code 88, the queue wait
  does not count against `--timeout`, and a lock whose holder is gone is recovered. Alternatives
  rejected: serialising every guarded command (builds and format checks do not compete with a
  test run), and an OS-level limit, which cannot tell a full test run from other work. The count
  and the wait are flags, never environment variables, like the caps; the runbook documents the
  slot ([Taking turns: the test slot](../dash/runbook.md#taking-turns-the-test-slot)).
- **Windows has no watchdog.** The guard and the refusals run on every platform, and the
  ledger runs on Windows in ledger-entry-only mode: it checks the children it recorded by pid
  liveness and skips the descendant and orphan scans. It cannot tell a reused pid from its child.
  `scripts/proc-guard.sh` is POSIX-only. Tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **The receiver child is labelled `job`.** It shares the job spawner, so the `receiver` kind is
  defined but no path stamps it. Tracked in
  [the spawn-guard residuals todo](../todo/kyberdash-spawn-guard-residuals.md).
- **Some Playwright specs have no sanctioned invocation.** `spine`, `deep-links` and `numerals`
  run only through `dash/playwright.config.ts`, which is not the isolated path, and
  `refresh-filters` is not run in CI. Tracked in
  [the Playwright webServer specs todo](../todo/kyberdash-playwright-webserver-specs.md).
