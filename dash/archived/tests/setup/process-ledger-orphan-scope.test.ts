// Process ledger orphan scope (storm guard T5, council m3). Failure this prevents: the
// teardown killed and failed on any PID-1 process whose command merely contained the dash root
// as a string, such as a sibling directory or a developer's own detached engine. Fakes only.
import { describe, expect, it } from 'vitest'

import { teardownProcessLedger, type ProcessLedgerDeps, type ProcessRow } from './process-ledger.js'

const ROOT = '/repo/dash'
const BASE = { runnerPid: 100, runStartedAt: 1_000, repoRoot: ROOT, graceMs: 5_000 } as const
const orphan = (pid: number, command: string): ProcessRow => ({ pid, ppid: 1, startedAt: 2_000, command })

function host(rows: readonly ProcessRow[]) {
  let table = [...rows]
  const signals: string[] = []
  const deps: ProcessLedgerDeps = {
    listProcesses: () => table,
    kill: (pid, signal) => {
      signals.push(`${signal} ${pid}`)
      if (signal === 'SIGKILL') table = table.filter((row) => row.pid !== pid)
    },
    sleep: async () => undefined,
  }
  return { deps, signals }
}

describe('process ledger orphan scope (storm guard T5, m3)', () => {
  it('does not match a sibling directory that only shares the root as a prefix', async () => {
    const h = host([orphan(401, 'node /repo/dash-foo/server.js'), orphan(402, 'node /repo/dashboard/x.js')])

    await expect(teardownProcessLedger({ ...BASE, entries: [] }, h.deps)).resolves.toBeUndefined()
    expect(h.signals).toEqual([])
  })

  it('reports an unlinked orphan with the exact root path but does not signal it', async () => {
    const h = host([orphan(403, `node ${ROOT}/dist/cli.js web`)])

    await expect(teardownProcessLedger({ ...BASE, entries: [] }, h.deps)).rejects.toThrow(
      /403.*dist\/cli\.js.*unlinked orphan, not killed/,
    )
    expect(h.signals, 'an unlinked orphan must never be signalled').toEqual([])
  })

  it('reports and kills an orphan whose command is a ledger command', async () => {
    const command = `node ${ROOT}/e2e/boot.ts`
    const h = host([orphan(404, command)])

    await expect(
      teardownProcessLedger({ ...BASE, entries: [{ pid: 999, command, startedAt: 1_500 }] }, h.deps),
    ).rejects.toThrow(/404/)
    expect(h.signals).toEqual(['SIGTERM 404', 'SIGKILL 404'])
  })
})
