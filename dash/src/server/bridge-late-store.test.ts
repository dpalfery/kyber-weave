import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRequire } from 'node:module'
import { mkdtempSync, rmSync, renameSync, unlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { IncomingMessage, ServerResponse } from 'node:http'

import { KyberBridge } from './bridge.js'
import { CanonStore } from '../canon/store.js'
import { handleKyberRequest } from './routes.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

/**
 * T3 RED: bridge-late-store.test.ts
 *
 * Tests that KyberBridge follows the file currently at canonPath:
 * - opens it read-only once it exists
 * - reopens when the file is replaced (device/inode change)
 * - stops serving a removed file
 * - throttles reopening checks based on reopenCheckIntervalMs
 * - never swaps an injected canonDb handle or :memory: DB
 * - returns [] after close()
 */

describe('KyberBridge: late-store opening and reopening', () => {
  let tempDir: string

  beforeEach(() => {
    tempDir = mkdtempSync(join(tmpdir(), 'kyber-bridge-late-store-'))
  })

  afterEach(() => {
    rmSync(tempDir, { recursive: true, force: true })
  })

  describe('(a) Late store: bridge created before file exists', () => {
    it('returns empty lists before canon.db exists, then returns data after creation', () => {
      const canonPath = join(tempDir, 'canon.db')

      // Create bridge before file exists
      const bridge = new KyberBridge({ canonPath })
      try {
        // Before file exists: should return empty
        expect(bridge.listHarnessRollups()).toEqual([])
        expect(bridge.listSessions()).toEqual([])

        // Create store and write data
        const store = new CanonStore(canonPath)
        store.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })

        // Write a session
        store.db.prepare(`
          INSERT INTO session (
            session_id, harness, label, is_subagent, parent_session,
            agent_name, repo, branch, started, ended, payload
          ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
        `).run(
          'sess-1', 'alpha', 'Test Session', 0, null, null, null, null,
          '2026-01-01T00:00:00Z', null, JSON.stringify({ id: 'sess-1' })
        )

        store.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store.db.close()

        // After file exists: bridge should see the data
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([
            expect.objectContaining({ harness: 'alpha', sampleCount: 1 })
          ])
        )
        expect(bridge.listSessions()).toEqual(
          expect.arrayContaining([
            expect.objectContaining({ session_id: 'sess-1', harness: 'alpha' })
          ])
        )
      } finally {
        bridge.close()
      }
    })
  })

  describe('(b) Late store through the report route', () => {
    it('handleKyberRequest returns harnesses: [] before store exists, then data after', () => {
      const canonPath = join(tempDir, 'canon.db')
      const bridge = new KyberBridge({ canonPath })

      try {
        // Before file exists
        const result1 = callReportRoute('/api/kyber/report', bridge)
        expect(result1.status).toBe(200)
        const body1 = result1.body as { harnesses?: unknown[] }
        expect(body1.harnesses).toEqual([])

        // Create store and write data
        const store = new CanonStore(canonPath)
        store.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        store.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store.db.close()

        // After file exists
        const result2 = callReportRoute('/api/kyber/report', bridge)
        expect(result2.status).toBe(200)
        const body2 = result2.body as { harnesses?: unknown[] }
        expect(body2.harnesses).toEqual(
          expect.arrayContaining([
            expect.objectContaining({ harness: 'alpha' })
          ])
        )
      } finally {
        bridge.close()
      }
    })
  })

  describe('(c) Close is final', () => {
    it('after close(), bridge returns [] and opens nothing even when file exists or is replaced', () => {
      const canonPath = join(tempDir, 'canon.db')

      // Create store with data
      const store1 = new CanonStore(canonPath)
      store1.upsertHarnessRollup({
        harness: 'alpha',
        sampleCount: 1,
        contextPressureMedian: 0.5,
        contextPressureP95: 0.8,
        cacheHitRate: 0.6,
        toolYield: 0.7,
        delegationOverhead: 0.2,
        fieldCoverage: 0.9,
        measurability: {},
      })
      store1.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      store1.db.close()

      const bridge = new KyberBridge({ canonPath })
      try {
        // Verify bridge sees data before close
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([
            expect.objectContaining({ harness: 'alpha' })
          ])
        )

        // Close the bridge
        bridge.close()

        // After close: returns []
        expect(bridge.listHarnessRollups()).toEqual([])
        expect(bridge.listSessions()).toEqual([])

        // Even if we replace the file, still returns []
        const store2 = new CanonStore(canonPath)
        store2.upsertHarnessRollup({
          harness: 'beta',
          sampleCount: 2,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        store2.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store2.db.close()

        expect(bridge.listHarnessRollups()).toEqual([])
      } finally {
        bridge.close()
      }
    })
  })

  describe('(d) Characterization: no stale snapshot', () => {
    it('bridge created after file exists sees rows a separate writer commits afterwards', () => {
      const canonPath = join(tempDir, 'canon.db')

      // Create store with initial data
      const store = new CanonStore(canonPath)
      try {
        store.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        store.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')

        // Create bridge after file exists
        const bridge = new KyberBridge({ canonPath })
        try {
          // Bridge sees alpha
          let rollups = bridge.listHarnessRollups()
          expect(rollups).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )

          // Issue one query to establish connection
          expect(bridge.listSessions()).toEqual([])

          // Separate writer commits new data
          store.upsertHarnessRollup({
            harness: 'beta',
            sampleCount: 2,
            contextPressureMedian: 0.5,
            contextPressureP95: 0.8,
            cacheHitRate: 0.6,
            toolYield: 0.7,
            delegationOverhead: 0.2,
            fieldCoverage: 0.9,
            measurability: {},
          })
          store.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')

          // Bridge should see the new data (no stale snapshot)
          rollups = bridge.listHarnessRollups()
          expect(rollups).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' }),
              expect.objectContaining({ harness: 'beta' })
            ])
          )
        } finally {
          bridge.close()
        }
      } finally {
        store.db.close()
      }
    })
  })

  describe('(e) Replaced file (skip on Windows)', () => {
    it('owned bridge serves new file after canonPath is replaced', function () {
      if (process.platform === 'win32') {
        this.skip()
      }

      const canonPath = join(tempDir, 'canon.db')

      // Create store A with alpha
      const storeA = new CanonStore(canonPath)
      try {
        storeA.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        storeA.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.db.close()

        const bridge = new KyberBridge({ canonPath })
        try {
          // Bridge serves alpha from store A
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )

          // Correct replacement: checkpoint and close old file, create new one, rename over
          // (We don't actually hold the old file open in this test, so we just create store B
          // at a temp location, then move it over canonPath)
          const tempStorePath = join(tempDir, 'canon-new.db')
          const storeB = new CanonStore(tempStorePath)
          try {
            storeB.upsertHarnessRollup({
              harness: 'beta',
              sampleCount: 2,
              contextPressureMedian: 0.5,
              contextPressureP95: 0.8,
              cacheHitRate: 0.6,
              toolYield: 0.7,
              delegationOverhead: 0.2,
              fieldCoverage: 0.9,
              measurability: {},
            })
            storeB.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.db.close()

            // Replace canonPath with the new file
            renameSync(tempStorePath, canonPath)

            // Clean up sidecars from old file
            const walPath = canonPath + '-wal'
            const shmPath = canonPath + '-shm'
            try {
              unlinkSync(walPath)
            } catch {}
            try {
              unlinkSync(shmPath)
            } catch {}

            // Bridge should now serve beta from store B
            const rollups = bridge.listHarnessRollups()
            expect(rollups).toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'beta' })
              ])
            )
            expect(rollups).not.toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'alpha' })
              ])
            )
          } finally {
            try {
              storeB.db.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.db.close()
        } catch {}
      }
    })
  })

  describe('(f) Removed file (skip on Windows)', () => {
    it('owned bridge returns [] when canonPath and its sidecars are removed', function () {
      if (process.platform === 'win32') {
        this.skip()
      }

      const canonPath = join(tempDir, 'canon.db')

      // Create store A with alpha
      const storeA = new CanonStore(canonPath)
      try {
        storeA.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        storeA.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.db.close()

        const bridge = new KyberBridge({ canonPath })
        try {
          // Bridge serves alpha
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )

          // Remove the file and sidecars
          unlinkSync(canonPath)
          const walPath = canonPath + '-wal'
          const shmPath = canonPath + '-shm'
          try {
            unlinkSync(walPath)
          } catch {}
          try {
            unlinkSync(shmPath)
          } catch {}

          // Bridge returns []
          expect(bridge.listHarnessRollups()).toEqual([])
          expect(bridge.listSessions()).toEqual([])

          // Create a new store at canonPath with gamma
          const storeB = new CanonStore(canonPath)
          try {
            storeB.upsertHarnessRollup({
              harness: 'gamma',
              sampleCount: 3,
              contextPressureMedian: 0.5,
              contextPressureP95: 0.8,
              cacheHitRate: 0.6,
              toolYield: 0.7,
              delegationOverhead: 0.2,
              fieldCoverage: 0.9,
              measurability: {},
            })
            storeB.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.db.close()

            // Bridge returns gamma
            expect(bridge.listHarnessRollups()).toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'gamma' })
              ])
            )
          } finally {
            try {
              storeB.db.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.db.close()
        } catch {}
      }
    })
  })

  describe('(g) Throttle: reopenCheckIntervalMs', () => {
    it('bridge with reopenCheckIntervalMs throttles reopening based on injected now clock', () => {
      const canonPath = join(tempDir, 'canon.db')

      // Create store A with alpha
      const storeA = new CanonStore(canonPath)
      try {
        storeA.upsertHarnessRollup({
          harness: 'alpha',
          sampleCount: 1,
          contextPressureMedian: 0.5,
          contextPressureP95: 0.8,
          cacheHitRate: 0.6,
          toolYield: 0.7,
          delegationOverhead: 0.2,
          fieldCoverage: 0.9,
          measurability: {},
        })
        storeA.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.db.close()

        // Mock clock starting at t=0
        let currentTime = 0
        const mockNow = () => currentTime

        const bridge = new KyberBridge({
          canonPath,
          reopenCheckIntervalMs: 1000,
          now: mockNow,
        })
        try {
          // At t=0: bridge serves alpha
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )

          // Replace file with store B (beta)
          const tempStorePath = join(tempDir, 'canon-new.db')
          const storeB = new CanonStore(tempStorePath)
          try {
            storeB.upsertHarnessRollup({
              harness: 'beta',
              sampleCount: 2,
              contextPressureMedian: 0.5,
              contextPressureP95: 0.8,
              cacheHitRate: 0.6,
              toolYield: 0.7,
              delegationOverhead: 0.2,
              fieldCoverage: 0.9,
              measurability: {},
            })
            storeB.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.db.close()

            renameSync(tempStorePath, canonPath)
            try {
              unlinkSync(canonPath + '-wal')
            } catch {}
            try {
              unlinkSync(canonPath + '-shm')
            } catch {}

            // At t=999: still within interval, bridge still serves alpha
            currentTime = 999
            const rollups1 = bridge.listHarnessRollups()
            expect(rollups1).toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'alpha' })
              ])
            )

            // At t=1000: interval expired, bridge serves beta
            currentTime = 1000
            const rollups2 = bridge.listHarnessRollups()
            expect(rollups2).toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'beta' })
              ])
            )
          } finally {
            try {
              storeB.db.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.db.close()
        } catch {}
      }
    })
  })

  describe('(h) Injected handle is never swapped', () => {
    it('bridge built with injected canonDb keeps returning its data after canonPath is replaced', function () {
      if (process.platform === 'win32') {
        this.skip()
      }

      const canonPath = join(tempDir, 'canon.db')

      // Create store A with alpha, open it with an injected handle
      const storeA = new CanonStore(canonPath)
      storeA.upsertHarnessRollup({
        harness: 'alpha',
        sampleCount: 1,
        contextPressureMedian: 0.5,
        contextPressureP95: 0.8,
        cacheHitRate: 0.6,
        toolYield: 0.7,
        delegationOverhead: 0.2,
        fieldCoverage: 0.9,
        measurability: {},
      })
      storeA.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.db.close()

      // Open the store for reading (injected handle)
      const injectedDb = new DatabaseSync(canonPath, { readOnly: true })
      try {
        const bridge = new KyberBridge({
          canonDb: injectedDb,
          canonPath,
        })
        try {
          // Bridge serves alpha from injected handle
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )

          // Replace canonPath with store B (beta)
          const tempStorePath = join(tempDir, 'canon-new.db')
          const storeB = new CanonStore(tempStorePath)
          storeB.upsertHarnessRollup({
            harness: 'beta',
            sampleCount: 2,
            contextPressureMedian: 0.5,
            contextPressureP95: 0.8,
            cacheHitRate: 0.6,
            toolYield: 0.7,
            delegationOverhead: 0.2,
            fieldCoverage: 0.9,
            measurability: {},
          })
          storeB.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
          storeB.db.close()

          renameSync(tempStorePath, canonPath)
          try {
            unlinkSync(canonPath + '-wal')
          } catch {}
          try {
            unlinkSync(canonPath + '-shm')
          } catch {}

          // Bridge STILL serves alpha from the injected handle (never swapped)
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([
              expect.objectContaining({ harness: 'alpha' })
            ])
          )
        } finally {
          bridge.close()
        }
      } finally {
        try {
          injectedDb.close()
        } catch {
          // bridge.close() already closed the injected db
        }
      }
    })
  })
})

/**
 * Helper to call handleKyberRequest with mocked req/res
 */
function callReportRoute(href: string, bridge: KyberBridge): { status: number; body: unknown } {
  let status = 0
  let body = ''
  const req = { method: 'GET' } as IncomingMessage
  const res = {
    writeHead: (code: number) => { status = code },
    end: (data: string) => { body = data },
  } as unknown as ServerResponse
  handleKyberRequest(req, res, new URL(href, 'http://127.0.0.1:4747'), bridge)
  return { status, body: body === '' ? undefined : JSON.parse(body) }
}
