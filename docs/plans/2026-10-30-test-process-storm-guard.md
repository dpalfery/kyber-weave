---
id: plans/2026-10-30-test-process-storm-guard
title: "Test process storm guard — defense in depth so a test run can never fork-bomb the machine"
doc-type: plan
status: current
component: KyberDash
owner: dpalfery
last-reviewed: 2026-10-30
development-mode: test-first
documentation-impact: extends-component
impact-components: [KyberDash, CI Pipelines, ReviewCouncil]
---

# Test process storm guard — defense in depth so a test run can never fork-bomb the machine

**Status: Ready — approved for execution 2026-10-30 (AP1); no task started.**

**Development mode:** `test-first` (supplied in the packet). **Autonomy:** `autonomous` (supplied in
the packet; matches the repository root `AGENTS.md` Config Reg `<autonomy>`).

Plan authored on branch `fix/opencode-v2-capture` (packet). Implementation branch: see D1.

**Hard safety rule for this plan's investigation and every executor.** No test, e2e, Playwright,
vitest, `dotnet test` or dashboard run until the step that allows it in §6 "Execution safety
order". The defect below crashed the owner's Macs three times. The investigation behind this plan
was static reading only.

## 1. Problem and goal

### 1.1 Diagnosed defect (confirmed in code; live process listing before the last crash)

1. `dash/e2e/harness-collection-boot.ts:24` and `dash/e2e/refresh-filters-boot.ts:19` call
   `runWebDashboard` with no `program`, `jobSpawner` or `receiverSpawner`.
2. `acquireHostedServices` (`dash/src/cli/web.ts:825-848`) then defaults the job program through
   `resolveJobSpawnTarget(currentSpawnRuntime(isSea))` (`dash/src/cli/spawn-target.ts:126-142`):
   `process.execPath` + `execArgv` (the tsx loader) + `process.argv[1]`, which is the boot script.
3. `JobHost.start` (`dash/src/jobs/host.ts:216-243`) runs the first tick at once and launches
   `[program, ...programArgs, 'dash', 'refresh', '--trigger', 'scheduled']` through `nodeSpawner`
   (`host.ts:121-135`).
4. The boot script ignores its argv, builds a fresh temp world and boots another full dashboard,
   whose JobHost spawns again. The chain is unbounded. Every link stays alive (it is a listening
   server), and the specs' `child.kill('SIGTERM')` hits only `npx`
   (`harness-collection.spec.ts:103,153`; `refresh-filters.spec.ts:33,71`), so the chain reparents
   to PID 1 when Playwright exits.
5. Observed: 446 leftover `node … harness-collection-boot.ts dash refresh --trigger scheduled`
   processes; an earlier Jetsam report showed 536 node processes and 92.5 GB on a 48 GB Mac.
   Reproduced on 3 Macs and 2 accounts.

### 1.2 Owner requirement

Defense in depth, so tests can never DDoS the machine again. Eight layers, each with a RED test
that cannot itself start a storm (fake spawners and unit seams; any real-process test is bounded
and cleans up after itself):

1. Fix both e2e boot scripts.
2. Recursion guard: spawned children carry an env marker and never host or spawn further.
3. Entry-point guard: the default spawn target re-runs only the real CLI entry.
4. Fail-closed spawning under vitest and Playwright unless a test opts in.
5. Leftover-process detection that fails the run and kills survivors.
6. An absolute process-cap watchdog wrapper for agent and local gate runs, with `AGENTS.md`
   guidance, wired into the fast loop and review gates.
7. An audit of every spawn site in `dash/` and in the .NET tests: each bounded, or recorded as
   bounded.
8. A CI step that fails a job on leftover processes.

### 1.3 Goal

After this plan, each of these alone stops the 2026-10 storm: the boot scripts inject inert
spawners (L1); a spawned child refuses to host (L2); a non-CLI entry is never re-run (L3); a test
process cannot spawn a real child without opting in (L4); survivors fail the run and are killed
(L5); the watchdog kills the tree at an absolute cap (L6); and a new unguarded spawn site fails a
static test (L7). CI fails on leftovers (L8).

## Decisions

