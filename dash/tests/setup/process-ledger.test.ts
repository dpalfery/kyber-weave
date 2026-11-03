// Process ledger (storm guard T5a, RED). Failure this prevents: a test run leaves children
// behind. They outlive the runner, get re-parented to PID 1, and nothing reaps them. That is how
// the 536-node storm reached the machine. T5b builds the ledger; until then these tests fail on
// their own reasons (the missing module, the missing export, the missing config wiring).
//
// The contract under test (GREEN is T5b: dash/tests/setup/process-ledger.ts,
// dash/tests/setup/process-ledger-global.ts, dash/playwright.isolated.config.ts, and the
// guardedSpawn ledger write in dash/src/process/spawn-guard.ts):
//
//   teardownProcessLedger(options, deps): Promise<void>
//     options = {
//       entries:     readonly { pid, command, startedAt }[]  // what guardedSpawn recorded
//       runnerPid:   number   // the process running the teardown; its descendants are survivors
//       runStartedAt: number  // epoch ms the run began
//       repoRoot:    string   // the dash/ root; PID-1 orphans must name it in their command line
//       graceMs:     number   // SIGTERM -> SIGKILL grace
//     }
//     deps = {
//       listProcesses(): readonly { pid, ppid, startedAt, command }[]   // the ps lister
//       kill(pid, signal: 'SIGTERM' | 'SIGKILL'): void
//       sleep(ms): Promise<void>                                         // the clock
//     }
//   Survivors are, deduplicated by pid:
//     - a ledger entry whose pid is alive and whose command equals the recorded command
//       (a reused pid with another command is not the ledger's child and is ignored);
//     - a descendant of runnerPid (never the runner itself);
//     - a PID-1 orphan whose command names repoRoot and whose startedAt >= runStartedAt.
//   Each survivor gets SIGTERM. After graceMs, the ones still listed get SIGKILL. Then the
//   teardown throws an Error that names every survivor by pid and command.
//   With no survivors it resolves and sends no signal.
//
//   systemLedgerDeps: the real deps (ps -axo, process.kill, a timer-backed sleep).
//
//   Spawn side: spawn-guard.ts exports spawnLedgerEntries(): readonly LedgerEntry[]. guardedSpawn
//   records every child it starts as { pid, command: [program, ...args].join(' '), startedAt }.
//
// The module is imported dynamically with a path held in a variable. That keeps the typecheck
// gate green while the module is still missing, and each test then fails on the missing module.
import { spawn, type ChildProcess, type SpawnOptions } from 'node:child_process'
import { readFileSync, existsSync } from 'node:fs'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { allowRealSpawns, guardedSpawn } from '../../src/process/spawn-guard.js'

type Signal = 'SIGTERM' | 'SIGKILL'
type ProcessRow = {
  readonly pid: number
  readonly ppid: number
  readonly startedAt: number
  readonly command: string
}
type LedgerEntry = { readonly pid: number; readonly command: string; readonly startedAt: number }
type ProcessLedgerDeps = {
  readonly listProcesses: () => readonly ProcessRow[]
  readonly kill: (pid: number, signal: Signal) => void
  readonly sleep: (ms: number) => Promise<void>
}
type TeardownOptions = {
  readonly entries: readonly LedgerEntry[]
  readonly runnerPid: number
  readonly runStartedAt: number
  readonly repoRoot: string
  readonly graceMs: number
}
type LedgerModule = {
  readonly teardownProcessLedger: (options: TeardownOptions, deps: ProcessLedgerDeps) => Promise<void>
  readonly systemLedgerDeps: ProcessLedgerDeps
}
type SpawnLedgerModule = {
  readonly spawnLedgerEntries: () => readonly LedgerEntry[]
}

const testDir = dirname(fileURLToPath(import.meta.url))
const dashRoot = join(testDir, '..', '..')
const ledgerModulePath = './process-ledger.js'
const spawnGuardModulePath = '../../src/process/spawn-guard.js'

const RUNNER_PID = 100
const RUN_START = 1_000
const REPO_ROOT = '/repo/dash'
const GRACE_MS = 5_000

function row(pid: number, ppid: number, command: string, startedAt = RUN_START + 500): ProcessRow {
  return { pid, ppid, startedAt, command }
}

