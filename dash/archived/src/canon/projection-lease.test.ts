// Shared projection cadence and lease (F1): every automatic path stamps one
// floor and wins one lease, so maintenance can neither bypass the receiver's
// spacing nor project over a live foreign pass. These tests pin the seam the
// two automatic paths share — parsing, staleness, floor arithmetic, atomic
// claiming across two handles on one file, stale/crash reclaim, and release —
// with deterministic clocks throughout: no wall-clock dependence, no sleeps.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, describe, expect, it, vi } from 'vitest'

import { CanonStore } from './store.js'
import {
  PROJECTION_LAST_START_KEY,
  PROJECTION_LEASE_KEY,
  SHARED_PROJECTION_MIN_INTERVAL_MS,
  heartbeatProjectionLease,
  isProjectionLeaseStale,
  parseProjectionLease,
  readSharedProjectionStart,
  releaseProjectionLease,
  sharedProjectionDue,
  startLeaseHeartbeat,
  tryClaimProjectionLease,
  type ClaimedProjectionLease,
} from './projection-lease.js'

const MINUTE_MS = 60_000
const T0 = Date.parse('2026-10-20T00:00:00.000Z')

const stores: CanonStore[] = []
const tempDirs: string[] = []

function openMemoryStore(): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  return store
}

function openFileStore(): { store: CanonStore; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), 'projection-lease-'))
  tempDirs.push(dir)
  const store = new CanonStore(join(dir, 'canon.db'))
  stores.push(store)
  return { store, dir }
}