| ID | Question | Answer / decision | Mitigating and supporting information | Status / approval provenance |
|---|---|---|---|---|
| D0 | Development mode | `test-first`. | Supplied in the packet; repository default. | ANSWERED — packet |
| D1 | How does this plan relate to the paused `2026-10-30-opencode-v2-capture` plan, and on which branch is it built? | **This plan blocks that plan's G1, G2 and G3.** It is built on its own branch `fix/test-process-storm-guard` cut from `origin/main`, merged first as its own PR; then `fix/opencode-v2-capture` is rebased onto `main` and the v2 plan's gates run through this plan's watchdog (`scripts/proc-guard.sh`). This plan file is committed on the new branch (cherry-picked from where it was authored). The v2 plan's own Execution state must record "blocked on `2026-10-30-test-process-storm-guard`" (the conductor makes that edit; this architect pass may write only this plan and its index row). | Both boot scripts predate the v2 branch (`harness-collection-boot.ts` carries the 2026-10-28 harness-collection plan's comments; `refresh-filters-boot.ts` still sets the dead `CODEBURN_DASH_DIR`), so the defect is on `main` and every branch (UNVERIFIED against `origin/main`: no git command was run). A separate PR protects `main`, every other branch and CI soonest, and keeps the v2 review scoped. Cost of being wrong: a rebase of the v2 branch over `dash/e2e/harness-collection-*` and `dash/src/cli/web.ts`, which both touch; small and mechanical. Alternative rejected: build on `fix/opencode-v2-capture`, which delays protection until the larger v2 PR merges and mixes two reviews. | ANSWERED — autonomous: low-confidence, no authoritative source (cheap to reverse) |
| D2 | One chokepoint or per-site checks? | **One module, `dash/src/process/spawn-guard.ts`, owns every KyberDash self-spawn.** `nodeSpawner` (job and receiver children), `DetachedLauncher` (engine) and `install/node-deps.ts` call `guardedSpawn(kind, program, args, options, spawnFn?)`. The guard applies, in order: test fail-closed (D4), recursion and depth (D3), entry-point validity for self-spawns (D5), env-marker injection (D3), and the process-ledger hook (D7). External bounded tools (`lsof`/`ss`/`netstat` in `otel/receiver.ts`, `antigravity.ts` `execFileText`, `openBrowser` in `web.ts`) are not self-spawns; they stay direct and are recorded as bounded in the L7 audit. | One place to reason about, one place the L7 scan points to. The low-level `spawnFn` parameter (default `child_process.spawn`) is the unit seam that lets every RED test observe argv and env without starting a process. | ANSWERED — autonomous: decided (design consolidation; cheap to reverse) |
| D3 | Recursion marker shape and rules | **Two env vars set on every guarded child:** `KYBERDASH_SPAWN_ROLE` = `job` \| `receiver` \| `engine`, and `KYBERDASH_SPAWN_DEPTH` = parent depth + 1 (absent = 0). **Rules:** (a) a process whose role is `job` or `receiver` never builds a JobHost or ReceiverHost — `acquireHostedServices` throws `SpawnedChildMayNotHostError`, and `runWebDashboard` surfaces it as a non-zero exit with one stderr line naming the role; (b) a `job` or `receiver` process makes no guarded spawn of any kind; (c) an `engine` process may host (its JobHost spawns `job` children) but never starts another engine (`ensureEngine` returns `failed` with reason "an engine child may not start an engine"); (d) any guarded spawn at depth ≥ 2 is refused. The legitimate maximum chain is user/tray (0) → engine (1) → job or receiver (2). | `JobHost` spawns with no `env` option today (`host.ts:123`), so the child inherits the parent env; the guard adds the two keys to a copy of `process.env`. Before GREEN, the implementer confirms statically that no path reachable from `dash refresh`, `dash import-history`, `dash clean` or `otel --serve` needs a guarded spawn (the only expected spawns there are `worker_threads`, which are not processes, and the bounded external tools of D2). A path that does is reported back, not exempted. | ANSWERED — autonomous: low-confidence, no authoritative source (cost of wrong: a legitimate child path refused; caught by T2's static reachability check and the full suite) |
| D4 | Fail-closed test mode: how is it switched on, and how does a test opt in? | **`KYBERDASH_SPAWN_GUARD=deny`** is set by a new vitest setup file `dash/tests/setup/spawn-guard-setup.ts` (registered next to `network-guard-setup.ts`) and by the Playwright isolated config (D8), so every child of a test process inherits it. Under `deny`, `guardedSpawn` records a hit and throws `SpawnRefusedError` (the JobHost already turns a throwing spawner into a recorded failure, `host.ts:321-326`). The setup file's `afterEach`/`afterAll` fail the test that caused a hit, with the hit list, exactly as the network guard does. **Opt-in:** `allowRealSpawns(reason)` from `dash/tests/setup/spawn-guard.ts`, scoped to the calling test and reset in `afterEach`; each opt-in names its bound in `reason` and is listed in the L7 allow-list. Production never sets the variable; a user who sets it only makes KyberDash refuse to spawn. | Mirrors the accepted network guard (D10 of the 2026-10-25 plan; `dash/tests/setup/network-guard-setup.ts`). An env var, unlike a module flag, crosses the process boundary, so a real-process test's grandchildren are fail-closed too. Tests that spawn raw `child_process` directly (lock tests, `web-listening.test.ts`, `instance.test.ts`) are not blocked by the guard; their children inherit `deny`, and each is recorded in the L7 audit with its bound. | ANSWERED — autonomous: decided from the existing network-guard pattern |
| D5 | Entry-point guard: what counts as the real CLI entry? | **A self-spawn target is valid only when** the process is a SEA (`execPath` is the CLI), or `realpath(argv[1])` equals `realpath` of the package's `src/launcher.ts` or `dist/cli.js` (package root found from the spawn-target module's own location), or `argv[1]` is absent and the fallback `kyberdash` from `PATH` is used (unchanged). **Anything else is refused:** `resolveJobSpawnTarget` returns `{ refused: '<reason naming argv[1]>' }`; JobHost and ReceiverHost record a failed job with that reason and spawn nothing; `ensureEngine` returns `failed` with it. An injected `program` (tests, the tray) is taken at face value as today. | Today the target is whatever `argv[1]` is: the boot script under Playwright, the vitest worker under vitest. Both are now refused, independently of L1 and L4. `dash/package.json` `bin.kyberdash` = `dist/cli.js` and `dev` = `tsx src/launcher.ts` are the two non-SEA entries. A global npm install resolves `dist/cli.js` through the bin symlink, which `realpath` follows. | ANSWERED — autonomous: decided from `package.json` entries and `spawn-target.ts` |
| D6 | Watchdog: language, cap semantics, default caps, behaviour on trip | **`scripts/proc-guard.sh` (POSIX bash; macOS and Linux).** Modes: `run [--max-node N] [--max-tree M] [--timeout S] -- <cmd…>` and `check-leftovers [--since <epoch>] [--root <dir>]`. `run` starts the command in its own process group, polls every second, and trips when **the absolute count of `node` processes owned by the current user** ≥ `N` (default 128, env `KW_MAX_NODE_PROCS`) **or** the wrapped tree's descendant count ≥ `M` (default 64, env `KW_MAX_TREE_PROCS`) **or** the timeout passes (default none). On trip: SIGTERM the process group and every descendant, then after 2 s SIGKILL; also kill orphans (parent PID 1) whose command line contains the repository root and that started after the wrapper; print the counts and the top commands; exit **86**. On normal exit: run the same reap; if anything was reaped, exit **87** even when the command passed; otherwise exit with the command's code. `check-leftovers` lists matching survivors and exits 87 when any exist (used by CI, D9). On other platforms the script prints a warning and runs the command unwrapped. | Owner asked for an absolute, not baseline-relative, node cap that kills the tree. The tree cap catches a storm early when the user-wide count is still under the cap because of the owner's many long-lived MCP and harness node processes. Defaults: a full vitest run uses about one worker per core plus tsx loaders, well under 64 descendants; 128 user-wide leaves room for those servers and trips long before the observed 446. A count-source override (`KW_PROC_GUARD_PS`, a command printing `pid ppid uid etime command` lines) is the test seam. Cost of wrong defaults: a false trip (exit 86) on a heavily loaded dev machine; each is an env override. Windows is not covered (owner machines are macOS; CI is Ubuntu). | ANSWERED — autonomous: low-confidence, no authoritative source (default caps are cheap to change) |
| D7 | Leftover detection: how are survivors found, given that a storm's children reparent to PID 1? | **Three detectors in one shared Node module, `dash/tests/setup/process-ledger.ts`:** (1) **ledger** — global setup creates a run directory and sets `KYBERDASH_PROCESS_LEDGER_DIR`; `guardedSpawn` writes one file per child (`pid`, start time, argv); teardown checks each pid with `process.kill(pid, 0)` and a command match (guards against PID reuse); (2) **descendants** — a `ps -A -o pid=,ppid=,command=` tree walk from the runner's PID; (3) **orphans** — processes with parent PID 1 whose command contains the `dash/` root and whose start is after the run start. Teardown SIGTERMs survivors, SIGKILLs after 2 s (process group first for detached ones), and **fails the run** listing them. Used by vitest `globalSetup` (new `dash/tests/setup/process-ledger-global.ts`) and Playwright `globalSetup`/`globalTeardown` (D8). On Windows, detectors 2 and 3 are skipped with a printed notice; the ledger still runs (`capture-windows` CI job runs vitest). | A ledger alone misses raw `child_process` tests; a descendant walk alone misses reparented orphans (the observed failure); the orphan scan alone is heuristic. Together they cover the observed case twice. | ANSWERED — autonomous: decided |
| D8 | Playwright: where do the guard and the ledger attach, given CI runs `npx --prefix dash playwright test dash/e2e/harness-collection.spec.ts` from the repository root, which does not load `dash/playwright.config.ts` (`ci.yml:810-814`)? | **A new `dash/playwright.isolated.config.ts`** for the self-booting specs (`harness-collection.spec.ts`, `refresh-filters.spec.ts`): no `webServer`, `workers: 1`, `globalSetup`/`globalTeardown` = the process ledger, and `process.env.KYBERDASH_SPAWN_GUARD = 'deny'` set at config load. CI and every doc invoke it explicitly with `--config dash/playwright.isolated.config.ts`. The existing `dash/playwright.config.ts` also gains the ledger hooks and passes `env: { KYBERDASH_SPAWN_GUARD: 'deny' }` to its `webServer`; if a spec served by that webServer needs a real scheduled refresh (implementer checks `spine`, `deep-links`, `numerals`, `opencode-live-gate` statically), the webServer gets `allow` for that config only, recorded in the L7 allow-list. **Both specs spawn their boot with `detached: true` and kill the process group** (`process.kill(-pid)`), not only `npx`. | An explicit `--config` is the only way to make the hooks load in CI's root invocation. The process-group kill closes the "SIGTERM reaches only npx" gap independently of the ledger. | ANSWERED — autonomous: decided from `ci.yml` and the Playwright config semantics (UNVERIFIED: Playwright's config resolution rule when run from the repository root is taken from the existing `ci.yml` comment, not re-checked against Playwright docs) |
| D9 | CI leftover step: which jobs, and what counts as a leftover? | **Jobs `ts-gates`, `dash-e2e` and `build-test`** each end with an `if: always()` step `scripts/proc-guard.sh check-leftovers --root "$GITHUB_WORKSPACE" --since <job-start>` that fails the job (exit 87) when any `node`, `tsx`, `npx`, `chromium`/`headless_shell` or `testhost` process whose command line contains the workspace path survives. `build-test` first runs `dotnet build-server shutdown` so MSBuild node reuse and `VBCSCompiler` are not counted. The job-start epoch is written to `$GITHUB_ENV` by a first step. | The runner's own processes (`Runner.Worker`, the setup-node action's embedded node) do not carry the workspace path in their command line. A job that leaves a survivor fails even when its tests passed, which is the owner's ask. | ANSWERED — autonomous: decided |
| D10 | Is the watchdog wired into review gates and the fast loop, or documented only? | **Both, plus documentation.** `scripts/fast-loop.sh` wraps each `npm --prefix dash …` and `dotnet test` invocation in `scripts/proc-guard.sh run --`. In `.kyber-weave/kyber-weave.yml`, the dash `ts-test` gate argv and the `dotnet test` gate argv become `[scripts/proc-guard.sh, run, --, …]`, with the matching config pin tests updated. `AGENTS.md` (root) and `dash/AGENTS.md` state that agents run local gates, tests and e2e only through `scripts/proc-guard.sh run --`. | Guidance alone is what failed three times; wiring makes the default path safe. Cost of wrong: a `kyber-weave.yml` change is a reserved path (review KW-REVIEW-008 → NEEDS_HUMAN, expected), and the gates stop working on a Windows dev host (none documented). | ANSWERED — autonomous: low-confidence, no authoritative source (reserved-path change; mitigated by owner review at the PR) |
| D11 | L7 audit: how is "bounded or recorded as bounded" enforced, not just written once? | **Two static scan tests with an allow-list**, each entry naming the file, the spawn call, and its bound (timeout, single owned child killed on close, lock-guarded single instance, `afterEach` kill, self-exit): (a) `dash/tests/spawn-sites.test.ts` scans every `.ts`/`.mts`/`.mjs` under `dash/` (excluding `node_modules`, `dist`, `.claude/worktrees`) for `child_process` imports, `new Worker(` and `Bun.spawn`; (b) `tests/KyberWeave.Tests/ProcessSpawnSitesTests.cs` scans `tests/` and `src/` `.cs` files for `Process.Start`, `new Process` and `new ProcessStartInfo`. A site not on the list fails the test with the file and line; a stale entry fails too. Unbounded sites found during the audit are fixed in the same task. | Makes the audit durable: the next new spawn site must state its bound to pass. Repository precedent for static scans: `ReleaseTests.cs` reads workflow YAML; `scripts/unreachable.mjs` scans `dash/`. | ANSWERED — autonomous: decided |
| D12 | When may the executor run the full suites and Playwright again? | **Strict order (§6 "Execution safety order"):** until T2b is GREEN, only the named single test files of T0–T2 run, each through the watchdog (T0's own first RED run is the one exception, being one `dotnet test` filter with one `sleep` child). The full `npm --prefix dash run test` runs only after T2b and T4b are GREEN, through the watchdog. **No local Playwright run** until the PR's CI `dash-e2e` job, including its leftover step, is green; then once locally through the watchdog with the isolated config. The dashboard (`kyberdash web`, `npm run dev -- web`) is never started by an executor of this plan. | The defect is a test-run storm; the guard layers must exist before the suites that triggered it run again. CI runners are ephemeral and cannot crash the owner's Mac. | ANSWERED — autonomous: decided from the packet's hard rule |
| D13 | ADR | **New ADR 0047 "KyberDash self-spawns go through one guarded chokepoint"**: the role/depth marker, the entry-point rule, fail-closed test mode, and the static allow-list; it constrains every future spawn site. The watchdog and CI step are documented in runbooks, not the ADR. | Expensive to revisit (every spawn site), with real rejected alternatives (per-site checks, OS-level limits such as `ulimit -u`, which on macOS counts every user process and kills unrelated apps). | ANSWERED — autonomous: decided from the ADR skill criteria |
| D14 | Should an OS-level limit (`ulimit -u`, `launchctl limit maxproc`) be part of the watchdog? | **No.** The watchdog counts and kills; it does not set OS limits. | `maxproc` counts all of the user's processes, so a limit low enough to stop a storm breaks the owner's desktop apps; raising it back requires the owner. | ANSWERED — autonomous: decided |
| AP1 | Approve and execute this plan? | **Approved.** The plan is Ready for execution as written: scale `extends-component` (KyberDash, CI Pipelines, ReviewCouncil), mode `test-first`, autonomy `autonomous`. D0–D14 are accepted as recommended, explicitly including D1 (separate branch `fix/test-process-storm-guard` cut from `origin/main`, merged first), D6 (watchdog caps and exit codes) and D10 (watchdog wired into the fast loop and review gates, including the reserved `.kyber-weave/kyber-weave.yml`). | The owner saw the plan's scale and mode before approving. The low-confidence decisions D1, D3, D6, D8 and D10 keep the reversal costs stated in their rows. | APPROVED — owner approval in chat ("Yes, build it"), 2026-10-30, relayed by the conductor as `AUTONOMOUS_APPROVAL: approve-and-execute`; autonomous: recommendation accepted for D1–D14 |

No hard-block escalation: every decision has a recommendation this plan stands behind.

## Design harvest

`documentation-impact: extends-component`; `impact-components: [KyberDash, CI Pipelines, ReviewCouncil]`.

| ID | Target component | Target doc type | Durable design item |
|---|---|---|---|
| H1 | KyberDash | adr | New ADR 0047: one guarded spawn chokepoint; role/depth marker and its four rules (D3); entry-point rule (D5); fail-closed test mode and opt-in (D4); static allow-list (D11); rejected alternatives (D13, D14). ADR index row. |
| H2 | KyberDash | architecture | `docs/dash/architecture.md`: the spawn chokepoint, which hosts spawn what with which role, the depth limit, the refusal outcomes (`SpawnedChildMayNotHostError`, refused spawn target, engine-from-engine), and that external tools are bounded direct calls. |
| H3 | KyberDash | runbook | `docs/dash/runbook.md`: running vitest and Playwright safely (watchdog, isolated config, `--config` invocation), what exit 86/87 mean and how to read the report, the opt-in for a real-process test, and how to clean up after an earlier storm (`scripts/proc-guard.sh check-leftovers`). Replaces the "verify" commands that run Playwright without the isolated config. |
| H4 | KyberDash | standard | `docs/standards/test/README.md`: a "Tests that start processes" section: fake spawners by default, `allowRealSpawns` with a stated bound, process-group kill, self-exit timeouts, the allow-list entries, and the .NET equivalent (`Kill(entireProcessTree: true)`, timeouts). |
| H5 | CI Pipelines | runbook | `docs/ci-pipelines/workflows-runbook.md`: the leftover step in `ts-gates`, `dash-e2e`, `build-test`; the `dash-e2e` `--config` invocation. |
| H6 | ReviewCouncil | architecture | `docs/code-review/architecture.md` (council and task gates) and root `AGENTS.md` §"The inner loop" / §"Before opening a PR": gates and the fast loop run through `scripts/proc-guard.sh`; exit 86/87. `dash/AGENTS.md`: the same rule for dash tests and e2e. |
| H7 | KyberDash | rule | `docs/rules/kyberdash-harness-collection.md`: the conformance spec runs with `--config dash/playwright.isolated.config.ts`. |

## Execution state

As of 2026-10-30 (closeout pass): code tasks T0–T8 done on branch `fix/test-process-storm-guard`. G1 final2
green (the executor's numbers, not re-run here). Council verdict `NEEDS_HUMAN` under KW-REVIEW-008 only (a
reserved-path change to `.kyber-weave/kyber-weave.yml`, expected by §7). The closeout mapping in §10 is
complete. **Not yet archived:** G2 (the PR's CI, including `dash-e2e` and its leftover steps) and G3 (local
Playwright through the watchdog, isolated config) have not run on a PR. §7 places the closeout after G2 and
G3, so the plan stays in `docs/plans/`. The plan file is also still untracked in this worktree, and `git mv`
needs it committed first.

**Dispatch tally (per failure cluster).**

| Failure-cluster key | Owning task | Dispatches | Last result |
|---|---|---|---|
| — | — | 0 | — |

### Findings drained 2026-10-30

Drained before the final council, after the conductor reported T0–T8 and G1 complete. Static
reading only; nothing was run. Three findings need rework (R1–R3) before the council; three are
captured as todos; the rest need no action.

| Finding | Disposition |
|---|---|
| F1 `attachOtelToEngine` writes `receiverHosted=on` before the spawn-target refusal | **Rework R1** (T3 scope: the plan names this caller). Confirmed: `dash/src/cli/register.ts:289-300` opens the store and writes the setting, then refuses. |
| F2 receiver child stamped `KYBERDASH_SPAWN_ROLE=job` | **Todo** [`kyberdash-spawn-guard-residuals`](../todo/kyberdash-spawn-guard-residuals.md) item 1. Behaviour equals D3 (both roles may neither host nor spawn); `docs/dash/architecture.md` "Known limits" already states it. Correction below. |
| F3 CI runs only `harness-collection.spec.ts`; webServer specs have no sanctioned invocation | **Rework R3** for one part: the unreferenced `dash/playwright.refresh-filters.config.ts` runs `refresh-filters.spec.ts` with no `deny` and no ledger hooks, a bypass of D8. **Todo** [`kyberdash-playwright-webserver-specs`](../todo/kyberdash-playwright-webserver-specs.md) for `spine`/`deep-links`/`numerals` and for adding `refresh-filters` to CI (the isolation gap §8 already put out of scope). The three `refresh-filters.spec.ts` repairs stand. |
| F4 .NET audit scans `tests/KyberWeave.Tests` only | **Rework R2**: D11 and the T6 row approved scanning `tests/` and `src/` (about 36 matches in about 18 `src/` files). Spawns reached through a production API from another file go to the residuals todo, item 4. |
| F5 ledger settle-window forgiveness; dash allow-list per (file, pattern) | **Todo** residuals items 2 and 3. Already documented as known limits in `docs/dash/runbook.md` and `docs/dash/architecture.md`. The .NET key (file, normalised text, count) is adequate. |
| F6 Windows: no watchdog; ledger checks entries only; pid reuse | **Todo** residuals item 5 (Windows pid reuse could signal an unrelated process). The missing watchdog and scans were accepted in D6, D7 and R5. |
| F7 task gates `NONE_APPLICABLE` for `.yml`/`.sh` edits | **Todo** [`task-gates-workflow-and-shell-edits`](../todo/task-gates-workflow-and-shell-edits.md) (ReviewCouncil). Not this plan's scope; the T0 and T7 tests read those files. |
| F8 `artifacts/` appears untracked | **No action.** `.gitignore:17` is `/artifacts/` in this worktree and in the primary checkout, and no negation rule re-includes it, so `artifacts/evidence/` is ignored and must stay out of the commit. Conductor: confirm with `git check-ignore -v artifacts/evidence` and `git ls-files artifacts` (must print nothing); never `git add -f` it. |
| F9 v2 plan blocked on this plan (D1) | **No action here.** The v2 plan's Execution state does not yet say "blocked on `2026-10-30-test-process-storm-guard`"; that conductor edit is still owed. After this PR merges: rebase `fix/opencode-v2-capture` onto `main`, then run its G1/G2/G3 through `scripts/proc-guard.sh run --` (Playwright with `--config dash/playwright.isolated.config.ts`). |

**Rework packets (before the council).**

- **R1 (F1).** RED, `test-dev`: in `dash/src/cli/register-otel-refused.test.ts`, after the refused
  run, assert the store at the test's `--db` path reads `receiverHosted` as `off` (migrate and open
  a `CanonStore` on it, `readSetting`, close). GREEN, `typescript-dev`: in `attachOtelToEngine`
  (`dash/src/cli/register.ts`), resolve the target and exit on refusal before opening the store
  and writing the setting. Run `cd dash && ../scripts/proc-guard.sh run -- npx vitest run src/cli/register-otel-refused.test.ts`.
