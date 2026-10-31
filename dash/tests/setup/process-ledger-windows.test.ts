// Process ledger on Windows (storm guard T5 rework). Failure this prevents: the global
// teardown shelled out to ps on Windows and threw, failing the whole vitest job. On win32 the
// descendant and PID-1 orphan scans are skipped; only ledger entries are checked, by pid.
// No real process starts here: ps, kill and liveness are all injected.
import { describe, expect, it } from 'vitest'

import { createSystemLedgerDeps, teardownProcessLedger, type ProcessLedgerDeps } from './process-ledger.js'

const OPTIONS = { runnerPid: 100, runStartedAt: 1_000, repoRoot: 'C:\\repo\\dash', graceMs: 5_000 } as const
const entry = { pid: 4101, command: 'node.exe child.js', startedAt: 1_500 }

function windowsDeps(alive: Set<number>) {
  const psCalls: string[] = []
  const signals: string[] = []
  const base = createSystemLedgerDeps('win32', (file) => {
    psCalls.push(file)
    throw new Error('ps must not run on win32')
  })
  const deps: ProcessLedgerDeps = {
    ...base,
    isAlive: (pid) => alive.has(pid),
    kill: (pid, signal) => {
      signals.push(`${signal} ${pid}`)
      if (signal === 'SIGKILL') alive.delete(pid)
    },
    sleep: async () => undefined,
  }
  return { deps, psCalls, signals }
}

describe('process ledger on win32 (storm guard T5 rework)', () => {
  it('lists no processes and never calls ps', () => {
    const { deps, psCalls } = windowsDeps(new Set())

    expect(deps.listProcesses()).toEqual([])
    expect(psCalls).toEqual([])
  })

  it('resolves without throwing or signalling when no ledger entry is alive', async () => {
    const { deps, psCalls, signals } = windowsDeps(new Set())

    await expect(teardownProcessLedger({ ...OPTIONS, entries: [entry] }, deps)).resolves.toBeUndefined()
    expect(psCalls).toEqual([])
    expect(signals).toEqual([])
  })

  it('still reports and kills a ledger entry whose pid is alive', async () => {
    const { deps, psCalls, signals } = windowsDeps(new Set([4101]))

    await expect(teardownProcessLedger({ ...OPTIONS, entries: [entry] }, deps)).rejects.toThrow(
      /4101.*node\.exe child\.js/,
    )
    expect(psCalls).toEqual([])
    expect(signals).toEqual(['SIGTERM 4101', 'SIGKILL 4101'])
  })

  it('skips the descendant and PID-1 orphan scans: a table would not be read', async () => {
    const { deps } = windowsDeps(new Set())
    const poisoned: ProcessLedgerDeps = {
      ...deps,
      listProcesses: () => [{ pid: 7, ppid: 100, startedAt: 2_000, command: 'node C:\\repo\\dash\\x.js' }],
    }

    await expect(teardownProcessLedger({ ...OPTIONS, entries: [] }, poisoned)).resolves.toBeUndefined()
  })
})
