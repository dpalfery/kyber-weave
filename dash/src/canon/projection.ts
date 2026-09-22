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
}

/**
 * Serialized, coalescing runner for the live collector's projections.
 *
 *   * `request()` marks the work dirty and runs a pass immediately — no
 *     timer, no debounce. One request from ingest is one dirty mark, not
 *     one projection: at most one pass ever runs at a time.
 *   * Dirtiness arriving while a pass is in flight coalesces into exactly
 *     ONE trailing pass, however many requests arrived. A burst of batches
 *     costs two passes, not N.
 *   * A failed pass settles its requests, reports through `onError`, and
 *     leaves the work dirty. It rolls nothing back — records already
 *     committed by the writer stay committed — and it does not retry by
 *     itself: the NEXT request runs the retained work again.
 *   * `drain()` and `close()` resolve only once quiescent — no pass in
 *     flight and no trailing pass still owed — which is what lets the
 *     collector close the SQLite store after them.
 *
 * A pass is awaited nowhere in the ingest path: the writer's sink calls
 * `request()` fire-and-forget precisely so a slow or failing projection
 * can never block, reject, or drop accepted ingestion.
 */
export class CanonicalProjectionScheduler {
  private readonly store: CanonStore
  private readonly project: (store: CanonStore) => Promise<BuildSessionsReport>
  private readonly report: (error: unknown) => void

  /** Un-projected work exists: a pass is owed on the next opportunity. */
  private dirty = false
  /** Set once `close()` begins; no new pass starts after it. */
  private closed = false
  /** The run currently driving passes to quiescence, if any. */
  private currentRun: Promise<void> | null = null

  constructor(options: CanonicalProjectionSchedulerOptions) {
    this.store = options.store
    this.project = options.project ?? projectCanonicalStore
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
   * Mark the work dirty and project it now. Resolves — never rejects — when
   * the scheduler next reaches quiescence, including any trailing pass the
   * requests that arrived mid-pass coalesced into.
   */
  request(): Promise<void> {
    if (this.closed) return Promise.resolve()
    this.dirty = true
    if (this.currentRun === null) this.currentRun = this.runUntilQuiescent()
    return this.currentRun
  }

  /**
   * Wait the scheduler out: any pass in flight finishes, and dirty work with
   * no pass running starts one. Resolves only at quiescence.
   */
  drain(): Promise<void> {
    if (this.currentRun !== null) return this.currentRun
    if (this.closed || !this.dirty) return Promise.resolve()
    this.currentRun = this.runUntilQuiescent()
    return this.currentRun
  }

  /**
   * Seal the scheduler and drain it. Work still dirty — including work
   * retained by a failed pass — runs one last time; after this resolves no
   * `request()` starts anything, so the store can be closed safely.
   */
  async close(): Promise<void> {
    this.closed = true
    if (this.currentRun === null && this.dirty) {
      this.currentRun = this.runUntilQuiescent()
    }
    if (this.currentRun !== null) await this.currentRun
  }

  /**
   * Drive passes until quiescent. The dirty flag is consumed up front so
   * dirtiness that arrives while a pass runs is a NEW trailing pass, not a
   * re-run of the one in flight; a failure re-marks it so the work stays
   * owed, then stops — the loop is not a retry loop.
   */
  private async runUntilQuiescent(): Promise<void> {
    try {
      while (this.dirty) {
        this.dirty = false
        try {
          await this.project(this.store)
        } catch (error) {
          this.dirty = true
          this.report(error)
          break
        }
      }
    } finally {
      this.currentRun = null
    }
  }
}