- **R2 (F4).** RED, `test-dev`: widen `tests/KyberWeave.Tests/ProcessSpawnSitesTests.cs` to scan
  `src/` as well as `tests/KyberWeave.Tests` (paths in failures and in
  `Fixtures/spawn-sites-allowlist.json` must name which root), and add the `new Process` pattern
  D11 names; with no `src/` entries it fails listing every `src/` site. GREEN, `test-dev`: an entry
  with a stated bound for each site; `csharp-dev` fixes any production site with no bound (no
  timeout, no tree kill) found on the way. Check `GateRunner` (it starts the test suites),
  `SquadHarnessLauncher` and `SelfUpdater` first; their bounds were not read in this drain. Then `docs-dev`: ADR 0047 decision 5 and the "Known limits" paragraph of
  `docs/dash/architecture.md` say the .NET audit reads `tests/` and `src/`. Run
  `scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~ProcessSpawnSitesTests"`.
- **R3 (F3 part).** RED, `test-dev`: in `dash/tests/e2e-boot-isolation.test.ts`, a static case that
  every `dash/playwright*.config.ts` is either `playwright.config.ts` or
  `playwright.isolated.config.ts`, sets `KYBERDASH_SPAWN_GUARD=deny` (config or `webServer.env`)
  and wires the process-ledger `globalSetup`/`globalTeardown`; it fails on
  `playwright.refresh-filters.config.ts`. GREEN, `typescript-dev`: delete that file (nothing
  references it). Run `cd dash && ../scripts/proc-guard.sh run -- npx vitest run tests/e2e-boot-isolation.test.ts`.

