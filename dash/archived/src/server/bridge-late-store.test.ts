import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { createRequire } from 'node:module'
import { mkdtempSync, rmSync, renameSync, unlinkSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import type { IncomingMessage, ServerResponse } from 'node:http'

// `node:fs`'s module namespace is not configurable in ESM, so `vi.spyOn`
// cannot intercept `statSync` directly (see
// https://vitest.dev/guide/mocking/modules#mocking-a-module). `vi.mock` with
// an `importOriginal`-backed passthrough lets case (i) install a throwing
// `statSync` for one probe window without touching any other `node:fs`
// export that `bridge.ts` and `CanonStore` rely on (`existsSync`,
// `readFileSync`, `mkdtempSync`, `rmSync`, `renameSync`, `unlinkSync`, ...).
const { getStatSyncOverride, setStatSyncOverride } = vi.hoisted(() => {
  let override: ((...args: unknown[]) => unknown) | null = null
  return {
    getStatSyncOverride: () => override,
    setStatSyncOverride: (fn: ((...args: unknown[]) => unknown) | null) => {
      override = fn
    },
  }
})

vi.mock('node:fs', async (importOriginal) => {
  const actual = await importOriginal<typeof import('node:fs')>()
  return {
    ...actual,
    statSync: (...args: unknown[]) => {
      const override = getStatSyncOverride()
      if (override) return override(...args)
      return (actual.statSync as (...a: unknown[]) => unknown)(...args)
    },
  }
})

import { KyberBridge } from './bridge.js'
import { CanonStore } from '../canon/store.js'
import { handleKyberRequest } from './routes.js'

const _require = createRequire(import.meta.url)
const { DatabaseSync } = _require('node:sqlite') as {
  DatabaseSync: typeof import('node:sqlite').DatabaseSync
}

/// CanonStore keeps its handle private. These casts verify the database
/// directly for test purposes, reaching past the public surface deliberately
/// rather than by accident (same pattern as dash/src/analysis/review.test.ts:59).
type StoreInternals = {
  db: { prepare(sql: string): { get(): unknown; all(): unknown[] }; exec(sql: string): void }
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
      const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
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
        store.upsertSession({
          sessionId: 'sess-1',
          harness: 'alpha',
          label: 'Test Session',
          isSubagent: false,
          parentSession: null,
          agentName: null,
          repo: null,
          branch: null,
          started: '2026-01-01T00:00:00Z',
          ended: null,
          payload: { id: 'sess-1' },
        })

        const storeInternals = store as unknown as StoreInternals
        storeInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store.close()

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
      const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })

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
        const storeInternals = store as unknown as StoreInternals
        storeInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store.close()

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
      const store1Internals = store1 as unknown as StoreInternals
      store1Internals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      store1.close()

      const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
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
        // Correct replacement: checkpoint and close old file, create new one, rename over
        const tempStorePath = join(tempDir, 'canon-replacement.db')
        const store2 = new CanonStore(tempStorePath)
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
        const store2Internals = store2 as unknown as StoreInternals
        store2Internals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        store2.close()

        renameSync(tempStorePath, canonPath)
        try {
          unlinkSync(canonPath + '-wal')
        } catch {}
        try {
          unlinkSync(canonPath + '-shm')
        } catch {}

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
        const storeInternals = store as unknown as StoreInternals
        storeInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')

        // Create bridge after file exists
        const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
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
          const storeInternals = store as unknown as StoreInternals
          storeInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')

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
        store.close()
      }
    })
  })

  describe('(e) Replaced file (skip on Windows)', () => {
    it.skipIf(process.platform === 'win32')('owned bridge serves new file after canonPath is replaced', () => {
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
        const storeAInternals = storeA as unknown as StoreInternals
        storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.close()

        const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
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
            const storeBInternals = storeB as unknown as StoreInternals
            storeBInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.close()

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
              storeB.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.close()
        } catch {}
      }
    })
  })

  describe('(f) Removed file (skip on Windows)', () => {
    it.skipIf(process.platform === 'win32')('owned bridge returns [] when canonPath and its sidecars are removed', () => {
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
        const storeAInternals = storeA as unknown as StoreInternals
        storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.close()

        const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
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
            const storeBInternals = storeB as unknown as StoreInternals
            storeBInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.close()

            // Bridge returns gamma
            expect(bridge.listHarnessRollups()).toEqual(
              expect.arrayContaining([
                expect.objectContaining({ harness: 'gamma' })
              ])
            )
          } finally {
            try {
              storeB.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.close()
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
        const storeAInternals = storeA as unknown as StoreInternals
        storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
        storeA.close()

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
            const storeBInternals = storeB as unknown as StoreInternals
            storeBInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
            storeB.close()

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
              storeB.close()
            } catch {}
          }
        } finally {
          bridge.close()
        }
      } finally {
        try {
          storeA.close()
        } catch {}
      }
    })
  })

  describe('(h) Injected handle is never swapped', () => {
    it.skipIf(process.platform === 'win32')('bridge built with injected canonDb keeps returning its data after canonPath is replaced', () => {
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
      const storeAInternals = storeA as unknown as StoreInternals
      storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.close()

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
          const storeBInternals = storeB as unknown as StoreInternals
          storeBInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
          storeB.close()

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

  describe('(i) Non-ENOENT stat exception preserves the handle and warns once', () => {
    it('keeps serving the previously-opened data, and warns exactly once, when statSync throws something other than the confirmed-absent signal', () => {
      const canonPath = join(tempDir, 'canon.db')

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
      const storeAInternals = storeA as unknown as StoreInternals
      storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.close()

      const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
      try {
        // Establish a live, healthy handle serving real data before the stat
        // call ever throws anything.
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
        )

        setStatSyncOverride(() => {
          throw Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' })
        })
        try {
          // A non-ENOENT stat exception (EACCES here) must NOT be treated as
          // "confirmed absent": the bridge must keep the healthy handle and
          // keep serving its previously-served data, across repeated probes.
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )

          const statFailureWarnings = warnSpy.mock.calls.filter((call) =>
            call.some((arg) => typeof arg === 'string' && arg.includes(canonPath)) &&
            call.some(
              (arg) =>
                (arg instanceof Error && arg.message.includes('EACCES')) ||
                (typeof arg === 'string' && arg.includes('EACCES'))
            )
          )
          expect(statFailureWarnings).toHaveLength(1)
        } finally {
          setStatSyncOverride(null)
        }

        // Once the transient failure clears, the next probe stats normally
        // again, finds the same file identity, and keeps serving it.
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
        )
      } finally {
        warnSpy.mockRestore()
        bridge.close()
      }
    })
  })

  describe('(j) Default reopenCheckIntervalMs: the omitted option still throttles to 1000ms', () => {
    it('a bridge constructed without reopenCheckIntervalMs still waits ~1000ms before picking up a replacement', () => {
      const canonPath = join(tempDir, 'canon.db')

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
      const storeAInternals = storeA as unknown as StoreInternals
      storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.close()

      // Mock clock starting at t=0.
      let currentTime = 0
      const mockNow = () => currentTime

      // `reopenCheckIntervalMs` is intentionally OMITTED: production
      // (`dash/src/cli/web.ts`) constructs `new KyberBridge()` with no
      // options either, so this must exercise
      // `DEFAULT_REOPEN_CHECK_INTERVAL_MS` (1000ms), not an explicitly
      // passed value. Case (g) proves the throttle boundary for an explicit
      // 1000; this proves omitting the option yields that same default.
      const bridge = new KyberBridge({ canonPath, now: mockNow })
      try {
        // At t=0 (construction counts as the first probe): bridge serves alpha.
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
        )

        // Replace file with store B (beta).
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
          const storeBInternals = storeB as unknown as StoreInternals
          storeBInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
          storeB.close()

          renameSync(tempStorePath, canonPath)
          try {
            unlinkSync(canonPath + '-wal')
          } catch {}
          try {
            unlinkSync(canonPath + '-shm')
          } catch {}

          // At t=999: still within the default 1000ms interval, still alpha.
          currentTime = 999
          const rollups1 = bridge.listHarnessRollups()
          expect(rollups1).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )

          // At t=1000: the default interval has elapsed, now beta.
          currentTime = 1000
          const rollups2 = bridge.listHarnessRollups()
          expect(rollups2).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'beta' })])
          )
        } finally {
          try {
            storeB.close()
          } catch {}
        }
      } finally {
        bridge.close()
      }
    })
  })

  describe('(k) Persistent stat failure re-warns after the rate-limit interval elapses', () => {
    it('warns again once STAT_FAILURE_WARN_INTERVAL_MS has passed since the last warning, while the failure keeps recurring', () => {
      const canonPath = join(tempDir, 'canon.db')

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
      const storeAInternals = storeA as unknown as StoreInternals
      storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.close()

      // Mock clock starting at t=0, same injected-`now` pattern as (g)/(j).
      let currentTime = 0
      const mockNow = () => currentTime

      const bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0, now: mockNow })
      const warnSpy = vi.spyOn(console, 'warn').mockImplementation(() => {})
      try {
        // Establish a live, healthy handle before the stat call ever throws.
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
        )

        setStatSyncOverride(() => {
          throw Object.assign(new Error('EACCES: permission denied'), { code: 'EACCES' })
        })
        try {
          const statFailureWarnings = () =>
            warnSpy.mock.calls.filter((call) =>
              call.some((arg) => typeof arg === 'string' && arg.includes(canonPath)) &&
              call.some(
                (arg) =>
                  (arg instanceof Error && arg.message.includes('EACCES')) ||
                  (typeof arg === 'string' && arg.includes('EACCES'))
              )
            )

          // At t=0: the stat fails for the first time. One warning fires,
          // and the handle keeps serving alpha.
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )
          expect(statFailureWarnings()).toHaveLength(1)

          // At t=30_000: the failure persists, but the rate-limit interval
          // (60_000ms) has not elapsed since the last warning. No new warning.
          currentTime = 30_000
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )
          expect(statFailureWarnings()).toHaveLength(1)

          // At t=60_000: the interval has elapsed and the failure is still
          // recurring. A durable operator-visible signal requires a second
          // warning here — this is the case that is silent on the unfixed
          // "warn once ever" behavior.
          currentTime = 60_000
          expect(bridge.listHarnessRollups()).toEqual(
            expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
          )
          expect(statFailureWarnings()).toHaveLength(2)
        } finally {
          setStatSyncOverride(null)
        }
      } finally {
        warnSpy.mockRestore()
        bridge.close()
      }
    })
  })

  describe('(l) Pragma failure after a successful open does not leak the native handle', () => {
    it('closes the just-opened handle and reports the bridge as absent, rather than leaking a half-open handle it can neither use nor track', () => {
      const canonPath = join(tempDir, 'canon.db')

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
      const storeAInternals = storeA as unknown as StoreInternals
      storeAInternals.db.exec('PRAGMA wal_checkpoint(TRUNCATE)')
      storeA.close()

      // `bridge.ts` reaches `node:sqlite`'s `DatabaseSync` through
      // `createRequire`, a real Node CJS `require` that bypasses Vitest's
      // ESM module graph — `vi.mock('node:sqlite', ...)` cannot intercept
      // it the way `vi.mock('node:fs', ...)` intercepts `statSync` above.
      // `node:sqlite` is a built-in, so every `createRequire(...)('node:sqlite')`
      // call (this file's, and `bridge.ts`'s) resolves to the same singleton
      // module: spying on `DatabaseSync.prototype` here reaches the instance
      // the bridge constructs.
      const closeSpy = vi.spyOn(DatabaseSync.prototype, 'close')
      const execSpy = vi
        .spyOn(DatabaseSync.prototype, 'exec')
        .mockImplementation(function (this: unknown, sql: string) {
          if (typeof sql === 'string' && sql.includes('busy_timeout')) {
            throw new Error('simulated PRAGMA failure after a successful open')
          }
        })

      let bridge: KyberBridge | undefined
      try {
        bridge = new KyberBridge({ canonPath, reopenCheckIntervalMs: 0 })

        // The `new DatabaseSync(...)` open succeeded; only the immediately
        // following pragma call threw. The bridge must not be stuck with
        // that half-open handle — it must report absent, the same as a
        // failed open.
        expect(bridge.listHarnessRollups()).toEqual([])

        // The leak this test guards: the just-opened native handle must be
        // closed rather than discarded still-open. On the unfixed code this
        // spy is never called.
        expect(closeSpy).toHaveBeenCalled()

        // Once the pragma stops failing, the next probe must be able to
        // open a fresh handle rather than being left permanently stuck —
        // proving the failure path left no dangling, unreachable state.
        execSpy.mockRestore()
        expect(bridge.listHarnessRollups()).toEqual(
          expect.arrayContaining([expect.objectContaining({ harness: 'alpha' })])
        )
      } finally {
        execSpy.mockRestore()
        closeSpy.mockRestore()
        bridge?.close()
      }
    })
  })

  describe('(m) Pragma failure after a successful :memory: open does not leak the native handle', () => {
    it('closes the just-opened :memory: handle when the busy_timeout pragma throws, at construction time', () => {
      // A `:memory:` bridge opens its handle exactly once, synchronously,
      // inside the constructor via `openMemoryDb()` — it is never probed or
      // reopened by `reconcile()` afterward (see the `ownsHandle` note: a
      // `:memory:` bridge is not an owned handle). So unlike case (l), which
      // exercises a later `reconcile()` probe, this only needs to observe
      // construction itself; there is no file/CanonStore setup to arrange.
      //
      // Same rationale as case (l) for spying on `DatabaseSync.prototype`
      // rather than `vi.mock('node:sqlite', ...)`: `bridge.ts` reaches
      // `node:sqlite`'s `DatabaseSync` through `createRequire`, a real Node
      // CJS `require` that bypasses Vitest's ESM module graph, and
      // `node:sqlite` being a built-in means every `createRequire(...)('node:sqlite')`
      // call (this file's, and `bridge.ts`'s) resolves to the same singleton
      // module.
      const closeSpy = vi.spyOn(DatabaseSync.prototype, 'close')
      const execSpy = vi
        .spyOn(DatabaseSync.prototype, 'exec')
        .mockImplementation(function (this: unknown, sql: string) {
          if (typeof sql === 'string' && sql.includes('busy_timeout')) {
            throw new Error('simulated PRAGMA failure after a successful :memory: open')
          }
        })

      let bridge: KyberBridge | undefined
      try {
        // The `new DatabaseSync(':memory:', ...)` open succeeds; only the
        // immediately following pragma call throws.
        bridge = new KyberBridge({ canonPath: ':memory:' })

        // The leak this test guards: the just-opened native handle must be
        // closed rather than discarded still-open. On the unfixed code this
        // spy is never called.
        expect(closeSpy).toHaveBeenCalled()

        // The bridge ends up with no handle to serve from — the same
        // "absent" outcome as a failed open.
        expect(bridge.listHarnessRollups()).toEqual([])
      } finally {
        execSpy.mockRestore()
        closeSpy.mockRestore()
        bridge?.close()
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
