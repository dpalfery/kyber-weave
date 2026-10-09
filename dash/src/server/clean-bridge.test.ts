// Bridge-level clean against a real store file (issue #312, plan T7): the
// route's 200 path runs `cleanDatabase` on a short-lived read-write store at
// `canonPath` while the bridge's own handle stays read-only, and the bridge
// serves the emptied store afterwards through its normal reconcile.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  vi.restoreAllMocks()
})

function seedRecord(store: CanonStore, spanId: string, harness: string): void {
  store.upsert({
    spanId,
    traceId: `trace-${spanId}`,
    parentSpanId: null,
    source: 'test:clean',
    harness,
    sessionId: `${harness}-session`,
    name: 'probe turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-10-01T12:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: {
      freshInput: 10,
      cacheRead: 0,
      cacheCreation: 0,
      output: 5,
      reasoning: 0,
      reportedInput: 10,
      reportedOutput: 5,
    },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
  })
}

describe('KyberBridge.cleanDatabase against a store file (issue #312)', () => {
  it('wipes the file store and serves the emptied store afterwards', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-clean-bridge-'))
    temporaryRoots.push(root)
    const dbPath = join(root, 'canon.db')
    const seed = new CanonStore(dbPath)
    seedRecord(seed, 'span-pi', 'pi')
    seedRecord(seed, 'span-cursor', 'cursor')
    seed.close()

    const bridge = new KyberBridge({ canonPath: dbPath, reopenCheckIntervalMs: 0 })
    try {
      // No store injection: the bridge must open its own short-lived writer.
      const report = await bridge.cleanDatabase({
        harnesses: ['pi'],
        confirm: true,
        reingestWeeks: null,
      })

      expect(report.harnesses).toEqual(['pi'])
      expect(report.wipe.records).toBe(1)
      expect(report.reingested).toBe(false)

      const reopened = new CanonStore(dbPath)
      try {
        expect(reopened.get('span-pi')).toBeUndefined()
        expect(reopened.get('span-cursor')?.spanId).toBe('span-cursor')
      } finally {
        reopened.close()
      }

      // The bridge serves what the file holds: the wiped harness is gone,
      // the untouched harness still lists its session.
      const sessions = bridge.listSessions(100)
      expect(sessions.every((s) => s.harness !== 'pi')).toBe(true)
      expect(sessions.some((s) => s.harness === 'cursor')).toBe(true)
    } finally {
      bridge.close()
    }
  })

  it('cleans through an injected store without touching the file', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedRecord(store, 'span-pi', 'pi')

      const report = await bridge.cleanDatabase({ all: true, confirm: true, reingestWeeks: null })

      expect(report.harnesses).toEqual(['*'])
      expect(store.count()).toBe(0)
    } finally {
      bridge.close()
      store.close()
    }
  })
})