After R1–R3: the task gate for each, then G1 again through the watchdog for the dash and .NET
suites and the two documentation checks. These are additive assertions inside the T3, T4 and T6
rows; R2 restores the approved T6 row. None weakens a row, so the plan stays Ready.

**Where the code differs from this plan's text (the code stands unless a rework above says otherwise).**

- Ledger (D7, §3.2): one JSON-lines file named by `KYBERDASH_SPAWN_LEDGER_FILE`, appended per
  child, plus an in-memory record (`spawnLedgerEntries()`), not `KYBERDASH_PROCESS_LEDGER_DIR`
  with one file per pid.
- `ensureEngine` (D3 (c)): refuses when the caller's role is `engine`, `job` or `receiver`, not
  only `engine`.
- Receiver child (D3, D2): stamped `KYBERDASH_SPAWN_ROLE=job`, because `ReceiverHost` uses the
  shared `nodeSpawner` (kind `job`). The `receiver` kind exists and is refused the same way. Todo
  item 1.
- .NET audit (D11): scans `tests/KyberWeave.Tests` only and has no `new Process` pattern. R2
  restores D11.
- Spawn kinds (§3.2): the fourth kind is `tool`, not `install`, and its children are stamped
  `KYBERDASH_SPAWN_ROLE=tool`, not left unchanged.

## 2. Investigation findings

All findings are from static reading of this checkout (`fix/opencode-v2-capture`, HEAD `db42167e`,
dirty working tree limited to `docs/`).

- **F1 — Boot scripts.** `harness-collection-boot.ts:24-42` passes `stateDir`, `maintenance`,
  filters, `port: 0`, `open: false`; `refresh-filters-boot.ts:19-26` passes only filters, `port`,
  `open` (no `stateDir`, so it also uses `~/.kyberdash` state). Neither passes `program`,
  `jobSpawner`, `receiverSpawner` or `receiverProber`.
- **F2 — Default target.** `acquireHostedServices` (`web.ts:840-842`) uses
  `resolveJobSpawnTarget(currentSpawnRuntime(isSea))` whenever `opts.program` is undefined.
  `resolveJobSpawnTarget` (`spawn-target.ts:126-142`) accepts any `argv[1]`.
- **F3 — Spawn on start.** `JobHost.start` arms the interval and awaits the first tick
  (`host.ts:216-220`); `runTick` launches `dash refresh --trigger scheduled` when due
  (`:232-243`); `launch` turns a throwing spawner into a recorded failure (`:320-326`).
  `nodeSpawner` passes no `env` (`:121-135`).
- **F4 — Receiver.** `ReceiverHost` defaults to `nodeSpawner` (`receiver-host.ts:190`) and spawns
  `[program, ...programArgs, 'otel', '--serve']` (`:300`) only when `receiverHosted` is `on`;
  restart backoff 1 s doubling to 60 s (`restartDelayMs`, `:106-109`); one owned child set
  (`:183`). Not the observed storm path, but the same unguarded target.
- **F5 — Engine.** `DetachedLauncher.start` spawns `detached: true, stdio: 'ignore'` and
  `unref()`s (`engine/ensure.ts:42-56`); `ensureEngine` uses it unless `startEngine` is injected
  (`:110-114`). Callers: `attachOtelToEngine` (which also calls `resolveJobSpawnTarget`) and
  `captureEngineSeams`. Bounded today only by the single-instance engine lock (ADR 0039).
- **F6 — Spec teardown.** Both specs spawn `npx tsx e2e/*-boot.ts` without `detached` and kill
  with `child.kill('SIGTERM')` (`harness-collection.spec.ts:103,153`;
  `refresh-filters.spec.ts:33,71`): the signal reaches `npx`, not the tsx node grandchild.
- **F7 — Playwright configs.** `dash/playwright.config.ts` has a `webServer`
  (`npm run build:dash && npm run dev -- web --no-open --port 4747`, `reuseExistingServer` outside
  CI) and no global hooks. CI's `dash-e2e` runs from the repository root without `--config`
  (`ci.yml:844-845`), and its comment states that this keeps `dash/playwright.config.ts` unloaded
  (`:810-814`).
- **F8 — Vitest setup.** `dash/vitest.config.ts:29-34` registers `env-isolation.ts`,
  `network-guard-setup.ts`, `web/vitest.setup.ts`; no `globalSetup`. The network guard records
  hits and fails the causing test in `afterEach`/`afterAll` (`tests/setup/network-guard-setup.ts`).
- **F9 — Spawn sites in `dash/` (non-test).** `child_process` in `cli/web.ts` (`execFile`,
  `openBrowser`, `:312`), `engine/ensure.ts` (`spawn`, detached), `jobs/host.ts` (`spawn`),
  `install/node-deps.ts:24` (`spawn`, `shell: false`, no timeout seen), `otel/receiver.ts:342-350`
  (`lsof`/`ss`/`netstat` with a timeout), `providers/antigravity.ts:239-241` (`execFile`, timeout
  3 s, 1 MiB buffer); `worker_threads` in `ingest/parse-workers.ts:138` (threads, not processes);
  scripts `scripts/release-acceptance/run.mjs`, `scripts/sea-mcp-smoke.mjs`,
  `scripts/sea-web-smoke.mjs`, `scripts/upgrade-path/run.mjs`,
  `scripts/opencode-live-gate/run.mjs`. Tray (Rust): `SystemServerSpawner`
  (`tray/src-tauri/src/supervisor.rs:408-458`) with backoff and `kill_tree`.
- **F10 — Real-process vitest tests.** `src/cli/web-listening.test.ts:75` (spawns
  `node --import tsx src/launcher.ts web --port 0`, SIGINT then SIGKILL after 2 s),
  `src/cli/web-spawn-target.test.ts:192,211,225` (`spawnSync`, 120 s timeout),
  `src/engine/instance.test.ts:84`, `src/refresh/lock*.test.ts`, plus `spawnSync` CLI tests
  (`cli-commands`, `cli-provider-validation`, `cli-refresh-help`, `report`, `report-api-parity`,
  `identity`, `parse-workers`), `execSync` in `data-handling.test.ts` and `web/src/kyber.test.ts`,
  `execFileSync` in `opencode-plugin/bundle.test.ts`, and `scripts/*.test.ts`.
  `web-listening.test.ts` starts a real dashboard whose JobHost will, after this plan, be
  refused by L4 (inherited `deny`) and L3 does not apply (its entry is the real launcher).