/**
 * A fake process table with a fake clock. Every pid dies on SIGKILL. The pids in exitsOnTerm
 * die on SIGTERM as well. Each signal is logged with the clock reading, so "KILL after the
 * grace period" is asserted on the fake clock, not on wall time.
 */
function fakeHost(rows: readonly ProcessRow[], exitsOnTerm: readonly number[] = []) {
  let table: ProcessRow[] = rows.map((entry) => ({ ...entry }))
  let clockMs = 0
  const log: string[] = []
  const deps: ProcessLedgerDeps = {
    listProcesses: () => table.map((entry) => ({ ...entry })),
    kill: (pid, signal) => {
      log.push(`${signal} ${pid} @${clockMs}`)
      if (signal === 'SIGKILL' || exitsOnTerm.includes(pid)) {
        table = table.filter((entry) => entry.pid !== pid)
      }
    },
    sleep: async (ms) => {
      clockMs += ms
    },
  }
  return { deps, signals: () => [...log] }
}

async function loadLedger(): Promise<LedgerModule> {
  // Missing until T5b. The failure names the module, which is the reason this suite is RED.
  return (await import(ledgerModulePath)) as LedgerModule
}

/** The message the teardown throws with, or '' when it resolves. Module load failures propagate. */
async function messageOf(attempt: Promise<void>): Promise<string> {
  try {
    await attempt
  } catch (error) {
    return error instanceof Error ? error.message : String(error)
  }
  return ''
}

