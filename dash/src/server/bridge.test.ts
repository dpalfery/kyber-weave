import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { DatabaseSync } from 'node:sqlite'

import { CanonStore } from '../canon/store.js'
import { KyberBridge, type ProblemRow, type QuarantineRow } from './bridge.js'

type QuarantineRowWithTimestamp = QuarantineRow & { timestamp?: string | null }

interface PagedBridge {
  getQuarantine(limit?: number, offset?: number): QuarantineRowWithTimestamp[]
  getQuarantineCount(): number
  getProblems(limit?: number, offset?: number): ProblemRow[]
  getProblemCount(): number
  close(): void
}

describe('bridge quarantine problems: pagination and metadata contract (Task T3)', () => {
  let tempDir: string
  let dbPath: string
  let store: CanonStore
  let db: DatabaseSync
  let bridge: KyberBridge
  let pagedBridge: PagedBridge

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-bridge-t3-'))
    dbPath = join(tempDir, 'canon.db')
    store = new CanonStore(dbPath)
    db = new DatabaseSync(dbPath)
    bridge = new KyberBridge({ canonDb: db })
    pagedBridge = bridge as unknown as PagedBridge
  })

  afterEach(() => {
    bridge.close()
    store.close()
    rmSync(tempDir, { recursive: true, force: true })
  })

  describe('pagination via limit and offset', () => {
    it('supports pagination via limit and offset for getQuarantine', () => {
      // Seed 25 quarantine entries with ordered timestamps
      for (let i = 0; i < 25; i++) {
        const id = String(i).padStart(2, '0')
        store.quarantine(
          `span-quar-${id}`,
          ['copilot'],
          'unmapped namespace',
          {
            source: `copilot:source-${id}`,
            name: `turn-${id}`,
            timestamp: `2026-09-30T10:${id}:00.000Z`,
          },
        )
      }

      const all = pagedBridge.getQuarantine(100, 0)
      expect(all).toHaveLength(25)

      // Query page 1 (items 0 to 9)
      const page1 = pagedBridge.getQuarantine(10, 0)
      expect(page1).toHaveLength(10)
      expect(page1.map((r) => r.span_id)).toEqual(all.slice(0, 10).map((r) => r.span_id))

      // Query page 2 (items 10 to 19)
      const page2 = pagedBridge.getQuarantine(10, 10)
      expect(page2).toHaveLength(10)
      expect(page2.map((r) => r.span_id)).toEqual(all.slice(10, 20).map((r) => r.span_id))
      expect(page2[0].span_id).toBe(all[10].span_id)

      // Query page 3 (items 20 to 24)
      const page3 = pagedBridge.getQuarantine(10, 20)
      expect(page3).toHaveLength(5)
      expect(page3.map((r) => r.span_id)).toEqual(all.slice(20, 25).map((r) => r.span_id))

      // Query beyond total count returns empty
      const pagePastEnd = pagedBridge.getQuarantine(10, 50)
      expect(pagePastEnd).toHaveLength(0)
    })

    it('supports pagination via limit and offset for getProblems', () => {
      // Seed 25 problems
      for (let i = 0; i < 25; i++) {
        const id = String(i).padStart(2, '0')
        store.recordProblem({
          spanId: `span-prob-${id}`,
          sessionId: 'session-pagination-test',
          severity: 'warning',
          code: `TOKEN_LIMIT_${id}`,
          message: `Problem message ${id}`,
          location: `loc-${id}`,
          harness: 'copilot',
          timestamp: `2026-09-30T10:${id}:00.000Z`,
        })
      }

      const all = pagedBridge.getProblems(100, 0)
      expect(all).toHaveLength(25)

      // Query page 1 (items 0 to 9)
      const page1 = pagedBridge.getProblems(10, 0)
      expect(page1).toHaveLength(10)
      expect(page1.map((r) => r.span_id)).toEqual(all.slice(0, 10).map((r) => r.span_id))

      // Query page 2 (items 10 to 19)
      const page2 = pagedBridge.getProblems(10, 10)
      expect(page2).toHaveLength(10)
      expect(page2.map((r) => r.span_id)).toEqual(all.slice(10, 20).map((r) => r.span_id))
      expect(page2[0].span_id).toBe(all[10].span_id)

      // Query page 3 (items 20 to 24)
      const page3 = pagedBridge.getProblems(10, 20)
      expect(page3).toHaveLength(5)
      expect(page3.map((r) => r.span_id)).toEqual(all.slice(20, 25).map((r) => r.span_id))

      // Query beyond total count returns empty
      const pagePastEnd = pagedBridge.getProblems(10, 50)
      expect(pagePastEnd).toHaveLength(0)
    })
  })

  describe('quarantine and problems column mapping and metadata', () => {
    it('returns quarantine rows with source, name, and timestamp populated from database', () => {
      store.quarantine(
        'span-quar-meta-test',
        ['copilot', 'gen_ai'],
        'unmapped namespace',
        {
          source: 'copilot:chat-worker',
          name: 'turn_completion',
          timestamp: '2026-09-30T15:30:00.000Z',
        },
      )

      const quarantined = pagedBridge.getQuarantine()
      const row = quarantined.find((r) => r.span_id === 'span-quar-meta-test')
      expect(row).toBeDefined()
      expect(row?.source).toBe('copilot:chat-worker')
      expect(row?.name).toBe('turn_completion')
      expect(row?.timestamp).toBe('2026-09-30T15:30:00.000Z')
    })

    it('returns problems with actual harness instead of repeating location / spanId', () => {
      const location = 'span-prob-harness-test'
      store.recordProblem({
        spanId: 'span-prob-harness-test',
        sessionId: 'session-problem-test',
        severity: 'error',
        code: 'TOKEN_PARSE_FAILURE',
        message: 'Failed parsing response tokens',
        location,
        harness: 'copilot',
        timestamp: '2026-09-30T15:30:00.000Z',
      })

      const problems = pagedBridge.getProblems()
      const prob = problems.find((p) => p.span_id === 'span-prob-harness-test')
      expect(prob).toBeDefined()
      expect(prob?.harness).toBe('copilot')
      expect(prob?.harness).not.toBe(location)
      expect(prob?.harness).not.toBe(prob?.span_id)
    })

    it('ensures bridge constructed with store shares database handle for rows and counts', () => {
      const storeBridge = new KyberBridge({ store }) as unknown as PagedBridge

      try {
        const quarCount = storeBridge.getQuarantineCount()
        const quarRows = storeBridge.getQuarantine(100, 0)
        expect(quarCount).toBe(quarRows.length)

        const probCount = storeBridge.getProblemCount()
        const probRows = storeBridge.getProblems(100, 0)
        expect(probCount).toBe(probRows.length)
      } finally {
        storeBridge.close()
      }
    })
  })
})

