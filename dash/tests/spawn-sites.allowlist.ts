// Storm guard T6 allow-list. Every child_process spawn, Worker and webServer site in dash/ must
// be listed here with the bound that limits it. T6a (RED) left it empty so the audit listed every
// site; T6b (GREEN) populates it from a source audit, each bound checked against the code.
//
// An entry covers every site in `file` that has the same `pattern`. The pattern is the API name
// at the site (spawn, spawnSync, exec, execSync, execFile, execFileSync, fork), 'new Worker', or
// 'webServer'. `bound` says how the site is limited (timeout, tree kill, single-shot, injected
// fake, bounded external tool). It must not be empty.
//
// A bound that says "NO TIMEOUT" states a real residual. It is not a bound. Those sites are listed
// as follow-ups in the T6b report; they are not given a false bound here.

export interface SpawnSiteAllowance {
  /** Path relative to dash/, with forward slashes, for example 'src/otel/receiver.ts'. */
  readonly file: string
  /** The API name at the site, 'new Worker', or 'webServer'. */
  readonly pattern: string
  /** How the site is limited. Required and non-empty. */
  readonly bound: string
  /** Context for the reviewer: what the site does and why it is safe. */
  readonly note: string
}

export const SPAWN_SITE_ALLOWLIST: readonly SpawnSiteAllowance[] = [
  // ── Production source ──────────────────────────────────────────────────────────────────────────
  {
    file: 'src/process/spawn-guard.ts',
    pattern: 'spawn',
    bound:
      'The chokepoint: defaultSpawn is the one bare child_process.spawn that guardedSpawn wraps. It sets ' +
      'no timeout of its own. The limits are applied by guardedSpawn before it is called: deny mode refuses ' +
      'unless allowRealSpawns(reason) ran; depth 2 or deeper is refused; a job or receiver never spawns; an ' +
      'engine never spawns an engine. Each caller supplies its own timeout or stdio options.',
    note:
      'Default spawn for the engine launcher (ensure.ts) and the job host (host.ts). Throws ' +
      'SpawnRefusedError and records a hit when refused; each started child is recorded in the ledger.',
  },
  {
    file: 'src/cli/web.ts',
    pattern: 'execFile',
    bound:
      'execFile timeout 10 s: openBrowser runs open (darwin), rundll32.exe (win32) or xdg-open (other) with argv, ' +
      'not a shell, on a URL validated to a loopback dashboard route. The timeout sends SIGTERM to the direct launcher ' +
      'and the callback ignores the result. It does not go through guardedSpawn, because open, xdg-open and rundll32 ' +
      'are not self-spawns, so spawn-guard deny mode does not cover it.',
    note: 'Fire-and-forget browser launcher, reached only with --open (not --no-open).',
  },
  {
    file: 'src/otel/receiver.ts',
    pattern: 'execFile',
    bound:
      'Each of the three calls (lsof, ss, netstat) passes timeout: DISCOVERY_TOOL_TIMEOUT_MS (2000 ms), and the ' +
      'child is killed at the timeout. The fourth site is the promisify(execFile) wrapper, which the same ' +
      'timeout covers when called.',
    note: 'Port-owner discovery. Fixed tool names and argv, no shell.',
  },
  {
    file: 'src/providers/antigravity.ts',
    pattern: 'execFile',
    bound:
      'execFileText applies timeout 3000 ms by default, 5000 ms for the powershell.exe calls, and maxBuffer ' +
      '1 MB. Each call is a fixed program with fixed argv (ps, lsof, powershell.exe), and the child is killed ' +
      'at the timeout.',
    note: 'Process and port discovery for the Antigravity provider. Output is capped at 1 MB.',
  },
  {
    file: 'src/ingest/parse-workers.ts',
    pattern: 'new Worker',
    bound:
      'The pool size comes from decideParseWorkers: at most cores-1; at most a memory budget (25% of available ' +
      'memory, capped) divided by the per-worker RSS estimate; and at most what the file and byte counts earn. ' +
      'Zero workers on 2 or fewer cores, under the memory floor, or under the pending-bytes floor. The ' +
      'KYBERDASH_PARSE_WORKERS override is capped at the core count. close() terminates every worker, and a ' +
      'constructor failure terminates the partial pool.',
    note: 'Parallel session parse. Each worker parses one file at a time and is terminated on close.',
  },
  {
    file: 'src/install/node-deps.ts',
    pattern: 'spawn',
    bound:
      'guardedSpawn with kind "tool" (so the depth and deny rules apply), options shell: false and timeout: ' +
      '300000 ms (TOOL_TIMEOUT_MS). The spawn is killed at the timeout and reports a failed exit.',
    note: 'Runs codesign, spctl and ditto on the installed bundle. Argv only; no shell.',
  },
  // ── Release and smoke scripts ──────────────────────────────────────────────────────────────────
  {
    file: 'scripts/release-acceptance/run.mjs',
    pattern: 'spawn',
    bound:
      'Each step gets spawn option timeout: STEP_TIMEOUT_MS (30 min). The spawn kills a step that outlives the ' +
      'timeout (SIGTERM, Node default), and close reports it as a failed step.',
    note: 'Release acceptance runner. Runs release tooling steps one at a time. Not a self-spawn.',
  },
  {
    file: 'scripts/sea-mcp-smoke.mjs',
    pattern: 'spawn',
    bound:
      'Starts the SEA binary under test with mcp args. After TIMEOUT_MS (10 s) closeChild ends stdin, and ' +
      'SIGKILL follows after a 1 s grace if the child is still alive. The finally block closes the child and ' +
      'the fixture server.',
    note: 'Smoke test for the packaged MCP server. Isolated HOME and state directory under a temp root.',
  },
  {
    file: 'scripts/sea-web-smoke.mjs',
    pattern: 'spawn',
    bound:
      'Starts the binary as web --no-open --port 0. An OVERALL_TIMEOUT_MS (20 s) timer sends SIGKILL. The ' +
      'finally block clears the timer, SIGKILLs the child if it is still running, and removes the temp home.',
    note: 'Smoke test for the packaged web dashboard. Port 0, so no fixed port is taken.',
  },
  {
    file: 'scripts/upgrade-path/run.mjs',
    pattern: 'spawn',
    bound:
      'serveSmoke starts the new binary as serve --stdio. A 240 s race on the two answers calls child.kill(), and ' +
      'a 30 s wait after stdin closes also calls child.kill(). Both send SIGTERM. There is no SIGKILL escalation, ' +
      'so a binary that ignores SIGTERM would survive the run. Known residual.',
    note: 'Upgrade-path serve check. The serve child is the only spawn here that is not a spawnSync.',
  },
  {
    file: 'scripts/upgrade-path/run.mjs',
    pattern: 'spawnSync',
    bound:
      'run() sets spawnSync timeout: STEP_TIMEOUT_MS (15 min). The call kills the child at the timeout (SIGTERM). ' +
      'No caller passes a timeout that would override it.',
    note: 'Runs the old and new CLI binaries and compare.mjs for the upgrade comparison.',
  },
  {
    file: 'playwright.config.ts',
    pattern: 'webServer',
    bound:
      'Playwright starts `npm run build:dash && npm run dev -- web` once, waits up to 120 s for the URL, and stops ' +
      'the server it started at run end. KYBERDASH_SPAWN_GUARD=deny refuses the dashboard\'s own child spawns ' +
      'unless a test calls allowRealSpawns.',
    note: 'The dashboard the e2e suite drives. reuseExistingServer is on outside CI.',
  },
  // ── Test sites: each classified by reading the call ────────────────────────────────────────────
  {
    file: 'scripts/sea-web-smoke.test.ts',
    pattern: 'spawnSync',
    bound:
      'Five calls run node scripts/sea-web-smoke.mjs against a stand-in written by the test. Each call sets ' +
      'spawnSync timeout 30 s, and the script bounds itself at 20 s with SIGKILL. The stand-ins are fixed ' +
      'scripts; the longest self-exits after 60 s.',
    note: 'Covers the smoke script\'s own failure paths (not-ready, wrong page, overall timeout, stray child).',
  },
  {
    file: 'scripts/sea-mcp-smoke.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node sea-mcp-smoke.mjs against a stand-in binary written by the test. spawnSync timeout is ' +
      'SMOKE_TIMEOUT_MS + 5 s (15 s), and the script bounds its own child at 10 s.',
    note: 'Covers the MCP smoke script\'s failure paths with a stand-in in place of the SEA binary.',
  },
  {
    file: 'scripts/pack-sea-web.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node scripts/pack-sea-web.mjs over a temp source directory (no index.html). It is a local file packer ' +
      'with no network. spawnSync timeout 30 s with SIGKILL, and the test asserts the call did not time out before it asserts a non-zero exit.',
    note: 'Asserts the packer exits non-zero when index.html is missing.',
  },
  {
    file: 'e2e/harness-collection.spec.ts',
    pattern: 'spawnSync',
    bound:
      'Runs npm run build:web once in beforeAll, only when the built dist is missing. spawnSync timeout 300 s with ' +
      'SIGKILL; a timeout makes the build fail and beforeAll throws. The kill reaches the direct npm child only, not ' +
      'processes npm spawns (npm install, vite), so those are bounded by their own exit.',
    note: 'One-time web build before the harness-collection boot.',
  },
  {
    file: 'e2e/harness-collection.spec.ts',
    pattern: 'spawn',
    bound:
      'Starts npx tsx e2e/harness-collection-boot.ts (the dashboard and its API) as a detached process group. The ' +
      'wait for the HC_READY line rejects after 150 s. afterAll kills the whole group (process.kill(-pid)), and a ' +
      'process exit listener does the same if the test process exits first.',
    note: 'Group kill reaches processes the boot forks, not only the boot itself.',
  },
  {
    file: 'e2e/refresh-filters.spec.ts',
    pattern: 'spawnSync',
    bound:
      'Runs npm run build:web once in beforeAll, only when the built dist is missing. spawnSync timeout 300 s with ' +
      'SIGKILL; a timeout makes the build fail and beforeAll throws. The kill reaches the direct npm child only, not ' +
      'processes npm spawns (npm install, vite), so those are bounded by their own exit.',
    note: 'One-time dashboard build before the refresh-filters boot. Script name and dist path match harness-collection.spec.ts.',
  },
  {
    file: 'e2e/refresh-filters.spec.ts',
    pattern: 'spawn',
    bound:
      'Starts npx tsx e2e/refresh-filters-boot.ts as a detached process group. The wait for the ready line rejects ' +
      'after 150 s. afterAll kills the whole group, and a process exit listener does the same as a fallback.',
    note: 'Group kill reaches processes the boot forks.',
  },
  {
    file: 'src/capture/harnesses/opencode-plugin/bundle.test.ts',
    pattern: 'execFileSync',
    bound:
      'Runs node --input-type=module with a fixed probe piped on stdin. It imports an in-memory data: module built ' +
      'from the bundle source. execFileSync timeout 20 s; env is reduced to PATH.',
    note: 'Loads the OpenCode plugin bundle in a fresh Node. Starts no server and no CLI.',
  },
  {
    file: 'src/cli/cli-commands.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node --import tsx src/launcher.ts menubar with KYBER_WEAVE_RELEASE_ORIGIN pointed at 127.0.0.1:1, so ' +
      'the installer fails at its first fetch. spawnSync timeout 60 s. spawnSync kills only the direct child.',
    note: 'Checks the installer exits non-zero and names the failing step. No network and no /Applications write.',
  },
  {
    file: 'src/cli/cli-provider-validation.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node --import tsx src/launcher.ts with provider arguments under an isolated HOME. spawnSync timeout 30 s.',
    note: 'Provider validation through the real CLI entry point.',
  },
  {
    file: 'src/cli/cli-refresh-help.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node --import tsx src/launcher.ts report --help. It only prints help and reads no database. spawnSync ' +
      'timeout 60 s with SIGKILL; a timeout leaves status null, which fails the status-0 assertion.',
    note: 'Asserts the report help discloses the context-report flags.',
  },
  {
    file: 'src/cli/report-api-parity.test.ts',
    pattern: 'spawnSync',
    bound:
      'Three calls run node --import tsx src/launcher.ts report --format json against a temp database that the test ' +
      'seeds. Each call sets spawnSync timeout 30 s. The afterEach removes the temp roots.',
    note: 'Checks the CLI report matches the API output for the same seeded data.',
  },
  {
    file: 'src/cli/report.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node --import tsx src/launcher.ts with report arguments under an isolated HOME and TZ=UTC. ' +
      'spawnSync timeout 30 s.',
    note: 'Report command through the real CLI entry point.',
  },
  {
    file: 'src/cli/web-listening.test.ts',
    pattern: 'spawn',
    bound:
      'Starts node --import tsx src/launcher.ts web --no-open --port 0 as a long-running server. The afterEach sends ' +
      'SIGINT to each running child and SIGKILL after 2 s (unref\'d timer). The wait for the first stdout line is ' +
      'bounded at 15 s by waitFor.',
    note: 'Checks the dashboard announces its listening URL. Port 0, so no fixed port is taken.',
  },
  {
    file: 'src/cli/web-listening.test.ts',
    pattern: 'spawnSync',
    bound:
      'Runs node --import tsx src/launcher.ts web --view not-a-real-view --no-open, which exits 2 at once. ' +
      'spawnSync timeout 30 s.',
    note: 'Checks an unknown view exits 2 and lists the valid forms.',
  },
  {
    file: 'src/cli/web-open-browser.test.ts',
    pattern: 'execFile',
    bound:
      'The child_process module is replaced by vi.mock with a vi.fn(). No real process starts, so there is no ' +
      'process bound to check.',
    note: 'Unit test of the openBrowser argv and URL validation.',
  },
  {
    file: 'src/cli/web-spawn-target.test.ts',
    pattern: 'spawnSync',
    bound:
      'Three calls run the tsx binary, or the resolved spawn target, on the launcher (dash settings show) or on a ' +
      'probe script. Each call sets spawnSync timeout 120 s. Tests run with isSea false, so the target is node or tsx.',
    note: 'Checks the spawn-target resolver produces a working command line from the real loader flags.',
  },
  {
    file: 'src/data-handling.test.ts',
    pattern: 'execSync',
    bound:
      'Five execSync calls run local git (rev-parse, ls-files --cached, check-ignore) inside the repository, each with ' +
      'execSync timeout 30 s. Local git commands; no network. A timeout throws; the repository-root lookup falls back to cwd on any error.',
    note: 'Checks that no tracked file or ignored candidate carries captured content.',
  },
  {
    file: 'src/engine/instance.test.ts',
    pattern: 'spawn',
    bound:
      'Starts node -e with a one-line net server that prints "up". The readiness wait rejects after 10 s, or at once if ' +
      'the child exits or errors first. A finally SIGKILLs the child and awaits its exit on every path, so a child that ' +
      'fails before printing is not left running.',
    note: 'Reproduces a leftover socket file from a killed engine.',
  },
  {
    file: 'src/identity.test.ts',
    pattern: 'spawnSync',
    bound: 'Runs node --import tsx src/launcher.ts --help. spawnSync timeout 60 s.',
    note: 'Checks the CLI help names KyberDash and not CodeBurn.',
  },
  {
    file: 'src/ingest/parse-workers.test.ts',
    pattern: 'spawnSync',
    bound:
      'Two calls run node --import tsx: the launcher with refresh arguments, and an -e one-liner that calls ' +
      'parseAllSessions. Each sets spawnSync timeout 60 s, under an isolated HOME.',
    note: 'Covers the parallel cold parse through the CLI and through the parse entry point.',
  },
  {
    file: 'src/refresh/lock-corrupt-body.test.ts',
    pattern: 'spawn',
    bound:
      'Starts tests/fixtures/cache-refresh-slow-owner.ts and cache-refresh-corrupt-owner.ts. Each acquires the ' +
      'refresh lock within the lock\'s own wait budget, then exits. The corrupt owner exits after its hold time ' +
      '(4000 ms). The test awaits the exit in finally, and that wait rejects after 30 s and SIGKILLs a fixture still running.',
    note: 'Fixture owners that hold the cache lock. They self-exit; the test never has to kill them.',
  },
  {
    file: 'src/refresh/lock-process.test.ts',
    pattern: 'spawn',
    bound:
      'Starts barrier-driven workers (tests/fixtures/cache-refresh-worker.ts). The fixture gives up on a barrier after ' +
      '20 s and exits non-zero. waitForExit rejects and SIGKILLs a worker still running after 25 s. afterEach SIGKILLs ' +
      'every worker still running, so a failed assertion leaves no survivor.',
    note: 'Two-process lock races. Each test awaits its workers\' exits.',
  },
  {
    file: 'src/refresh/lock.test.ts',
    pattern: 'spawn',
    bound:
      'Starts node -e "" (an empty program) to obtain the pid of a process that has exited. It exits during node ' +
      'startup, and the exit wait rejects after 10 s and SIGKILLs the child if it ever does not exit.',
    note: 'Gives a dead-holder pid for the stale-lock gate.',
  },
  {
    file: 'tests/setup/process-ledger.test.ts',
    pattern: 'spawn',
    bound:
      'Starts a node child with setTimeout 30 s through guardedSpawn, after allowRealSpawns. The test SIGKILLs it in ' +
      'finally. The ledger teardown is the backstop for any survivor.',
    note: 'Bounded real-process case for the spawn ledger. Owned by the process-ledger worker.',
  },
  {
    file: 'tests/setup/process-ledger.ts',
    pattern: 'spawnSync',
    bound:
      'Runs ps -axo with LC_ALL=C and a 64 MB maxBuffer, under spawnSync timeout 10 s with SIGKILL. ps is read-only and ' +
      'local; a timeout makes ps fail, so listing throws. Owned by the process-ledger worker.',
    note: 'Lists the system process table for the end-of-run ledger.',
  },
  {
    file: 'web/src/kyber.test.ts',
    pattern: 'execSync',
    bound:
      'Runs npm run build in web/ (a full Vite build) through execSync with a 120 s timeout and SIGKILL. execSync blocks ' +
      'the event loop, so the bound is the timeout, not the vitest test timeout. The kill reaches the direct shell only, ' +
      'not a grandchild the build leaves behind.',
    note: 'Checks the built CSS for the @theme tokens the components consume.',
  },
]
