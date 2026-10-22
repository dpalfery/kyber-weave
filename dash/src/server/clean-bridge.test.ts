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

// The bridge holds the store refresh lock around `cleanDatabase` (issue #312
// F4). Stub only the acquire — every other lock export stays real — so these
// tests never touch the real `~/.kyberdash` lock file.
const { acquireStoreRefreshLockMock } = vi.hoisted(() => ({
  acquireStoreRefreshLockMock: vi.fn(async () => ({
    outcome: 'acquired' as const,
    handle: {
      token: 'clean-bridge-test',
      release: async () => {},
      verifyStillOwner: async () => true,
    },
  })),
}))

// A clean that did not ask for a backfill must never scan source logs (issue #319 T11).
const { importFolderHistoryMock } = vi.hoisted(() => ({
  // Typed with the real signature so `mock.calls[0][1]` keeps the options tuple; a bare
  // zero-argument mock would widen the call tuple to `[]`.
  importFolderHistoryMock: vi.fn(async (_store: unknown, _options: { weeks?: number; harnesses?: readonly string[]; trigger: string }) => ({})),
}))
vi.mock('../refresh/folder-import.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../refresh/folder-import.js')>()
  return { ...actual, importFolderHistory: importFolderHistoryMock }
})

vi.mock('../refresh/lock.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../refresh/lock.js')>()
  return { ...actual, acquireStoreRefreshLock: acquireStoreRefreshLockMock }
})

const temporaryRoots: string[] = []

afterEach(() => {
  for (const root of temporaryRoots.splice(0)) rmSync(root, { recursive: true, force: true })
  vi.restoreAllMocks()
  importFolderHistoryMock.mockClear()
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

  it('does not import when the request omits reingestWeeks (tray default)', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedRecord(store, 'span-pi', 'pi')

      const report = await bridge.cleanDatabase({ all: true, confirm: true })

      expect(report.reingested).toBe(false)
      expect(report.historyWeeks).toBeNull()
      expect(importFolderHistoryMock).not.toHaveBeenCalled()
      expect(store.count()).toBe(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('imports exactly the requested number of weeks when reingestWeeks is given', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedRecord(store, 'span-pi', 'pi')

      const report = await bridge.cleanDatabase({ all: true, confirm: true, reingestWeeks: 3 })

      expect(report.reingested).toBe(true)
      expect(report.historyWeeks).toBe(3)
      expect(importFolderHistoryMock).toHaveBeenCalledTimes(1)
      expect(importFolderHistoryMock.mock.calls[0]?.[1]).toMatchObject({ weeks: 3 })
    } finally {
      bridge.close()
      store.close()
    }
  })
})