afterEach(() => {
  for (const store of stores.splice(0)) store.close()
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

function leaseJson(overrides: Record<string, unknown> = {}): string {
  return JSON.stringify({ owner: 'test', pid: process.pid, token: 'token-1', sinceMs: T0, ...overrides })
}

describe('parseProjectionLease', () => {
  it('reads a well-formed lease record', () => {
    expect(parseProjectionLease(leaseJson())).toEqual({ owner: 'test', pid: process.pid, token: 'token-1', sinceMs: T0 })
  })

  it.each([
    ['not json'],
    ['42'],
    ['null'],
    ['[]'],
    ['{}'],
    [leaseJson({ owner: '' })],
    [leaseJson({ owner: 42 })],
    [leaseJson({ pid: 'self' })],
    [leaseJson({ pid: -1 })],
    [leaseJson({ pid: 1.5 })],
    [leaseJson({ token: '' })],
    [leaseJson({ sinceMs: 'recent' })],
  ])('rejects malformed lease %j as absent', (value) => {
    // A corrupt lease must never brick automatic projection: it reads as
    // absent, so the next claim overwrites it instead of waiting it out.
    expect(parseProjectionLease(value)).toBeUndefined()
  })

  it('rejects a non-string value without parsing', () => {
    expect(parseProjectionLease(undefined)).toBeUndefined()
    expect(parseProjectionLease({ owner: 'test' })).toBeUndefined()
  })
})

describe('isProjectionLeaseStale', () => {
  const fresh = { owner: 'holder', pid: 424242, token: 't', sinceMs: T0 }

  it('treats an absent lease as free', () => {
    expect(isProjectionLeaseStale(undefined, T0, 90_000, () => true)).toBe(true)
  })

  it('holds a fresh lease with a live holder', () => {
    expect(isProjectionLeaseStale(fresh, T0 + 1_000, 90_000, () => true)).toBe(false)
  })

  it('frees a lease older than the staleness gate even with a live holder', () => {
    expect(isProjectionLeaseStale(fresh, T0 + 90_001, 90_000, () => true)).toBe(true)
  })

  it('frees a fresh lease whose holder is dead on the first poll', () => {
    expect(isProjectionLeaseStale(fresh, T0 + 1_000, 90_000, () => false)).toBe(true)
  })

  it('holds a fresh lease when the clock went backwards and the holder is alive', () => {
    expect(isProjectionLeaseStale(fresh, T0 - 1_000, 90_000, () => true)).toBe(false)
  })

  it('refuses to decide on an unreadable clock', () => {
    expect(isProjectionLeaseStale(fresh, Number.NaN, 90_000, () => false)).toBe(false)
  })
})

describe('sharedProjectionDue', () => {
  it('is due when no pass ever stamped the floor', () => {
    expect(sharedProjectionDue(openMemoryStore(), T0, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(true)
  })

  it('is not due inside the shared spacing after a stamped pass', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LAST_START_KEY, String(T0))
    expect(sharedProjectionDue(store, T0 + 9 * MINUTE_MS, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(false)
  })

  it('is due exactly at the shared spacing boundary', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LAST_START_KEY, String(T0))
    expect(sharedProjectionDue(store, T0 + SHARED_PROJECTION_MIN_INTERVAL_MS, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(true)
  })

  it('is due when the stamp does not parse', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LAST_START_KEY, 'not-a-time')
    expect(sharedProjectionDue(store, T0, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(true)
  })

  it('is not due when the clock went backwards past the stamp', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LAST_START_KEY, String(T0))
    expect(sharedProjectionDue(store, T0 - 1, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(false)
  })

  it('is not due on an unreadable clock', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LAST_START_KEY, String(T0))
    expect(sharedProjectionDue(store, Number.NaN, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(false)
  })
})

describe('readSharedProjectionStart', () => {
  it('reads absent as null and a stamp as its instant', () => {
    const store = openMemoryStore()
    expect(readSharedProjectionStart(store)).toBeNull()
    store.setMetadata(PROJECTION_LAST_START_KEY, String(T0))
    expect(readSharedProjectionStart(store)).toBe(T0)
  })
})

describe('tryClaimProjectionLease', () => {
  it('claims a free lease and stamps the shared floor at the claim instant', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 })
    expect(claimed).not.toBeNull()
    expect(claimed?.lease).toMatchObject({ owner: 'maintenance', pid: process.pid, sinceMs: T0 })
    expect(claimed?.lease.token).toMatch(/^[0-9a-f]{32}$/)
    expect(readSharedProjectionStart(store)).toBe(T0)
    // The claim itself closes the floor: an immediate second check is not due.
    expect(sharedProjectionDue(store, T0, SHARED_PROJECTION_MIN_INTERVAL_MS)).toBe(false)
  })

  it('refuses a second claim while a live holder owns the lease', () => {
    const store = openMemoryStore()
    const first = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 })
    expect(first).not.toBeNull()
    expect(tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 + 1_000 })).toBeNull()
  })

  it('refuses to claim on an unreadable clock', () => {
    expect(tryClaimProjectionLease(openMemoryStore(), { owner: 'maintenance', nowMs: Number.NaN })).toBeNull()
  })

  it('names an empty owner unknown rather than stamping a blank claimant', () => {
    const claimed = tryClaimProjectionLease(openMemoryStore(), { owner: '', nowMs: T0 })
    expect(claimed?.lease.owner).toBe('unknown')
  })

  it('reclaims a lease older than the staleness gate', () => {
    const store = openMemoryStore()
    const old = tryClaimProjectionLease(store, { owner: 'crashed', nowMs: T0 })
    expect(old).not.toBeNull()
    const next = tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 + 90_001 })
    expect(next).not.toBeNull()
    expect(next?.lease.owner).toBe('maintenance')
  })

  it('reclaims a fresh lease whose holder pid is dead', () => {
    const store = openMemoryStore()
    // A holder no pid probe can find alive: the 2^31 range never names a
    // live process, so the dead-pid fast path fires deterministically.
    store.setMetadata(PROJECTION_LEASE_KEY, leaseJson({ owner: 'sigkilled', pid: 2_147_483_647, sinceMs: T0 }))
    const claimed = tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 + 1_000 })
    expect(claimed?.lease.owner).toBe('maintenance')
  })

  it('holds a fresh lease whose holder pid is alive', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LEASE_KEY, leaseJson({ sinceMs: T0 }))
    expect(tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 + 1_000 })).toBeNull()
  })

  it('claims over a corrupt lease instead of waiting it out', () => {
    const store = openMemoryStore()
    store.setMetadata(PROJECTION_LEASE_KEY, 'corrupt-body')
    const claimed = tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 })
    expect(claimed?.lease.owner).toBe('maintenance')
  })

  it('serializes two handles on one file: exactly one wins', () => {
    const { store: first, dir } = openFileStore()
    const second = new CanonStore(join(dir, 'canon.db'))
    stores.push(second)
    const a = tryClaimProjectionLease(first, { owner: 'a', nowMs: T0 })
    const b = tryClaimProjectionLease(second, { owner: 'b', nowMs: T0 })
    // One winner, one loser — never two owners.
    expect([a === null, b === null].filter(Boolean)).toHaveLength(1)
    const winner = a ?? b
    expect(winner?.lease.owner).toMatch(/^[ab]$/)
  })

  it('throws the floor-write error when the compensating release also fails', () => {
    const store = openMemoryStore()
    const floorFailure = new Error('floor write failed')
    const releaseFailure = new Error('compensating release failed')
    // A store that breaks after the lease CAS: the floor write fails, and
    // the compensating release hits the same broken store. The caller must
    // see the floor failure, never the cleanup one.
    let compareAndSets = 0
    const broken: CanonStore = new Proxy(store, {
      get(target, property, receiver) {
        if (property === 'setMetadata') {
          return () => {
            throw floorFailure
          }
        }
        if (property === 'compareAndSetMetadata') {
          return (...args: [string, string | undefined, string | null]) => {
            compareAndSets += 1
            if (compareAndSets > 1) throw releaseFailure
            return (target.compareAndSetMetadata as (...call: [string, string | undefined, string | null]) => boolean)(...args)
          }
        }
        const value = Reflect.get(target, property, receiver)
        return typeof value === 'function' ? (value as (...call: never[]) => unknown).bind(target) : value
      },
    })
    expect(() => tryClaimProjectionLease(broken, { owner: 'maintenance', nowMs: T0 })).toThrow(floorFailure)
  })
})

