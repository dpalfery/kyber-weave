// Process ledger review fixes (storm guard T5, PR #477 C8-C10). Fakes only; no real process.
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { dirname, join } from 'node:path'
import { fileURLToPath } from 'node:url'
import { describe, expect, it } from 'vitest'

import { endLedgerRun } from './process-ledger-global.js'
import { teardownProcessLedger, type ProcessLedgerDeps, type ProcessRow } from './process-ledger.js'

const dashRoot = join(dirname(fileURLToPath(import.meta.url)), '..', '..')
const OPTIONS = { entries: [], runnerPid: 100, runStartedAt: 1_000, repoRoot: '/repo/dash', graceMs: 1 } as const

function fakeDeps(rows: readonly ProcessRow[], extra: Partial<ProcessLedgerDeps> = {}) {
  const signals: string[] = []
  const deps: ProcessLedgerDeps = {
    listProcesses: () => rows,
    kill: (pid, signal) => void signals.push(`${signal} ${pid}`),
    sleep: async () => undefined,
    ...extra,
  }
  return { deps, signals }
}

describe('C8: win32 without a liveness probe', () => {
  it('throws instead of reporting no survivors', async () => {
    const { deps } = fakeDeps([], { platform: 'win32' })

    await expect(
      teardownProcessLedger({ ...OPTIONS, entries: [{ pid: 4101, command: 'node.exe x', startedAt: 2_000 }] }, deps),
    ).rejects.toThrow(/isAlive/)
  })
})

describe('C9: ledger wiring missing at teardown', () => {
  it('still scans for runner descendants, kills them, fails, and warns on stderr', async () => {
    const saved = process.env.KYBERDASH_SPAWN_LEDGER_FILE
    delete process.env.KYBERDASH_SPAWN_LEDGER_FILE
    const warnings: string[] = []
    const { deps, signals } = fakeDeps([
      { pid: process.pid, ppid: 1, startedAt: 2_000, command: 'node vitest' },
      { pid: 777, ppid: process.pid, startedAt: 2_000, command: 'node stray.js' },
    ])
    try {
      await expect(endLedgerRun(deps, (message) => void warnings.push(message))).rejects.toThrow(/777.*stray\.js/)
    } finally {
      if (saved !== undefined) process.env.KYBERDASH_SPAWN_LEDGER_FILE = saved
    }
    expect(signals).toContain('SIGTERM 777')
    expect(warnings.join('')).toMatch(/ledger/i)
  })

  it('warns but resolves when nothing is left behind', async () => {
    const saved = process.env.KYBERDASH_SPAWN_LEDGER_FILE
    delete process.env.KYBERDASH_SPAWN_LEDGER_FILE
    const warnings: string[] = []
    try {
      await expect(endLedgerRun(fakeDeps([]).deps, (message) => void warnings.push(message))).resolves.toBeUndefined()
    } finally {
      if (saved !== undefined) process.env.KYBERDASH_SPAWN_LEDGER_FILE = saved
    }
    expect(warnings.length).toBe(1)
  })
})

describe('C10: playwright.config.ts denies spawns for spec workers', () => {
  it('sets KYBERDASH_SPAWN_GUARD at config top level, not only in webServer.env', () => {
    const config = readFileSync(join(dashRoot, 'playwright.config.ts'), 'utf8')

    expect(config).toMatch(/^process\.env\.KYBERDASH_SPAWN_GUARD\s*=\s*'deny'/m)
  })
})

describe('R2: ledger file set but missing', () => {
  it('warns, still scans and kills survivors, and removes the temp dir', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyberdash-ledger-test-'))
    const saved = process.env.KYBERDASH_SPAWN_LEDGER_FILE
    process.env.KYBERDASH_SPAWN_LEDGER_FILE = join(dir, 'spawns.jsonl')
    const warnings: string[] = []
    const { deps, signals } = fakeDeps([{ pid: 778, ppid: process.pid, startedAt: 2_000, command: 'node stray2.js' }])
    try {
      await expect(endLedgerRun(deps, (message) => void warnings.push(message))).rejects.toThrow(/778.*stray2\.js/)
    } finally {
      if (saved === undefined) delete process.env.KYBERDASH_SPAWN_LEDGER_FILE
      else process.env.KYBERDASH_SPAWN_LEDGER_FILE = saved
    }
    expect(signals).toContain('SIGTERM 778')
    expect(warnings.join('')).toMatch(/spawns\.jsonl/)
    expect(existsSync(dir), 'the temp dir must be removed').toBe(false)
  })
})

describe('R3: the ledger file path is validated before any read or delete', () => {
  // The delete is always an injected recorder: no test here can remove a real directory except
  // the one it created itself under the OS temp directory.
  async function runWith(file: string, rows: readonly ProcessRow[] = []) {
    const saved = process.env.KYBERDASH_SPAWN_LEDGER_FILE
    process.env.KYBERDASH_SPAWN_LEDGER_FILE = file
    const warnings: string[] = []
    const removed: string[] = []
    const { deps } = fakeDeps(rows)
    try {
      await endLedgerRun(
        deps,
        (message) => void warnings.push(message),
        (dir) => void removed.push(dir),
      )
    } finally {
      if (saved === undefined) delete process.env.KYBERDASH_SPAWN_LEDGER_FILE
      else process.env.KYBERDASH_SPAWN_LEDGER_FILE = saved
    }
    return { warnings, removed }
  }

  it.each([
    '/',
    '/tmp/foo',
    'relative/spawns.jsonl',
    `${tmpdir()}/kyberdash-ledger-x/../spawns.jsonl`,
    `${tmpdir()}/other/spawns.jsonl`,
  ])('never deletes anything for %s and warns', async (file) => {
    const { warnings, removed } = await runWith(file)

    expect(removed, 'an unvalidated path must never reach a delete').toEqual([])
    expect(warnings.join('')).toMatch(/not a ledger file path/)
  })

  it('still scans for survivors when the path is invalid', async () => {
    await expect(
      runWith('/tmp/foo', [{ pid: 779, ppid: process.pid, startedAt: 2_000, command: 'node stray3.js' }]),
    ).rejects.toThrow(/779/)
  })

  it('removes exactly the validated kyberdash-ledger directory', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyberdash-ledger-test-'))
    try {
      const { removed } = await runWith(join(dir, 'spawns.jsonl'))

      expect(removed).toEqual([dir])
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })

  it('surfaces both the read error and the survivor report, chained by cause', async () => {
    const dir = mkdtempSync(join(tmpdir(), 'kyberdash-ledger-test-'))
    const file = join(dir, 'spawns.jsonl')
    mkdirSync(file) // reading a directory fails with EISDIR, not ENOENT
    try {
      const failure = await runWith(file, [
        { pid: 780, ppid: process.pid, startedAt: 2_000, command: 'node stray4.js' },
      ]).then(
        () => undefined,
        (error: unknown) => error,
      )

      expect(failure).toBeInstanceOf(Error)
      expect((failure as Error).message).toMatch(/780.*stray4\.js/)
      expect((failure as Error).message).toMatch(/EISDIR/)
      expect((failure as Error).cause).toBeDefined()
    } finally {
      rmSync(dir, { recursive: true, force: true })
    }
  })
})