describe('process ledger teardown (storm guard T5a)', () => {
  it('reports a ledger entry still alive with its recorded command, then kills it with TERM and, after the grace period, KILL', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([row(4101, 1, 'node stuck-child.js', RUN_START + 10)])

    const attempt = ledger.teardownProcessLedger(
      {
        entries: [{ pid: 4101, command: 'node stuck-child.js', startedAt: RUN_START + 10 }],
        runnerPid: RUNNER_PID,
        runStartedAt: RUN_START,
        repoRoot: REPO_ROOT,
        graceMs: GRACE_MS,
      },
      host.deps,
    )

    await expect(attempt, 'a live ledger entry must make the teardown throw').rejects.toThrow(
      /4101.*node stuck-child\.js/,
    )
    expect(host.signals(), 'TERM first, then KILL only after the grace period on the fake clock').toEqual([
      'SIGTERM 4101 @0',
      `SIGKILL 4101 @${GRACE_MS}`,
    ])
  })

  it('ignores a reused pid whose command differs from the ledger entry: not reported, not signalled', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([row(4102, 1, 'node unrelated-tool.js', RUN_START + 10)])

    await expect(
      ledger.teardownProcessLedger(
        {
          entries: [{ pid: 4102, command: 'node ledger-child.js', startedAt: RUN_START + 10 }],
          runnerPid: RUNNER_PID,
          runStartedAt: RUN_START,
          repoRoot: REPO_ROOT,
          graceMs: GRACE_MS,
        },
        host.deps,
      ),
      'a reused pid is not the ledger child and must not fail the run',
    ).resolves.toBeUndefined()
    expect(host.signals(), 'a reused pid must not be signalled').toEqual([])
  })

  it('resolves without signalling when every ledger entry has already exited', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([])

    await expect(
      ledger.teardownProcessLedger(
        {
          entries: [{ pid: 4103, command: 'node done.js', startedAt: RUN_START + 10 }],
          runnerPid: RUNNER_PID,
          runStartedAt: RUN_START,
          repoRoot: REPO_ROOT,
          graceMs: GRACE_MS,
        },
        host.deps,
      ),
      'a clean run must pass the teardown',
    ).resolves.toBeUndefined()
    expect(host.signals()).toEqual([])
  })

  it('finds descendants of the runner pid and never signals the runner itself', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([
      row(RUNNER_PID, 1, 'node vitest run'),
      row(101, RUNNER_PID, 'node worker.js'),
      row(102, 101, 'node grandchild.js'),
      row(200, 1, 'node elsewhere.js'),
    ])

    const message = await messageOf(
      ledger.teardownProcessLedger(
        { entries: [], runnerPid: RUNNER_PID, runStartedAt: RUN_START, repoRoot: REPO_ROOT, graceMs: GRACE_MS },
        host.deps,
      ),
    )

    expect(message, 'a live child of the runner must be reported').toContain('101')
    expect(message, 'a grandchild of the runner must be reported').toContain('102')
    expect(message, 'an unrelated process must not be reported').not.toContain('elsewhere.js')
    expect(
      host.signals().filter((entry) => entry.startsWith('SIGTERM')).sort(),
      'both descendants must be sent SIGTERM',
    ).toEqual(['SIGTERM 101 @0', 'SIGTERM 102 @0'])
    expect(host.signals().join('|'), 'the runner and the unrelated process must never be signalled').not.toMatch(
      /\b(100|200)\b/,
    )
  })

  it('finds PID-1 orphans whose command names the dash root and that started after the run began', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([row(300, 1, `node ${REPO_ROOT}/node_modules/vitest/dist/worker.js`, RUN_START + 500)])

    const message = await messageOf(
      ledger.teardownProcessLedger(
        { entries: [], runnerPid: RUNNER_PID, runStartedAt: RUN_START, repoRoot: REPO_ROOT, graceMs: GRACE_MS },
        host.deps,
      ),
    )

    expect(message, 'a PID-1 orphan under the dash root started in this run must be reported').toContain('300')
    expect(host.signals()).toContain('SIGTERM 300 @0')
  })

  it('ignores PID-1 orphans that started before the run, sit outside the dash root, or are not re-parented', async () => {
    const ledger = await loadLedger()
    const host = fakeHost([
      row(301, 1, `node ${REPO_ROOT}/node_modules/vitest/dist/worker.js`, RUN_START - 100),
      row(302, 1, 'node /other/project/tool.js', RUN_START + 500),
      row(303, 5, `node ${REPO_ROOT}/node_modules/vitest/dist/worker.js`, RUN_START + 500),
    ])

    await expect(
      ledger.teardownProcessLedger(
        { entries: [], runnerPid: RUNNER_PID, runStartedAt: RUN_START, repoRoot: REPO_ROOT, graceMs: GRACE_MS },
        host.deps,
      ),
      'orphans from an earlier run, outside the repo, or not re-parented must not fail this run',
    ).resolves.toBeUndefined()
    expect(host.signals()).toEqual([])
  })

  it('throws one error naming every survivor by pid and command, and has killed them', async () => {
    const ledger = await loadLedger()
    // 500 ignores SIGTERM; 501 exits on SIGTERM; 502 is a runner descendant that ignores SIGTERM.
    const host = fakeHost(
      [
        row(RUNNER_PID, 1, 'node vitest run'),
        row(500, 1, 'node stubborn-a.js', RUN_START + 10),
        row(501, 1, 'node polite-b.js', RUN_START + 10),
        row(502, RUNNER_PID, 'node stubborn-c.js'),
      ],
      [501],
    )

    const message = await messageOf(
      ledger.teardownProcessLedger(
        {
          entries: [
            { pid: 500, command: 'node stubborn-a.js', startedAt: RUN_START + 10 },
            { pid: 501, command: 'node polite-b.js', startedAt: RUN_START + 10 },
          ],
          runnerPid: RUNNER_PID,
          runStartedAt: RUN_START,
          repoRoot: REPO_ROOT,
          graceMs: GRACE_MS,
        },
        host.deps,
      ),
    )

    expect(message, 'the error must name the first survivor by pid and command').toMatch(/500.*node stubborn-a\.js/)
    expect(message, 'the error must name the second survivor by pid and command').toMatch(/501.*node polite-b\.js/)
    expect(message, 'the error must name the descendant survivor by pid and command').toMatch(/502.*node stubborn-c\.js/)
    expect(
      [...host.signals()].sort(),
      'every survivor gets SIGTERM; the ones that ignore it get SIGKILL after the grace period',
    ).toEqual(
      [
        'SIGTERM 500 @0',
        'SIGTERM 501 @0',
        'SIGTERM 502 @0',
        `SIGKILL 500 @${GRACE_MS}`,
        `SIGKILL 502 @${GRACE_MS}`,
      ].sort(),
    )
  })

  it('a real node child spawned through guardedSpawn is recorded in the ledger, reported by the teardown, and gone within 5 s', async () => {
    // Bounded real case: the child self-exits after 30 s, and the test kills it in finally.
    const ledger = await loadLedger()
    const spawnModule = (await import(spawnGuardModulePath)) as SpawnLedgerModule
    const program = process.execPath
    const args = ['-e', 'setTimeout(()=>{}, 30000)']
    const runStartedAt = Date.now()
    let child: ChildProcess | undefined

    allowRealSpawns('ledger real-process case; self-exits 30 s')
    try {
      const started = guardedSpawn(
        (file: string, fileArgs: readonly string[], options: SpawnOptions) => spawn(file, fileArgs, options),
        { kind: 'tool', program, args, options: { stdio: 'ignore' } },
      )
      child = started
      const pid = started.pid
      expect(pid, 'the real spawn must start a process').toEqual(expect.any(Number))

      const entry = spawnModule.spawnLedgerEntries().find((candidate) => candidate.pid === pid)
      expect(entry, `guardedSpawn must record pid ${pid} in the spawn ledger`).toBeDefined()
      expect(entry?.command, 'the ledger must record the command as program plus args').toBe(
        [program, ...args].join(' '),
      )

      const exit = new Promise<string | null>((resolve) => {
        started.once('exit', (_code, signal) => resolve(signal))
      })
      const attempt = ledger.teardownProcessLedger(
        {
          entries: entry ? [entry] : [],
          runnerPid: process.pid,
          runStartedAt,
          repoRoot: dashRoot,
          graceMs: 1_000,
        },
        ledger.systemLedgerDeps,
      )
      await expect(attempt, 'the teardown must report the live child by pid').rejects.toThrow(String(pid))

      let timer: NodeJS.Timeout | undefined
      const timedOut = new Promise<'timed out'>((resolve) => {
        timer = setTimeout(() => resolve('timed out'), 5_000)
      })
      const outcome = await Promise.race([exit, timedOut])
      clearTimeout(timer)
      expect(outcome, `pid ${pid} must be gone within 5 s of the teardown`).toBe('SIGTERM')
    } finally {
      if (child && child.exitCode === null && child.signalCode === null) {
        child.kill('SIGKILL')
      }
    }
  })
})

