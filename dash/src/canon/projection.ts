// Shared canonical projection (plan: "Canonical projection correction",
// T20 → T21). Static dot-folder refresh and live OTLP are two ingress
// adapters over ONE canonical store, and both must end in the same
// projection of it: `projectCanonicalStore` is the only full projection —
// a thin entry over the authoritative `buildSessions()` — and
// `CanonicalProjectionScheduler` is the serialized, coalescing runner the
// live collector drives so accepted work becomes derived sessions without
// a static refresh.
//
// Nothing here derives sessions itself. A second derivation, or a second
// database, is exactly the divergence this seam exists to prevent: every
// surface consumes what `buildSessions()` cached, whichever adapter fed
// the store.

import { buildSessions, type BuildSessionsReport } from './sessions.js'
import type { CanonStore } from './store.js'

/**
 * The sole full projection over the canonical store: rebuild every derived
 * session, run, execution, harness rollup and finding through the
 * authoritative `buildSessions()` pipeline. Rebuilding is always safe — the
 * derived tables are caches over `records`, replaced wholesale.
 *
 * Cost write-back (issue #186 U9): `records.cost_json` is the derived, re-derivable cost cache.
 * The report path reads it, the next projection reprices it (one transaction per session, and no
 * write when already correct), and a re-ingest overwrite self-heals it.
 */
export async function projectCanonicalStore(store: CanonStore): Promise<BuildSessionsReport> {
  return buildSessions(store)
}

export type CanonicalProjectionSchedulerOptions = {
  store: CanonStore
  /**
   * The projector a pass runs. Defaults to `projectCanonicalStore`; tests
   * override it to gate pass timing. Production code should not.
   */
  project?: (store: CanonStore) => Promise<BuildSessionsReport>
  /** Where a failed pass's error lands. See `report` below for the default. */
  onError?: (error: unknown) => void
  /** A pass may start once the ingest stream has been quiet this long. */
  idleMs?: number
  /** Owed work older than this starts a pass even without an idle gap. */
  maxWaitMs?: number
  /** No pass may start sooner than this after the previous pass started. */
  minIntervalMs?: number
}

export const DEFAULT_PROJECTION_IDLE_MS = 10_000
export const DEFAULT_PROJECTION_MAX_WAIT_MS = 600_000
export const DEFAULT_PROJECTION_MIN_INTERVAL_MS = 600_000

/**
 * Serialized, coalescing, DEBOUNCED runner for the live collector's
 * projections.
 *
 *   * `request()` marks the work dirty and SCHEDULES the next pass; it does
 *     not start one immediately. One request from ingest is one dirty mark,
 *     not one projection: at most one pass ever runs at a time.
 *   * The next pass starts at
 *     `max(min(lastRequestAt + idleMs, oldestUnprojectedAt + maxWaitMs),
 *         lastPassStartAt + minIntervalMs)`: an idle window collapses a
 *     burst into one pass, the staleness cap rescues a trickle that never
 *     goes quiet, and the start floor bounds pass frequency under a
 *     saturated stream to one full pass per `minIntervalMs`.
 *   * Dirtiness arriving while a pass is in flight coalesces into exactly
 *     ONE scheduled trailing pass, however many requests arrived. A burst
 *     of batches costs two passes, not N.
 *   * A failed pass settles its requests, reports through `onError`, and
 *     leaves the work dirty. It rolls nothing back — records already
 *     committed by the writer stay committed — and it does not retry by
 *     itself: the NEXT request runs the retained work again.
 *   * `drain()` and `close()` bypass the schedule and resolve only once
 *     quiescent — no pass in flight and no trailing pass still owed —
 *     which is what lets the collector close the SQLite store after them.
 *
 * A pass is awaited nowhere in the ingest path: the writer's sink calls
 * `request()` fire-and-forget precisely so a slow or failing projection
 * can never block, reject, or drop accepted ingestion.
 */
export class CanonicalProjectionScheduler {
  private readonly store: CanonStore
  private readonly project: (store: CanonStore) => Promise<BuildSessionsReport>
  private readonly report: (error: unknown) => void
  private readonly idleMs: number
  private readonly maxWaitMs: number
  private readonly minIntervalMs: number

  /** Un-projected work exists: a pass is owed on the next opportunity. */
  private dirty = false
  /** Set once `close()` begins; no new pass starts after it. */
  private closed = false
  /** The run currently driving passes, if any. */
  private currentRun: Promise<'quiescent' | 'trailing' | 'failed'> | null = null
  /** The pending scheduled pass start, if any. */
  private timer: ReturnType<typeof setTimeout> | null = null
  /** When the most recent dirty mark arrived. */
  private lastRequestAt: number | null = null
  /** When the oldest un-projected dirty mark arrived. */
  private oldestUnprojectedAt: number | null = null
  /** When the previous pass started; the floor is measured from it. */
  private lastPassStartAt: number | null = null
  /** Promises to settle the next time the scheduler stops owing work. */
  private waiters: Array<() => void> = []

