// Process ledger review fixes (storm guard T5, PR #477 C8-C10). Fakes only; no real process.
import { readFileSync } from 'node:fs'
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