describe('heartbeatProjectionLease', () => {
  it('advances the claim while the lease still names us', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
    const next = heartbeatProjectionLease(store, claimed, T0 + 10_000)
    expect(next).not.toBeNull()
    expect(next?.lease.sinceMs).toBe(T0 + 10_000)
    expect(next?.raw).not.toBe(claimed.raw)
  })

  it('loses to a successor instead of stamping over it', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
    // A successor that stole the lease after our beat (crashed heartbeats,
    // aged out): our next beat must fail, not overwrite them.
    store.setMetadata(PROJECTION_LEASE_KEY, leaseJson({ owner: 'successor', sinceMs: T0 + 200_000 }))
    expect(heartbeatProjectionLease(store, claimed, T0 + 10_000)).toBeNull()
  })
})

describe('startLeaseHeartbeat', () => {
  it('reports a throwing beat through onError instead of dying quietly', async () => {
    vi.useFakeTimers()
    try {
      const store = openMemoryStore()
      const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
      const beatFailure = new Error('heartbeat store failed')
      const broken: CanonStore = new Proxy(store, {
        get(target, property, receiver) {
          if (property === 'compareAndSetMetadata') {
            return () => {
              throw beatFailure
            }
          }
          const value = Reflect.get(target, property, receiver)
          return typeof value === 'function' ? (value as (...call: never[]) => unknown).bind(target) : value
        },
      })
      const seen: unknown[] = []
      const heartbeat = startLeaseHeartbeat(broken, claimed, 10, (err) => {
        seen.push(err)
      })
      try {
        await vi.advanceTimersByTimeAsync(50)
        // The failed beat surfaces instead of vanishing into a silent
        // clearInterval, and the stopped timer beats no more.
        expect(seen).toEqual([beatFailure])
        await vi.advanceTimersByTimeAsync(1_000)
        expect(seen).toEqual([beatFailure])
      } finally {
        heartbeat.stop()
      }
    } finally {
      vi.useRealTimers()
    }
  })
})

describe('releaseProjectionLease', () => {
  it('releases our own claim so the next claimant wins', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
    expect(releaseProjectionLease(store, claimed)).toBe(true)
    expect(tryClaimProjectionLease(store, { owner: 'maintenance', nowMs: T0 + 1_000 })).not.toBeNull()
  })

  it('reads an already-gone lease as released', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
    store.compareAndSetMetadata(PROJECTION_LEASE_KEY, claimed.raw, null)
    expect(releaseProjectionLease(store, claimed)).toBe(true)
  })

  it('never releases another owner from underneath them', () => {
    const store = openMemoryStore()
    const claimed = tryClaimProjectionLease(store, { owner: 'receiver', nowMs: T0 }) as ClaimedProjectionLease
    const forged: ClaimedProjectionLease = {
      lease: { owner: 'impostor', pid: process.pid, token: 'other', sinceMs: T0 },
      raw: leaseJson({ owner: 'impostor', token: 'other' }),
    }
    expect(releaseProjectionLease(store, forged)).toBe(false)
    // The true owner's claim is untouched.
    expect(store.getMetadata(PROJECTION_LEASE_KEY)).toBe(claimed.raw)
  })
})
