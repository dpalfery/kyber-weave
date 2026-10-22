// Coverage after a wipe (issue #319 T11 RED): the refresh footer and the harness table
// report "covered from .. through .." from the last successful refresh_run row. A clean
// deletes the data that window described, so right after a wipe the bridge must report NO
// folder window, even though the pre-wipe refresh_run rows are still there. Those rows are
// audit history and are not deleted; the coverage read ignores rows that started before
// the store's `last_clean_at`. A refresh that starts after the wipe describes the new data
// and counts again. Fails today: the bridge reads the newest success row unconditionally.
import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'

const { acquireStoreRefreshLockMock } = vi.hoisted(() => ({
  acquireStoreRefreshLockMock: vi.fn(async () => ({
    outcome: 'acquired' as const,
    handle: { token: 'coverage-after-clean', release: async () => {}, verifyStillOwner: async () => true },
  })),
}))
vi.mock('../refresh/lock.js', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../refresh/lock.js')>()
  return { ...actual, acquireStoreRefreshLock: acquireStoreRefreshLockMock }
})

const roots: string[] = []
afterEach(() => {
  for (const root of roots.splice(0)) rmSync(root, { recursive: true, force: true })
})

const PRE_WIPE_START = '2026-10-01T00:00:00.000Z'

function seedSuccessRun(store: CanonStore, id: string, startedAt: string, weeks: number): void {
  store.startRefreshRun({ id, startedAt, pid: 4242, trigger: 'cli', historyWeeks: weeks })
  store.completeRefreshRun(id, 'success', new Date(Date.parse(startedAt) + 60_000).toISOString(), 'ok')
}

describe('coverage read after a clean', () => {
  it('reports a folder window before the wipe (control)', () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedSuccessRun(store, 'pre', PRE_WIPE_START, 2)
      const state = bridge.getRefreshState()
      expect(state.historyWeeks).toBe(2)
      expect(state.coveredFrom).not.toBeNull()
      expect(state.coveredThrough).toBe(PRE_WIPE_START)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('reports NO folder window after a wipe-all, while the pre-wipe rows are kept', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedSuccessRun(store, 'pre', PRE_WIPE_START, 2)
      store.logIngest('otlp:logs', 3)

      await bridge.cleanDatabase({ all: true, confirm: true })

      const state = bridge.getRefreshState()
      expect(state.historyWeeks).toBeNull()
      expect(state.coveredFrom).toBeNull()
      expect(state.coveredThrough).toBeNull()
      // Audit history survives the wipe.
      expect(store.listRefreshRuns().map((r) => r.id)).toContain('pre')
      expect(store.getIngestLog().length).toBeGreaterThan(0)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('reports NO folder window after a harness-scoped wipe too', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedSuccessRun(store, 'pre', PRE_WIPE_START, 2)
      await bridge.cleanDatabase({ harnesses: ['pi'], confirm: true })
      expect(bridge.getRefreshState().coveredFrom).toBeNull()
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('counts a refresh that starts after the wipe', async () => {
    const store = new CanonStore(':memory:')
    const bridge = new KyberBridge({ store })
    try {
      seedSuccessRun(store, 'pre', PRE_WIPE_START, 2)
      await bridge.cleanDatabase({ all: true, confirm: true })
      const afterWipe = new Date(Date.now() + 3_600_000).toISOString()
      seedSuccessRun(store, 'post', afterWipe, 1)

      const state = bridge.getRefreshState()
      expect(state.historyWeeks).toBe(1)
      expect(state.coveredThrough).toBe(afterWipe)
    } finally {
      bridge.close()
      store.close()
    }
  })

  it('applies the same rule when the bridge reads a store file through its own handle', async () => {
    const root = mkdtempSync(join(tmpdir(), 'kyber-coverage-clean-'))
    roots.push(root)
    const dbPath = join(root, 'canon.db')
    const seed = new CanonStore(dbPath)
    seedSuccessRun(seed, 'pre', PRE_WIPE_START, 2)
    seed.close()

    const bridge = new KyberBridge({ canonPath: dbPath, reopenCheckIntervalMs: 0 })
    try {
      await bridge.cleanDatabase({ all: true, confirm: true })
      const state = bridge.getRefreshState()
      expect(state.coveredFrom).toBeNull()
      expect(state.coveredThrough).toBeNull()
      expect(state.historyWeeks).toBeNull()
    } finally {
      bridge.close()
    }
    const reopened = new CanonStore(dbPath)
    try {
      expect(reopened.listRefreshRuns().map((r) => r.id)).toContain('pre')
    } finally {
      reopened.close()
    }
  })
})
