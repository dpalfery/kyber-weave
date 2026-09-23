import { describe, expect, it } from 'vitest'

import type { SourceCheckpoint } from '../canon/source-state.js'
import { formatRefreshReport } from './report.js'
import { createCanonicalWriter } from './writer.js'

function checkpoint(sourceKey: string): SourceCheckpoint {
  return {
    harnessId: 'pi',
    sourceKey,
    providerId: 'pi',
    parserId: 'pi',
    parserContractVersion: '1',
    format: 'jsonl',
    sourceRootLabel: '~/.pi/agent/sessions',
    revisionToken: '1:1:1:1',
    coveredFromUtc: '2026-08-29T00:00:00.000Z',
    coveredThroughUtc: '2026-09-12T00:00:00.000Z',
    lastAttemptUtc: '2026-09-12T00:00:00.000Z',
    lastSuccessUtc: '2026-09-12T00:00:00.000Z',
    lastStatus: 'ok',
    lastErrorCode: null,
    unitCount: 1,
    recordCount: 0,
  }
}

describe('createCanonicalWriter', () => {
  it('serializes commits and applies backpressure at the injected capacity', async () => {
    let inFlight = 0
    let peak = 0
    const order: string[] = []
    const writer = createCanonicalWriter({
      capacity: 2,
      commit: (item) => {
        inFlight += 1
        peak = Math.max(peak, inFlight)
        order.push(item.checkpoint.sourceKey)
        inFlight -= 1
      },
    })

    await Promise.all([
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('a') }),
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('b') }),
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('c') }),
    ])
    await writer.drain()

    expect(peak).toBe(1)
    expect(order).toEqual(['a', 'b', 'c'])
  })

  it('commits records, provenance, and checkpoint in one call', async () => {
    const calls: Array<{ records: number; provenance: number; sourceKey: string }> = []
    const writer = createCanonicalWriter({
      capacity: 1,
      commit: (item) => {
        calls.push({
          records: item.records.length,
          provenance: item.provenance.length,
          sourceKey: item.checkpoint.sourceKey,
        })
      },
    })
    await writer.enqueue({
      records: [{ spanId: 'ab' } as never],
      provenance: [{ spanId: 'ab' } as never],
      checkpoint: checkpoint('unit-1'),
    })
    await writer.drain()
    expect(calls).toEqual([{ records: 1, provenance: 1, sourceKey: 'unit-1' }])
  })

  it('rejects only the failed enqueue, commits later units, and lets drain settle for the report', async () => {
    const committed: string[] = []
    const writer = createCanonicalWriter({
      capacity: 2,
      commit: (item) => {
        if (item.checkpoint.sourceKey === 'failing') {
          throw new Error('commit boom')
        }
        committed.push(item.checkpoint.sourceKey)
      },
    })

    const [first, second] = await Promise.allSettled([
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('failing') }),
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('ok') }),
    ])
    await expect(writer.drain()).resolves.toBeUndefined()

    expect(first).toMatchObject({ status: 'rejected', reason: expect.objectContaining({ message: 'commit boom' }) })
    expect(second).toEqual({ status: 'fulfilled', value: undefined })
    expect(committed).toEqual(['ok'])

    const rows = [
      {
        harnessId: 'failing',
        units: 1,
        changed: 1,
        skipped: 0,
        created: 0,
        updated: 0,
        problems: 1,
        status: 'failed' as const,
        diagnostic: first.status === 'rejected' ? (first.reason as Error).message : undefined,
      },
      {
        harnessId: 'ok',
        units: 1,
        changed: 1,
        skipped: 0,
        created: 1,
        updated: 0,
        problems: 0,
        status: 'ok' as const,
      },
    ]
    const failedJobs = rows.filter((row) => row.status === 'failed').length
    const report = formatRefreshReport({
      historyWeeks: 2,
      commandStartedAt: '2026-09-12T00:00:00.000Z',
      rows,
      derived: { sessions: 0, runs: 0, executions: 0, rollups: 0 },
      failedJobs,
      derivationFailed: false,
      exitCode: failedJobs > 0 ? 1 : 0,
    })

    expect(report).toContain('failing')
    expect(report).toContain('ok')
    expect(report).toContain('failed')
    expect(report).toContain('Refresh completed with 1 failed harness job.')
    expect(failedJobs > 0 ? 1 : 0).toBe(1)
  })

  it('lets drain settle after a failed commit without rethrowing', async () => {
    const writer = createCanonicalWriter({
      capacity: 1,
      commit: () => {
        throw new Error('commit boom')
      },
    })
    await expect(
      writer.enqueue({ records: [], provenance: [], checkpoint: checkpoint('failing') }),
    ).rejects.toThrow('commit boom')
    await expect(writer.drain()).resolves.toBeUndefined()
  })
})