  constructor(options: CanonicalProjectionSchedulerOptions) {
    this.store = options.store
    this.project = options.project ?? projectCanonicalStore
    this.idleMs = options.idleMs ?? DEFAULT_PROJECTION_IDLE_MS
    this.maxWaitMs = options.maxWaitMs ?? DEFAULT_PROJECTION_MAX_WAIT_MS
    this.minIntervalMs = options.minIntervalMs ?? DEFAULT_PROJECTION_MIN_INTERVAL_MS
    const onError = options.onError
    this.report = (error: unknown) => {
      if (onError !== undefined) {
        onError(error)
        return
      }
      // No handler configured: fail loudly on the next turn rather than
      // swallowing a derivation failure — the same bargain `IngestWriter`
      // strikes for persistence failures.
      setImmediate(() => {
        throw error
      })
    }
  }

  /**
   * Mark the work dirty and schedule its projection. Resolves — never
   * rejects — when the scheduler next reaches quiescence, including any
   * trailing pass the requests that arrived mid-pass coalesced into.
   */
  request(): Promise<void> {
    if (this.closed) return Promise.resolve()
    const now = Date.now()
    this.dirty = true
    this.lastRequestAt = now
    if (this.oldestUnprojectedAt === null) this.oldestUnprojectedAt = now
    this.schedule()
    return new Promise<void>((resolve) => {
      this.waiters.push(resolve)
    })
  }

  /**
   * Wait the scheduler out: the pending timer is bypassed, any pass in
   * flight finishes, and owed work with no pass running runs now. A failed
   * pass settles its requests and retains the work — drain does not turn
   * into a retry loop. Resolves only when quiescent or after a failure.
   */
  async drain(): Promise<void> {
    for (;;) {
      this.cancelTimer()
      if (this.currentRun !== null) {
        await this.currentRun
        continue
      }
      if (this.closed || !this.dirty) return
      this.currentRun = this.runUntilQuiescent()
      const outcome = await this.currentRun
      if (outcome === 'failed') return
      // 'trailing' means owed work is still scheduled out: run it now
      // rather than waiting for the debounce/floor it was assigned.
    }
  }

  /**
   * Seal the scheduler and drain it. Work still dirty — including work
   * retained by a failed pass — runs one last time; after this resolves no
   * `request()` starts anything, so the store can be closed safely.
   */
  async close(): Promise<void> {
    this.closed = true
    for (;;) {
      this.cancelTimer()
      if (this.currentRun !== null) {
        await this.currentRun
        continue
      }
      if (!this.dirty) return
      this.currentRun = this.runUntilQuiescent()
      const outcome = await this.currentRun
      if (outcome === 'failed') {
        // Sealed with owed work retained: better than an unbounded failure
        // loop inside close, and records stay committed.
        return
      }
    }
  }

  /**
   * Compute the next pass start from the schedule rule and (re)arm the
   * timer. The pending timer is `unref()`d so it can never hold the
   * process open.
   */
  private schedule(): void {
    this.cancelTimer()
    if (this.closed || !this.dirty || this.currentRun !== null) return
    const lastRequest = this.lastRequestAt
    const oldest = this.oldestUnprojectedAt
    if (lastRequest === null || oldest === null) return
    let at = Math.min(lastRequest + this.idleMs, oldest + this.maxWaitMs)
    if (this.lastPassStartAt !== null) {
      at = Math.max(at, this.lastPassStartAt + this.minIntervalMs)
    }
    const delay = Math.max(0, at - Date.now())
    this.timer = setTimeout(() => {
      this.timer = null
      if (this.closed || !this.dirty || this.currentRun !== null) return
      this.currentRun = this.runUntilQuiescent()
    }, delay)
    this.timer.unref?.()
  }

  private cancelTimer(): void {
    if (this.timer !== null) {
      clearTimeout(this.timer)
      this.timer = null
    }
  }

  /**
   * Run the owed pass(es) for THIS cycle. The dirty flag is consumed up
   * front so dirtiness that arrives while a pass runs is a NEW trailing
   * pass, not a re-run of the one in flight; a failure re-marks it so the
   * work stays owed, then stops — the loop is not a retry loop. When a
   * pass succeeds with dirtiness still owed (mid-pass requests), the
   * trailing pass is SCHEDULED per the rule and this run ends: the timer
   * drives the next cycle, which is what bounds pass frequency under a
   * sustained stream. Waiters settle only when no work is owed or a pass
   * failed (a failed pass leaves work owed, but its requests must still
   * settle — the retry belongs to the NEXT explicit request/drain).
   */
  private async runUntilQuiescent(): Promise<'quiescent' | 'trailing' | 'failed'> {
    try {
      if (!this.dirty) {
        this.settleWaiters()
        return 'quiescent'
      }
      this.dirty = false
      const passStart = Date.now()
      this.lastPassStartAt = passStart
      this.lastRequestAt = null
      this.oldestUnprojectedAt = null
      try {
        await this.project(this.store)
      } catch (error) {
        this.dirty = true
        this.report(error)
        this.settleWaiters()
        return 'failed'
      }
      if (this.dirty) {
        // Mid-pass requests coalesced into one trailing pass, scheduled
        // from their timestamps — never a same-instant rebuild. Clear the
        // run slot first so schedule() arms the timer.
        this.currentRun = null
        this.schedule()
        return 'trailing'
      }
      this.settleWaiters()
      return 'quiescent'
    } finally {
      this.currentRun = null
    }
  }

  private settleWaiters(): void {
    const settled = this.waiters
    this.waiters = []
    for (const resolve of settled) resolve()
  }
}