- **F11 — .NET process starts.** 133 occurrences of `Process.Start`/`new Process`/
  `ProcessStartInfo` across 32 files under `tests/KyberWeave.Tests` (largest: `ReleaseTests.cs`
  48, `ProcessRunnerInputTests.cs` 9, `SquadHarnessLauncherTests.cs` 8,
  `UtilitiesStatusLineArtifactTests.cs` 7, `AnalysisPersistenceTests.cs` 7). `ProcessRunnerTests.cs:64`
  already kills with `entireProcessTree: true`. Not audited one by one in this pass (T6 does).
- **F12 — Gate entry points.** `scripts/fast-loop.sh:251-270` calls `npm --prefix dash run
  typecheck|lint|test:related` and tray UI scripts directly. `.kyber-weave/kyber-weave.yml`
  declares the dash gates as argv (`:190`, `:206` `[npm, run, --prefix, dash, test, --, --coverage]`,
  `:215`, `:221`, plus task gates `:311-319`).
- **F13 — No existing marker.** No `KYBERDASH_*` env var for child role, depth or spawn mode
  exists in `dash/` (grep).
- **F14 — Package entries.** `dash/package.json`: `bin.kyberdash` = `dist/cli.js` (a copy of
  `src/launcher.ts`), `dev` = `tsx src/launcher.ts`, `test` = `vitest run src tests scripts
  web/src --exclude "src/refresh/lock*"`, `test:locks` runs the lock files with
  `--pool=forks --no-file-parallelism`.

**Discovery:** `codegraph 3, docs 1, direct reads 9` (plus 12 targeted greps). Direct reads, with
reasons: root `AGENTS.md` (Config Reg); three architect references (plan authoring, autonomy,
test-first contract); `docs/plans/README.md` (index structure); the v2 plan ×2 (decision format
and its gates, for D1); `dash/vitest.config.ts` (a config file no explore call returned);
`.github/workflows/ci.yml` lines 770-859 (workflow YAML is not code-indexed). Greps: spawn imports
across `dash/`, `worker_threads`/`Worker`/`Bun.spawn`, the spawn calls in six source files and six
real-process tests, the e2e specs' spawn and kill lines, `package.json` scripts and entries,
`fast-loop.sh` and `kyber-weave.yml` gate argv, CI job names, .NET `Process.Start` counts and the
`WaitForExit`/`Kill` lines of four .NET files, ADR index numbering, catalog component names, and
the `KYBERDASH_*` marker search. No discovery call failed; no sub-agent was used; nothing was
executed.

## 3. Target design

### 3.1 Layers and where each stops the observed chain

| Layer | Mechanism | Stops the 2026-10 chain at |
|---|---|---|
| L1 boot scripts | Both boots go through `dash/e2e/boot-dashboard.ts` `bootIsolatedDashboard`, which always passes inert seams: a refusing `jobSpawner` and `receiverSpawner` (return `exited: {code: null, stderr: 'e2e boot: spawning disabled'}`), a `receiverProber` that answers `refused`, and a temp `stateDir` for both scripts. Specs spawn the boot detached and kill the process group. | The first spawn: none happens. |
| L2 recursion | Role/depth marker (D3). | The first child: it refuses to host. |
| L3 entry point | `resolveJobSpawnTarget` refuses a non-CLI `argv[1]` (D5). | The first spawn: the boot script is not the CLI entry. |
| L4 fail-closed | `KYBERDASH_SPAWN_GUARD=deny` in vitest and Playwright; `allowRealSpawns` opt-in (D4). | The first spawn: refused under any test process. |
| L5 ledger | Global setup/teardown detectors (D7). | After the run: survivors killed, run failed. |
| L6 watchdog | `scripts/proc-guard.sh run` absolute caps (D6). | At 64 descendants or 128 user node processes, whichever first. |
| L7 audit | Static allow-list scans (D11). | At review: a new unguarded site fails a test. |
| L8 CI | `check-leftovers` step (D9). | In CI: a survivor fails the job. |

### 3.2 `spawn-guard.ts` contract

```text
type SpawnKind = 'job' | 'receiver' | 'engine' | 'install'
guardedSpawn(kind, program, args, options, spawnFn = child_process.spawn): ChildProcess
  throws SpawnRefusedError { kind, program, args, reason } when:
    - KYBERDASH_SPAWN_GUARD === 'deny' and no allowRealSpawns opt-in is active (hit recorded)
    - own role is 'job' or 'receiver'
    - own role is 'engine' and kind === 'engine'
    - own depth + 1 > 2
  otherwise: spawnFn(program, args, { ...options, env: { ...(options.env ?? process.env),
    KYBERDASH_SPAWN_ROLE: kind === 'install' ? <unchanged> : kind,
    KYBERDASH_SPAWN_DEPTH: String(depth + 1) } }) and, when KYBERDASH_PROCESS_LEDGER_DIR is set,
    writes <dir>/<pid>.json { pid, startedAt, kind, argv }.
spawnGuardHits(): readonly string[]; clearSpawnGuardHits(): void   (for the setup file)
assertMayHost(): void   throws SpawnedChildMayNotHostError for role job|receiver
```

`install` children (`npm` in `node-deps.ts`) carry the depth but no KyberDash role: they are not
KyberDash processes.

## 4. Test contract (test-first)

Every runner command below runs through the watchdog once T0 is GREEN. `<vt>` abbreviates
`cd dash && ../scripts/proc-guard.sh run -- npx vitest run`; `<dt>` abbreviates
`scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter`.