describe('ledger wiring (static)', () => {
  it('vitest.config.ts registers the ledger as a globalSetup entry', () => {
    const config = readFileSync(join(dashRoot, 'vitest.config.ts'), 'utf8')
    const globalSetup = /globalSetup:\s*(\[[^\]]*\]|[^,\n]+)/.exec(config)?.[1] ?? ''

    expect(globalSetup, 'vitest.config.ts must register process-ledger-global.ts as globalSetup').toContain(
      'process-ledger-global',
    )
  })

  it.each([
    ['playwright.config.ts', 'playwright.config.ts'],
    ['playwright.isolated.config.ts', 'playwright.isolated.config.ts'],
  ])('%s references the ledger global teardown', (_label, file) => {
    const path = join(dashRoot, file)
    expect(existsSync(path), `${file} is missing`).toBe(true)
    const config = readFileSync(path, 'utf8')
    const globalTeardown = /globalTeardown:\s*([^,\n]+)/.exec(config)?.[1] ?? ''

    expect(globalTeardown, `${file} must run the ledger teardown as globalTeardown`).toContain(
      'process-ledger-global',
    )
  })

  describe('playwright.isolated.config.ts', () => {
    const isolatedPath = join(dashRoot, 'playwright.isolated.config.ts')

    function readIsolatedConfig(): string {
      if (!existsSync(isolatedPath)) throw new Error(`${isolatedPath} is missing`)
      return readFileSync(isolatedPath, 'utf8')
    }

    it('runs one worker at a time', () => {
      expect(readIsolatedConfig(), 'the isolated config must set workers: 1').toMatch(/\bworkers:\s*1\b/)
    })

    it('has no webServer block, so it never starts a dashboard', () => {
      expect(
        readIsolatedConfig(),
        'the isolated config must not start a dashboard through webServer; each spec boots its own',
      ).not.toMatch(/\bwebServer\s*:/)
    })
  })
})
