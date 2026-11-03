// Process ledger vs the vitest pool (storm guard T5, rework). Failure this prevents: the
// global teardown ran while a forks worker was still exiting and failed a passing run with
// "pid <n>: (node)". A worker that is exiting is not a leftover; a real one still is.
import { describe, expect, it } from 'vitest'

import { settleProcessLedger, teardownProcessLedger, type ProcessLedgerDeps, type ProcessRow } from './process-ledger.js'

const OPTIONS = { entries: [], runnerPid: 100, runStartedAt: 1_000, repoRoot: '/repo/dash', graceMs: 5_000 } as const
const row = (pid: number, ppid: number, command: string): ProcessRow => ({ pid, ppid, startedAt: 2_000, command })

/** A fake host whose listed rows change as the fake clock advances. */
function host(rowsAt: (clockMs: number) => readonly ProcessRow[]) {
  let clock = 0
  const signals: string[] = []
  const deps: ProcessLedgerDeps = {
    listProcesses: () => rowsAt(clock),
    kill: (pid, signal) => void signals.push(`${signal} ${pid}`),
    sleep: async (ms) => void (clock += ms),
  }
  return { deps, signals }
}

describe('process ledger settle (storm guard T5 rework)', () => {
  it('does not report a pool worker that is still exiting when the teardown starts', async () => {
    const h = host((clock) => (clock < 300 ? [row(101, 100, '(node)')] : []))

    await settleProcessLedger(OPTIONS, h.deps, { maxWaitMs: 3_000, pollMs: 100 })
    await expect(teardownProcessLedger(OPTIONS, h.deps)).resolves.toBeUndefined()
    expect(h.signals, 'an exiting worker must not be signalled').toEqual([])
  })

  it('still reports and kills a real leftover that outlives the settle window', async () => {
    const h = host(() => [row(102, 100, 'node leftover.js')])

    await settleProcessLedger(OPTIONS, h.deps, { maxWaitMs: 1_000, pollMs: 100 })
    await expect(teardownProcessLedger(OPTIONS, h.deps)).rejects.toThrow(/102.*node leftover\.js/)
    expect(h.signals).toContain('SIGTERM 102')
  })
})