| Task | Test project or file | Runner command | Observable behavior | RED evidence required | GREEN acceptance |
|---|---|---|---|---|---|
| T0 | `tests/KyberWeave.Tests/ProcGuardScriptTests.cs` (new) | First RED run only: `dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj --filter "FullyQualifiedName~ProcGuardScriptTests"`; afterwards `<dt> "FullyQualifiedName~ProcGuardScriptTests"` | (1) `run --max-tree 1 -- sleep 30` with `KW_PROC_GUARD_PS` pointing at a fixture script that reports 2 descendants: `sleep` is killed within 5 s and the exit code is 86, stderr names the cap; (2) `--max-node 3` with a fixture reporting 3 user node processes: exit 86; (3) a command that exits 0 and leaves a fixture-reported orphan carrying the repo root: exit 87, orphan listed; (4) a clean command passes its own exit code through (0 and 3); (5) `check-leftovers` with fixture survivors exits 87, with none exits 0; (6) `scripts/fast-loop.sh` invokes every `npm --prefix dash` and `dotnet test` line through `scripts/proc-guard.sh run --` (static read of the script, as `FastLoopScriptTests` does). Every test kills its `sleep` in `finally`; each starts at most one real child, which self-exits within 30 s. | The test file exists and fails because `scripts/proc-guard.sh` does not exist (process start fails) and `fast-loop.sh` has no wrapper. | Same tests pass unchanged; `ps` after the run shows no `sleep 30` left (asserted inside test 1). |
| T1 | `dash/tests/setup/spawn-guard.test.ts`, `dash/src/process/spawn-guard.test.ts` (new) | `<vt> src/process/spawn-guard.test.ts tests/setup/spawn-guard.test.ts` | With a recording `spawnFn` (never a real process): (a) under `deny`, `guardedSpawn` throws `SpawnRefusedError`, records one hit, and `spawnFn` is not called; with `allowRealSpawns('reason')` it calls `spawnFn` once; the opt-in is gone after `afterEach`; (b) the env passed to `spawnFn` carries `KYBERDASH_SPAWN_ROLE=job` and `KYBERDASH_SPAWN_DEPTH=1` from depth 0, and `2` from depth 1; (c) own role `job` or `receiver` → every kind refused; own role `engine` → kind `engine` refused, kind `job` allowed; depth 2 → refused; (d) `nodeSpawner` (built by `createNodeSpawner(spawnFn)`), `DetachedLauncher` (constructor `spawnFn`) and `node-deps` route through the guard: under `deny` each throws and none calls `spawnFn`; (e) the setup file fails a test that caused a hit (verified through a nested `vitest` API run of a fixture test file, or by asserting the exported `afterEach` handler throws on a non-empty hit list); (f) the real default `nodeSpawner.spawn(process.execPath, ['-e', ''])` under the setup's `deny` throws and starts nothing. | Tests exist and fail: module `src/process/spawn-guard.ts` missing (import error) and, for (f), the bounded `node -e ''` actually spawns and exits at once (the only real child RED can start). | Same tests pass unchanged; `spawnGuardHits()` empty after the suite file. |
| T2 | `dash/src/cli/web-recursion-guard.test.ts`, `dash/src/engine/ensure-recursion.test.ts` (new) | `<vt> src/cli/web-recursion-guard.test.ts src/engine/ensure-recursion.test.ts` | (a) with `process.env.KYBERDASH_SPAWN_ROLE='job'` (restored after), `runWebDashboard({ stateDir: <tmp>, store: <temp CanonStore>, jobSpawner: fake, receiverSpawner: fake, receiverProber: refused, maintenance: noop, port: 0, open: false, … })` rejects with `SpawnedChildMayNotHostError`, the fake spawners record zero calls, and the port is closed; same for role `receiver`; (b) with no role it starts and the fake job spawner records the first-tick `dash refresh --trigger scheduled` call with `KYBERDASH_SPAWN_ROLE=job` in the env it would pass (the fake is a `spawnFn`-level recorder wired through `createNodeSpawner`); (c) `ensureEngine` under role `engine` returns `{outcome: 'failed', reason: /engine child may not start an engine/}` and its injected launcher `spawnFn` is never called; under role `job` the same. | Tests fail: (a) resolves and starts hosting with the fakes (no real process: fakes only); (b) the env lacks the marker; (c) `startEngine`/launcher is called. | Same tests pass unchanged. |
| T3 | `dash/src/cli/spawn-target.test.ts` (new); JobHost/ReceiverHost refusal cases in `dash/src/jobs/host.test.ts` and `dash/src/jobs/receiver-host.test.ts` | `<vt> src/cli/spawn-target.test.ts src/jobs/host.test.ts src/jobs/receiver-host.test.ts` | Pure: `resolveJobSpawnTarget` with injected runtimes returns the existing target for SEA, for `argv[1]` = the package's `src/launcher.ts` (also through a symlink in a temp dir), for `dist/cli.js`, and the `kyberdash` fallback for no `argv[1]`; returns `{ refused }` naming the path for `e2e/harness-collection-boot.ts`, a vitest worker path and an arbitrary `/tmp/x.ts`. A JobHost built with a refused target and a recording spawner records a failed refresh whose `lastFailure` contains the refusal reason and never calls the spawner; a ReceiverHost the same with `receiverHosted=on`. | Tests fail: the boot and worker paths come back as a valid target; hosts call the spawner. | Same tests pass unchanged; existing `web-spawn-target.test.ts` unchanged and green in the full run. |
| T4 | `dash/tests/e2e-boot-isolation.test.ts` (new) | `<vt> tests/e2e-boot-isolation.test.ts` | (a) In-process: `bootIsolatedDashboard` for each world (harness-collection, refresh-filters), port 0, then one awaited JobHost tick: `spawnGuardHits()` is empty, the inert job spawner recorded the scheduled refresh and started nothing, the server answers `/api/kyber/version`, and close leaves no hosted pair; `stateDir` is under the world's temp root for both boots. (b) Static: both `dash/e2e/*-boot.ts` files call `bootIsolatedDashboard` and do not call `runWebDashboard` directly; both specs spawn the boot with `detached: true` and kill with `process.kill(-pid, …)`. | Tests fail: `boot-dashboard.ts` missing; with only the static half written first, the boot scripts call `runWebDashboard` directly. Safe because T1–T3 are GREEN: the guard refuses any real spawn (`deny`), so the pre-fix boot cannot start a child. | Same tests pass unchanged. |
| T5 | `dash/tests/setup/process-ledger.test.ts` (new) | `<vt> tests/setup/process-ledger.test.ts` | With a fake `ps` lister and fake `kill`: ledger entries alive with a matching command are reported and killed (TERM then KILL after the grace period on a fake clock); a reused PID with a different command is ignored; descendants of the runner PID are found; PID-1 orphans whose command holds the `dash/` root and started after run start are found, earlier ones ignored; teardown throws listing survivors. One bounded real case: spawn `node -e "setTimeout(()=>{}, 30000)"` (self-exits at 30 s) through `guardedSpawn` with `allowRealSpawns('ledger real-process case; self-exits 30 s')`, run teardown, assert it is reported and gone within 5 s. Vitest `globalSetup` and both Playwright configs reference the ledger hooks (static). | Tests fail: module missing. | Same tests pass unchanged; no `setTimeout(()=>{}, 30000)` node process remains. |
| T6 | `dash/tests/spawn-sites.test.ts`, `tests/KyberWeave.Tests/ProcessSpawnSitesTests.cs` (new) | `<vt> tests/spawn-sites.test.ts`; `<dt> "FullyQualifiedName~ProcessSpawnSitesTests"` | Every spawn site (D11 patterns) is on the allow-list with a non-empty bound; unlisted sites fail with file:line; stale entries fail; every non-test `dash/src` self-spawn is a `guardedSpawn` call (only `spawn-guard.ts` imports `spawn` from `child_process` for self-spawns). | Tests written with an empty allow-list fail, listing every site (the audit's input). | Tests pass with the populated allow-list; each unbounded site found was fixed (timeout, tree kill, or `afterEach` cleanup) and its entry states the bound. |
| T7 | `tests/KyberWeave.Tests/CiLeftoverStepTests.cs` (new); the review-gate config pins (`ReviewTaskGatesConfigTests` and any test pinning `kyber-weave.yml` gate argv) | `<dt> "FullyQualifiedName~CiLeftoverStepTests\|FullyQualifiedName~ReviewTaskGatesConfigTests"` | `ci.yml` `ts-gates`, `dash-e2e`, `build-test` each record a job-start epoch first and end with an `if: always()` step running `scripts/proc-guard.sh check-leftovers`; `build-test` runs `dotnet build-server shutdown` before it; `dash-e2e` invokes Playwright with `--config dash/playwright.isolated.config.ts`. The dash `ts-test` gate and the `dotnet test` gate argv start with `scripts/proc-guard.sh, run, --`. | Tests fail: steps and argv absent. | Same tests pass unchanged. |
| T8 | No test (documentation). | `dotnet run --project src/KyberWeave.Cli -- docs validate .` and `… docs drift .` | H1–H7 written; no doc tells anyone to run Playwright without the isolated config or local gates without the watchdog. | Not applicable: documentation task; replaced by the two documentation checks. | Both checks report zero findings. |

Changing any row above, including weakening an assertion to reach GREEN, is a scope change: the
plan returns to Draft for reapproval.

## 5. Tasks

### T0 — Watchdog script, fast-loop wiring (L6)
- **Skills:** `test-dev` (RED), `github-devops` (GREEN). **Depends on:** none.
- **Files:** new `scripts/proc-guard.sh`; `scripts/fast-loop.sh` (wrap lines 251-270 and any
  `dotnet test`); new `tests/KyberWeave.Tests/ProcGuardScriptTests.cs` and fixture scripts under
  `tests/KyberWeave.Tests/Fixtures/proc-guard/`.
- **Acceptance:** T0 row GREEN; `shellcheck`-clean if the repository's gates run it; D6 exit codes
  and modes exactly.

### T1 — Spawn chokepoint and fail-closed test mode (L4 core, L2 marker)
- **Skills:** `test-dev` (RED T1a), `typescript-dev` (GREEN T1b). **Depends on:** T0.
- **Files:** new `dash/src/process/spawn-guard.ts`; `dash/src/jobs/host.ts` (`createNodeSpawner`,
  `nodeSpawner` = `createNodeSpawner()`); `dash/src/engine/ensure.ts` (`DetachedLauncher` takes
  `spawnFn`, routes through the guard); `dash/src/install/node-deps.ts`; new
  `dash/tests/setup/spawn-guard.ts` (`allowRealSpawns`) and `spawn-guard-setup.ts`;
  `dash/vitest.config.ts` (`setupFiles`); tests of the T1 row.
- **Acceptance:** T1 row GREEN; `npm --prefix dash run check:reachable` reaches the new module.

### T2 — Recursion refusals (L2)
- **Skills:** `test-dev` (RED T2a), `typescript-dev` (GREEN T2b). **Depends on:** T1b.
- **Files:** `dash/src/cli/web.ts` (`acquireHostedServices` calls `assertMayHost()` before
  building a pair; `runWebDashboard` maps the error to one stderr line and exit code 1 in the CLI
  path); `dash/src/engine/ensure.ts` (`ensureEngine` refusal under role `engine`/`job`/`receiver`);
  tests of the T2 row.
- **Acceptance:** T2 row GREEN; the D3 static reachability statement (which commands run in child
  roles, and that none needs a guarded spawn) is written into this plan's Execution state.

### T3 — Entry-point guard (L3)
- **Skills:** `test-dev` (RED T3a), `typescript-dev` (GREEN T3b). **Depends on:** T2b (both edit
  `acquireHostedServices`).
- **Files:** `dash/src/cli/spawn-target.ts` (`SpawnTarget | { refused }`), `dash/src/cli/web.ts`
  (pass the refusal to both hosts), `dash/src/jobs/host.ts` and `dash/src/jobs/receiver-host.ts`
  (record a refused target as a failed job, never spawn), the `attachOtelToEngine` caller of
  `resolveJobSpawnTarget`; tests of the T3 row.
- **Acceptance:** T3 row GREEN.

### T4 — Boot scripts and spec teardown (L1)
- **Skills:** `test-dev` (RED T4a and the spec edits), `typescript-dev` (GREEN T4b).
  **Depends on:** T3b (RED must run with L2–L4 already in place; see D12).
- **Files:** new `dash/e2e/boot-dashboard.ts`; `dash/e2e/harness-collection-boot.ts`,
  `dash/e2e/refresh-filters-boot.ts` (call `bootIsolatedDashboard`; `refresh-filters` also gets a
  temp `stateDir` and the live `KYBERDASH_DASH_DIR` name instead of `CODEBURN_DASH_DIR`);
  `dash/e2e/harness-collection.spec.ts`, `dash/e2e/refresh-filters.spec.ts` (`detached: true`,
  process-group kill); new `dash/tests/e2e-boot-isolation.test.ts`.
- **Acceptance:** T4 row GREEN. No Playwright run in this task (D12).

### T5 — Leftover detection (L5) and Playwright configs
- **Skills:** `test-dev` (RED T5a), `typescript-dev` (GREEN T5b). **Depends on:** T1b (ledger hook
  in `guardedSpawn`), T4b (spec files).
- **Files:** new `dash/tests/setup/process-ledger.ts`, `dash/tests/setup/process-ledger-global.ts`;
  `dash/vitest.config.ts` (`globalSetup`); new `dash/playwright.isolated.config.ts`;
  `dash/playwright.config.ts` (ledger hooks, `webServer.env` per D8); `dash/src/process/spawn-guard.ts`
  (ledger write, if not already in T1b); tests of the T5 row.
- **Acceptance:** T5 row GREEN.

### T6 — Spawn-site audit and allow-lists (L7)
- **Skills:** `test-dev` (RED T6a and GREEN of the test allow-lists), `typescript-dev` and
  `csharp-dev` (fixes to unbounded production or test sites found). **Depends on:** T3b, T5b
  (final shape of the dash sites). The .NET half may start after T0.
- **Files:** new `dash/tests/spawn-sites.test.ts` (+ allow-list beside it),
  `tests/KyberWeave.Tests/ProcessSpawnSitesTests.cs` (+ allow-list); any site fixed. Sites to audit
  at minimum: F9, F10, F11, `scripts/opencode-live-gate/run.mjs`, `ReleaseTests.cs`,
  `SquadHarnessLauncherTests.cs`, `ProcessRunnerInputTests.cs`, `SqliteTestFixture.cs`,
  `Fakes/TestGitRepositoryTemplate.cs`. The tray's `SystemServerSpawner` is recorded as bounded
  (validated binary, backoff, `kill_tree`) in the dash allow-list's notes, not scanned (Rust).
- **Acceptance:** T6 row GREEN; the audit table (site, bound, fixed or as-is) is copied into this
  plan's Execution state.

### T7 — CI leftover step and review-gate wiring (L8, L6 wiring)
- **Skills:** `test-dev` (RED T7a), `github-devops` (GREEN T7b). **Depends on:** T0, T5b
  (isolated config exists).
- **Files:** `.github/workflows/ci.yml` (`ts-gates`, `dash-e2e`, `build-test`);
  `.kyber-weave/kyber-weave.yml` (dash `ts-test` and `dotnet test` gate argv); the matching config
  pin tests; new `tests/KyberWeave.Tests/CiLeftoverStepTests.cs`.
- **Acceptance:** T7 row GREEN.

### T8 — Documentation
- **Skills:** `docs-dev` (with `app-docs-standard`, `architecture-decision-record`).
  **Depends on:** T0–T7.
- **Files:** H1–H7 targets; root `AGENTS.md` and `dash/AGENTS.md` (hand-authored sections only,
  never the Config Reg block); ADR index row 0047.
- **Acceptance:** T8 row.

## 6. Dependency graph and execution safety order

```
T0 ──► T1a ──► T1b ──► T2a ──► T2b ──► T3a ──► T3b ──► T4a ──► T4b ──┐
                 │                                          │        │
                 └──────────────► T5a ─────────────────────►T5b ─────┼──► T6 (dash half) ──┐
T0 ──► T6 (.NET half) ───────────────────────────────────────────────┘                     │
T0, T5b ──► T7a ──► T7b ───────────────────────────────────────────────────────────────────┼──► T8 ──► G1 ──► G2 ──► G3 ──► review ──► closeout
```

The chain T1–T4 is deliberately serial: each RED must run with the earlier layers GREEN, so no RED
can start a storm. T5a can be written in parallel with T2–T4 (fakes only). The .NET half of T6
runs in parallel after T0.

**Execution safety order (D12; binding on every executor):**

1. T0 RED runs `dotnet test` filtered to `ProcGuardScriptTests` directly (one `sleep` child at
   most). From T0 GREEN on, every test command runs through `scripts/proc-guard.sh run --`.
2. Until T2b is GREEN, only the files named in the T0–T2 rows run, one row at a time.
3. The full `npm --prefix dash run test` and `test:locks` run only after T4b is GREEN, through the
   watchdog.
4. No local Playwright run until the PR's CI `dash-e2e` job and its leftover step are green.
5. Nobody starts the dashboard (`kyberdash web`, `npm run dev -- web`) for this plan.
6. After every run, the executor runs `scripts/proc-guard.sh check-leftovers --root <repo>` and
   reports any survivor to the conductor before anything else.

## 7. Verification gates

### G1 — Local, macOS, all through the watchdog

```bash
scripts/proc-guard.sh run -- npm --prefix dash run typecheck
scripts/proc-guard.sh run -- npm --prefix dash run lint
scripts/proc-guard.sh run -- npm --prefix dash run test
scripts/proc-guard.sh run -- npm --prefix dash run test:locks
scripts/proc-guard.sh run -- npm --prefix dash run check:reachable
scripts/proc-guard.sh run -- dotnet test tests/KyberWeave.Tests/KyberWeave.Tests.csproj
scripts/proc-guard.sh run -- dotnet run --project src/KyberWeave.Cli -- review gates . --all --out artifacts/gates.json
dotnet run --project src/KyberWeave.Cli -- docs validate . --merge-ready
dotnet run --project src/KyberWeave.Cli -- docs drift .
scripts/proc-guard.sh check-leftovers --root "$(git rev-parse --show-toplevel)"
```

### G2 — CI

The draft PR's Ubuntu jobs green on the final head, including `dash-e2e` with
`--config dash/playwright.isolated.config.ts` and the leftover steps in `ts-gates`, `dash-e2e`,
`build-test`.

### G3 — Local Playwright, once, after G2

```bash
scripts/proc-guard.sh run --timeout 600 -- npx --prefix dash playwright test --config dash/playwright.isolated.config.ts
scripts/proc-guard.sh check-leftovers --root "$(git rev-parse --show-toplevel)"
```

Pass: both specs green, the ledger teardown passes, `check-leftovers` exits 0, and the watchdog
did not trip. Any trip or survivor fails the gate and returns to the owning task.

### Review and closeout

- Whole-change `code-review` after G1–G3. Expected `NEEDS_HUMAN` under KW-REVIEW-008
  (`.kyber-weave/kyber-weave.yml` is a reserved path).
- `docs-dev` closeout: Closeout mapping for D0–D14, AP1 and H1–H7, archive this plan, move its index
  row, `docs validate --merge-ready` and `docs drift` at zero.
- After merge: the conductor unblocks the v2 plan (D1) and its gates run through the watchdog.

## 8. Risks and out of scope

**Risks.**
- **R1 — A legitimate child path needs a spawn** (D3). Caught by T2's static reachability
  statement and the full suite; reported, never exempted.
- **R2 — False watchdog trips** on a loaded machine (D6). Env overrides; the report lists the top
  commands so the cause is visible.
- **R3 — Playwright config resolution from the repository root** (D8, UNVERIFIED). The explicit
  `--config` removes the dependency on the rule.
- **R4 — `webServer` specs need a real refresh** (D8). Then that config alone gets `allow`,
  recorded in the allow-list with its bound.
- **R5 — Windows.** The watchdog and descendant/orphan detectors are POSIX only; the ledger and L1–L4
  work on Windows (`capture-windows` job).
- **R6 — Reserved-path review.** `kyber-weave.yml` change makes the review `NEEDS_HUMAN` (expected).
- **R7 — Leftovers from earlier storms on the owner's Macs** are not this plan's to kill
  automatically; the runbook (H3) gives the `check-leftovers` command, and the executor reports any
  it sees (§6 step 6) rather than killing processes it did not start.

**Out of scope.**
- `dash/playwright.config.ts`'s `webServer` using the owner's real `~/.kyberdash` state and the
  live port 4747 (`reuseExistingServer`): an isolation problem, not a storm; recorded as a gap for
  a separate todo.
- OS-level process limits (D14).
- The tray's Rust supervisor beyond recording it as bounded (T6).
- Any OpenCode V2 capture work (the v2 plan).

## 9. References

- `docs/plans/2026-10-30-opencode-v2-capture.md` (paused; its G1–G3 wait on this plan, D1).
- `dash/tests/setup/network-guard-setup.ts` (pattern for L4).
- ADR 0039 (single-instance engine), `docs/dash/runbook.md`, `docs/dash/architecture.md`,
  `docs/standards/test/README.md`, `docs/ci-pipelines/workflows-runbook.md`,
  `docs/code-review/architecture.md`.

## 10. Closeout mapping (docs-dev, 2026-10-30)

Status of this section: mapping complete. Archival is **blocked** (see the last subsection). Every
durable item maps to a destination below. "Waiver" follows the closeout rules: an item is waived only
when it is transient with no surviving invariant.

### 10.1 Decisions (D0–D14), approval (AP1)

| ID | Durable content | Canonical destination | Documentation impact | Status |
|---|---|---|---|---|
| D0 | Mode `test-first` | Waiver: transient plan metadata; no surviving invariant. | none | archived-by-waiver |
| D1 | Separate branch cut from `origin/main`; v2 plan blocked on this plan | Waiver: transient sequencing. Handoff below (§10.3). | none | handoff pending |
| D2 | One guarded chokepoint, `guardedSpawn` | ADR 0047 decision 1; `docs/dash/architecture.md` "Spawn chokepoint, child roles and refusals" | extends-component | mapped |
| D3 | Role and depth marker, four rules | ADR 0047 decision 2; architecture "Rules applied in order" table | extends-component | mapped, corrected (below) |
| D4 | Fail-closed deny mode, `allowRealSpawns` opt-in | ADR 0047 decision 4; `docs/standards/test/README.md` "Tests that start processes" rules 2–3; `dash/AGENTS.md` "Tests that start processes" | extends-component | mapped |
| D5 | Entry-point rule for self-spawns | ADR 0047 decision 3; architecture "Entry-point refusal"; `kyberdash otel` ordering note | extends-component | mapped, corrected (below) |
| D6 | Watchdog: `scripts/proc-guard.sh`, caps 128/64, exit 86/87/124 | `docs/dash/runbook.md` "Running tests safely"; root `AGENTS.md` inner loop; `docs/code-review/architecture.md` watchdog section | extends-component | mapped |
| D7 | Three-detector ledger; Windows handling | ADR 0047 decision 6; runbook "Process ledger"; architecture "Known limits" | extends-component | mapped, corrected (below) |
| D8 | Isolated Playwright config; `--config` invocation; process-group kill | `dash/AGENTS.md`; runbook "Playwright runs only with the isolated config"; `docs/rules/kyberdash-harness-collection.md` "Running the conformance spec"; `docs/ci-pipelines/workflows-runbook.md` | extends-component | mapped, corrected (below) |
| D9 | CI leftover step in `ts-gates`, `dash-e2e`, `build-test` | `docs/ci-pipelines/workflows-runbook.md` "Leftover-process steps" | extends-component | mapped, corrected (below) |
| D10 | Watchdog wired into fast loop and gate argv | root `AGENTS.md` "Before opening a PR"; `docs/code-review/architecture.md` watchdog section; `dash/AGENTS.md` | extends-component | mapped |
| D11 | Static spawn-site audits with allow-lists | `docs/standards/test/README.md` "The allow-list (dash)" and "(.NET)"; ADR 0047 decision 5; `dash/AGENTS.md` "Adding a spawn site" | extends-component | mapped, corrected (below) |
| D12 | Execution safety order | Waiver: transient executor sequencing for this plan. The standing rules it protects (no dashboard start in tests; no Playwright without the isolated config) are in the runbook and `dash/AGENTS.md`. | none | archived-by-waiver |
| D13 | ADR 0047 | ADR 0047; `docs/adr/README.md` index row | extends-component | mapped |
| D14 | No OS-level limits (`ulimit`, `maxproc`) | ADR 0047 "Alternatives considered" | extends-component | mapped |
| AP1 | Approval record | Waiver: transient approval record; the decisions it approved are mapped above. | none | archived-by-waiver |

### 10.2 Design harvest (H1–H7)

| ID | Target and destination | Documentation impact | Last reviewed | Status |
|---|---|---|---|---|
| H1 | ADR 0047 (new) and `docs/adr/README.md` row | extends-component | 2026-10-30 | mapped |
| H2 | `docs/dash/architecture.md` "Spawn chokepoint, child roles and refusals (ADR 0047)" | extends-component | 2026-10-30 | mapped |
| H3 | `docs/dash/runbook.md` "Running tests safely"; isolated-config invocation in "Running Unit & Contract Tests" and the Playwright verification block | extends-component | 2026-10-30 | mapped |
| H4 | `docs/standards/test/README.md` "Tests that start processes" (dash and .NET allow-lists, production .NET bounds) | extends-component | 2026-10-30 | mapped |
| H5 | `docs/ci-pipelines/workflows-runbook.md` (`dash-e2e` invocation, leftover steps) | extends-component | 2026-10-30 | mapped |
| H6 | Root `AGENTS.md` (inner loop, before PR; Config Reg block untouched), `dash/AGENTS.md`, `docs/code-review/architecture.md` | extends-component | 2026-10-30 | mapped |
| H7 | `docs/rules/kyberdash-harness-collection.md` "Running the conformance spec" | extends-component | 2026-10-30 | mapped |

### 10.3 Reworks R1–R3 and the OpenCode V2 dependency

| ID | Rework | Destination | Status |
|---|---|---|---|
| R1 | A legitimate child path refused by the guard (reachability check) | Refusal rules in ADR 0047 decision 2 and the architecture "Rules applied in order" table. The static reachability result is the Execution state record in this plan. | mapped |
| R2 | False watchdog trips on a loaded machine | Runbook exit-code table (86: raise the matching override and say why). | mapped |
| R3 | Playwright config resolution from the repository root | Every documented invocation passes `--config dash/playwright.isolated.config.ts` explicitly (runbook, rule, CI runbook, `dash/AGENTS.md`). | mapped |
| D1 dependency | `fix/opencode-v2-capture` must rebase onto `main` after this merges, then run its gates through `scripts/proc-guard.sh run --`. | Handoff to the conductor. Recorded here; the v2 plan's own Execution state is edited by the conductor. | handoff pending |

### 10.4 Corrections where the code differs from the plan text

The code is the truth; the canonical docs state what the code does.

- **Ledger env var.** The plan names `KYBERDASH_PROCESS_LEDGER_DIR`. The code uses `KYBERDASH_SPAWN_LEDGER_FILE`, one appended JSONL file per run.
- **`ensureEngine` refusal.** The plan limits the refusal to the engine role for "engine from engine". The code refuses for every spawned child role (engine, job, receiver).
- **Receiver role.** The plan has `ReceiverHost` spawn with role `receiver`. The code stamps `job`, because `nodeSpawner` hard-codes it. The `receiver` kind is defined and refused the same way; it is tracked in `docs/todo/kyberdash-spawn-guard-residuals.md`.
- **Fourth spawn kind.** The plan names the install spawn `install`. The code's fourth kind is `tool`, used by `install/node-deps.ts`.
- **.NET audit scope.** The plan scans `tests/` and `src/`. The code does too, with root-prefixed keys (`tests/KyberWeave.Tests/...`, `src/...`) and normalized text with a count. The dash audit keys by file and API pattern.
- **`RunInteractive`.** `ProcessRunner.RunInteractive` (`squad run`) is unbounded by design, because a user attends it. Recorded in ADR 0047 decision 5 and the test standard.
- **Windows ledger.** The plan says the ledger skips the descendant and orphan detectors on Windows. The code runs the ledger on Windows in ledger-entry-only mode: pid liveness of recorded children, then TERM and KILL. It cannot tell a reused pid from its child. The watchdog is POSIX-only.
- **Playwright configs.** The plan lists a `refresh-filters` config. That file was deleted. A test pins the set to `playwright.config.ts` and `playwright.isolated.config.ts`, and checks that each sets deny and wires the ledger.
- **CI leftover match.** The plan filters leftovers by command name (`node`, `tsx`, `npx`, `chromium`, `testhost`). The code lists any PID-1 orphan whose command contains the workspace path and started after the job began.

### 10.5 Evidence and archival status

- **G1 (local, through the watchdog):** green on final2. The executor's numbers are not re-run in this closeout.
- **Council:** `NEEDS_HUMAN` under KW-REVIEW-008 only (the reserved `.kyber-weave/kyber-weave.yml` path). Two Minor and two Info findings from the second pass are recorded in `docs/todo/kyberdash-spawn-guard-residuals.md` (items 6 to 9).
- **G2 (CI on the PR):** pending. Requires the PR's Ubuntu jobs green, including `dash-e2e` with the isolated config and the three leftover steps.
- **G3 (local Playwright, once, after G2):** pending. Requires G2 first.
- **Archival: blocked.** (a) §7 places closeout after G2 and G3; (b) this plan file is untracked, so `git mv` cannot move it until it is committed; (c) `docs validate . --merge-ready` reports KW-DOC-LIFECYCLE-003 while the plan is open in `docs/plans`. The owner decides when G2 and G3 are accepted, and the archive move follows that decision.
