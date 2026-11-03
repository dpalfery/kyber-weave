// Shared projection cadence in the scheduler (F1): the receiver's in-memory
// floor never constrained the maintenance tick in another process, and the
// two could project the same corpus concurrently. These tests pin the other
// half of the shared seam — the scheduler delaying past a foreign pass's
// stamped start, deferring to a live foreign leaseholder, reclaiming a stale
// lease, draining past the floor but not past a live lease — with fake
// timers and hand-gated projectors throughout, so no pass timing depends on
// a sleep.

import { mkdtempSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'

import {
  CanonicalProjectionScheduler,
  DEFAULT_PROJECTION_MIN_INTERVAL_MS,
  SCHEDULER_DEFERRED_RETRY_MS,
} from './projection.js'
import type { BuildSessionsReport } from './sessions.js'
import { PROJECTION_LAST_START_KEY, PROJECTION_LEASE_KEY } from './projection-lease.js'
import { CanonStore } from './store.js'

const NO_OP_REPORT: BuildSessionsReport = { built: 0, skipped: 0, derivationDegraded: 0, pruned: 0, rollups: 0, findings: 0 }

type Deferred = { resolve: () => void; promise: Promise<void> }

function deferred(): Deferred {
  let resolve!: () => void
  const promise = new Promise<void>((settle) => {
    resolve = settle
  })
  return { resolve, promise }
}

/**
 * A projector the test gates by hand: each pass records its start, then
 * waits for the test to open that pass's gate — the same arrangement
 * `projection.test.ts` uses to make pass counts assertable without sleeps.
 */
function gatedProjector(): {
  project: () => Promise<BuildSessionsReport>
  readonly passes: number
  started: (pass: number) => Promise<void>
  open: (pass: number) => void
} {
  const gates = new Map<number, Deferred>()
  const starts = new Map<number, Deferred>()
  let passes = 0
  const slot = (map: Map<number, Deferred>, pass: number): Deferred => {
    const existing = map.get(pass)
    if (existing !== undefined) return existing
    const created = deferred()
    map.set(pass, created)
    return created
  }
  return {
    async project(): Promise<BuildSessionsReport> {
      passes += 1
      slot(starts, passes).resolve()
      await slot(gates, passes).promise
      return { ...NO_OP_REPORT }
    },
    get passes() {
      return passes
    },
    started: (pass) => slot(starts, pass).promise,
    open: (pass) => slot(gates, pass).resolve(),
  }
}

const stores: CanonStore[] = []
const tempDirs: string[] = []
const schedulers: CanonicalProjectionScheduler[] = []

function openMemoryStore(): CanonStore {
  const store = new CanonStore(':memory:')
  stores.push(store)
  return store
}

function openFileStore(): { store: CanonStore; dir: string } {
  const dir = mkdtempSync(join(tmpdir(), 'projection-cadence-'))
  tempDirs.push(dir)
  const store = new CanonStore(join(dir, 'canon.db'))
  stores.push(store)
  return { store, dir }
}

function trackScheduler(scheduler: CanonicalProjectionScheduler): CanonicalProjectionScheduler {
  schedulers.push(scheduler)
  return scheduler
}

beforeEach(() => {
  vi.useFakeTimers()
})

afterEach(async () => {
  vi.useRealTimers()
  for (const scheduler of schedulers.splice(0)) await scheduler.close()
  for (const store of stores.splice(0)) store.close()
  for (const dir of tempDirs.splice(0)) rmSync(dir, { recursive: true, force: true })
})

describe('CanonicalProjectionScheduler shared floor', () => {
  it('delays the next pass past a foreign automatic pass start', async () => {
    const store = openMemoryStore()
    const gated = gatedProjector()
    const scheduler = trackScheduler(
      new CanonicalProjectionScheduler({ store, project: gated.project, idleMs: 1_000, maxWaitMs: 60_000, minIntervalMs: 10_000 }),
    )
    // A foreign automatic pass (maintenance on its tick) just stamped its
    // start: this scheduler's own spacing applies from that instant too.
    store.setMetadata(PROJECTION_LAST_START_KEY, String(Date.now()))

    const pending = scheduler.request()
    await vi.advanceTimersByTimeAsync(1_000)
    // The idle window expired, but the shared floor has not.
    expect(gated.passes).toBe(0)
    await vi.advanceTimersByTimeAsync(8_999)
    expect(gated.passes).toBe(0)
    await vi.advanceTimersByTimeAsync(1)
    await gated.started(1)
    expect(gated.passes).toBe(1)

    gated.open(1)
    await pending
    expect(gated.passes).toBe(1)
  })

  it('keeps the receiver default spacing at ten minutes', () => {
    expect(DEFAULT_PROJECTION_MIN_INTERVAL_MS).toBe(600_000)
  })

  it('still schedules and reports when the shared floor read fails', async () => {
    // A store that cannot serve a metadata read must not break ingestion:
    // `request()` is fire-and-forget from the writer, so the floor probe
    // fails open to the local schedule and the pass itself surfaces the
    // store failure through `onError` like any other pass failure.
    const store = openMemoryStore()
    const gated = gatedProjector()
    const reported: unknown[] = []
    const scheduler = trackScheduler(
      new CanonicalProjectionScheduler({
        store,
        project: gated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        onError: (error) => {
          reported.push(error)
        },
      }),
    )
    vi.spyOn(store, 'getMetadata').mockImplementation(() => {
      throw new Error('store unreadable (fixture)')
    })

    const pending = scheduler.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await pending
    expect(gated.passes).toBe(0)
    expect(reported).toEqual([expect.objectContaining({ message: 'store unreadable (fixture)' })])
  })
})

describe('CanonicalProjectionScheduler shared lease', () => {
  it('defers to a live foreign leaseholder and runs after its release, never overlapping', async () => {
    const { store: holderStore } = openFileStore()
    const waiterStore = new CanonStore(join(tempDirs[0] as string, 'canon.db'))
    stores.push(waiterStore)
    const holderGated = gatedProjector()
    const waiterGated = gatedProjector()
    const holder = trackScheduler(
      new CanonicalProjectionScheduler({
        store: holderStore,
        project: holderGated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'holder',
      }),
    )
    const waiter = trackScheduler(
      new CanonicalProjectionScheduler({
        store: waiterStore,
        project: waiterGated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'waiter',
      }),
    )

    holder.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await holderGated.started(1) // holder's pass is in flight and holds the lease

    const waiting = waiter.request()
    await vi.advanceTimersByTimeAsync(1_000)
    // The waiter's idle window expired, but the lease is held: no second
    // pass starts over the live one.
    expect(holderGated.passes).toBe(1)
    expect(waiterGated.passes).toBe(0)

    holderGated.open(1) // the holder finishes and releases
    await vi.advanceTimersByTimeAsync(1_000)
    await waiterGated.started(1)
    expect(waiterGated.passes).toBe(1)

    waiterGated.open(1)
    await waiting
    expect(holderGated.passes).toBe(1)
    expect(waiterGated.passes).toBe(1)
  })

  it('reclaims a stale foreign lease instead of waiting it out', async () => {
    const store = openMemoryStore()
    const gated = gatedProjector()
    const scheduler = trackScheduler(
      new CanonicalProjectionScheduler({ store, project: gated.project, idleMs: 1_000, maxWaitMs: 60_000, minIntervalMs: 0 }),
    )
    // A crashed holder's lease: fresh-looking pid range, ancient stamp.
    store.setMetadata(
      PROJECTION_LEASE_KEY,
      JSON.stringify({ owner: 'crashed', pid: 2_147_483_647, token: 'dead', sinceMs: Date.now() - 3_600_000 }),
    )

    const pending = scheduler.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await gated.started(1)
    expect(gated.passes).toBe(1)

    gated.open(1)
    await pending
    expect(gated.passes).toBe(1)
  })

  it('drains past the shared floor without waiting for it', async () => {
    const store = openMemoryStore()
    const gated = gatedProjector()
    const scheduler = trackScheduler(
      new CanonicalProjectionScheduler({ store, project: gated.project, idleMs: 60_000, maxWaitMs: 60_000, minIntervalMs: 60_000 }),
    )
    // A foreign pass just started: the timer path would wait out the floor,
    // but drain is an explicit "run now" and bypasses it.
    store.setMetadata(PROJECTION_LAST_START_KEY, String(Date.now()))
    const pending = scheduler.request()
    expect(gated.passes).toBe(0)

    const drained = scheduler.drain()
    await gated.started(1)
    expect(gated.passes).toBe(1)

    gated.open(1)
    await drained
    await pending
    expect(gated.passes).toBe(1)
  })

  it('polls a live foreign lease from drain and runs once it releases', async () => {
    const { store: holderStore } = openFileStore()
    const waiterStore = new CanonStore(join(tempDirs[0] as string, 'canon.db'))
    stores.push(waiterStore)
    const holderGated = gatedProjector()
    const waiterGated = gatedProjector()
    const holder = trackScheduler(
      new CanonicalProjectionScheduler({
        store: holderStore,
        project: holderGated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'holder',
      }),
    )
    const waiter = trackScheduler(
      new CanonicalProjectionScheduler({
        store: waiterStore,
        project: waiterGated.project,
        idleMs: 60_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'waiter',
      }),
    )

    holder.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await holderGated.started(1)

    waiter.request()
    const drained = waiter.drain()
    // Drain bypasses the schedule and immediately attempts the pass, which
    // defers on the live foreign lease instead of overlapping it.
    await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    expect(holderGated.passes).toBe(1)
    expect(waiterGated.passes).toBe(0)

    holderGated.open(1) // the holder finishes and releases
    await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    await waiterGated.started(1)
    expect(waiterGated.passes).toBe(1)

    waiterGated.open(1)
    await drained
    expect(waiterGated.passes).toBe(1)
  })
})

describe('CanonicalProjectionScheduler shutdown deadline (F1)', () => {
  it('drain() gives up waiting for a heartbeat-live stuck holder, retains work, and settles', async () => {
    const { store: holderStore } = openFileStore()
    const waiterStore = new CanonStore(join(tempDirs[0] as string, 'canon.db'))
    stores.push(waiterStore)
    const holderGated = gatedProjector()
    const waiterGated = gatedProjector()
    const reported: unknown[] = []
    const holder = trackScheduler(
      new CanonicalProjectionScheduler({
        store: holderStore,
        project: holderGated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'holder',
      }),
    )
    // Past the 90 s stale gate: without the holder's 10 s heartbeat the
    // lease would reclaim, so surviving past it proves the holder stayed
    // live the whole wait and the deadline — not the staleness gate — ended it.
    const waiter = trackScheduler(
      new CanonicalProjectionScheduler({
        store: waiterStore,
        project: waiterGated.project,
        idleMs: 60_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'waiter',
        shutdownDeferredTimeoutMs: 120_000,
        onError: (error) => {
          reported.push(error)
        },
      }),
    )

    holder.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await holderGated.started(1) // holder's pass wedges here; its heartbeat keeps the lease fresh

    const waiting = waiter.request()
    let waitingSettled = false
    void waiting.then(() => {
      waitingSettled = true
    })
    const drained = waiter.drain()
    let drainedResolved = false
    void drained.then(() => {
      drainedResolved = true
    })
    // Poll past the stale gate while the holder heartbeats: every retry
    // still defers, so no waiter pass ever starts.
    for (let elapsed = 0; elapsed < 120_000; elapsed += SCHEDULER_DEFERRED_RETRY_MS) {
      await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    }
    await drained
    expect(drainedResolved).toBe(true)
    expect(waiterGated.passes).toBe(0)
    expect(holderGated.passes).toBe(1)
    // Fail loudly: the give-up is reported and the request settles instead
    // of hanging on owed work that never ran.
    expect(reported).toHaveLength(1)
    expect(String((reported[0] as Error)?.message ?? reported[0])).toMatch(/gave up waiting for a live foreign leaseholder/)
    await waiting
    expect(waitingSettled).toBe(true)

    // Retained work survives the give-up: once the stuck holder finishes,
    // the next drain runs it.
    holderGated.open(1)
    await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    const redrained = waiter.drain()
    await waiterGated.started(1)
    waiterGated.open(1)
    await redrained
    expect(waiterGated.passes).toBe(1)
  })

  it('close() gives up waiting for a heartbeat-live stuck holder and still seals', async () => {
    const { store: holderStore } = openFileStore()
    const waiterStore = new CanonStore(join(tempDirs[0] as string, 'canon.db'))
    stores.push(waiterStore)
    const holderGated = gatedProjector()
    const waiterGated = gatedProjector()
    const reported: unknown[] = []
    const holder = trackScheduler(
      new CanonicalProjectionScheduler({
        store: holderStore,
        project: holderGated.project,
        idleMs: 1_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'holder',
      }),
    )
    const waiter = trackScheduler(
      new CanonicalProjectionScheduler({
        store: waiterStore,
        project: waiterGated.project,
        idleMs: 60_000,
        maxWaitMs: 60_000,
        minIntervalMs: 0,
        leaseOwner: 'waiter',
        shutdownDeferredTimeoutMs: 5_000,
        onError: (error) => {
          reported.push(error)
        },
      }),
    )

    holder.request()
    await vi.advanceTimersByTimeAsync(1_000)
    await holderGated.started(1) // wedged pass; heartbeat keeps the lease live

    waiter.request()
    const closing = waiter.close()
    let closedResolved = false
    void closing.then(() => {
      closedResolved = true
    })
    for (let elapsed = 0; elapsed < 5_000; elapsed += SCHEDULER_DEFERRED_RETRY_MS) {
      await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    }
    await closing
    expect(closedResolved).toBe(true)
    expect(waiterGated.passes).toBe(0)
    expect(reported).toHaveLength(1)
    expect(String((reported[0] as Error)?.message ?? reported[0])).toMatch(/gave up waiting for a live foreign leaseholder/)

    // Sealed: no later request starts anything, so the store can be closed.
    // Release the stuck holder for the shared afterEach teardown.
    holderGated.open(1)
    await vi.advanceTimersByTimeAsync(SCHEDULER_DEFERRED_RETRY_MS)
    expect(holderGated.passes).toBe(1)
    await waiter.request()
    await vi.advanceTimersByTimeAsync(120_000)
    expect(waiterGated.passes).toBe(0)
    // Cleanup for the shared afterEach `close()`: the waiter still retains
    // owed work (the give-up never clears it), so the teardown close will
    // run one final pass — pre-open its gate so the teardown cannot hang.
    waiterGated.open(1)
  })
})
