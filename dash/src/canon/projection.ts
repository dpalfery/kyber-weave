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

import { setImmediate as scheduleImmediate } from 'node:timers'

import { buildSessions, type BuildSessionsReport } from './sessions.js'
import { DETECTOR_VERSION, SCHEMA_VERSION, type CanonStore } from './store.js'
import { descriptorFor } from '../refresh/registry.js'
import { getPricingGenerationKey } from '../pricing/models.js'

/**
 * Metadata key: the records-write marker the last projection observed.
 *
 * The gate compares the corpus's current marker against this one: a different
 * value means the corpus moved since the last rebuild, so the derived tables
 * are stale and must be rebuilt.
 */
const PROJECTION_RECORDS_MARKER_KEY = 'projection_records_marker'

/**
 * Metadata key: the parser-contract fingerprint the last projection observed.
 *
 * The fingerprint holds, per source checkpoint, the contract version the
 * checkpoint was written under AND the version the registry asks for now. Both
 * halves matter: the registry half catches a contract bump in the code (the
 * stored checkpoints still name the old contract), and the stored half catches
 * a checkpoint that was invalidated (its version was re-stamped under the new
 * contract, and the corpus it covers is about to be re-read).
 */
const PROJECTION_PARSER_CONTRACTS_KEY = 'projection_parser_contracts'

/**
 * Metadata key: the pricing fingerprint the last projection observed.
 *
 * WHY the gate needs it: a session's derived cost is priced from the bundled
 * pricing table, not from the corpus. A binary whose pricing table moved —
 * a repriced model, a new pricing rule — therefore changes the money the
 * dashboard shows while every record, every detector and every parser
 * contract stays exactly as it was. The other gate inputs cannot see that,
 * so a maintenance tick that skipped the projection would serve stale money
 * figures until the next refresh.
 */
const PROJECTION_PRICING_KEY = 'projection_pricing'

/**
 * Metadata key: the store's schema version the last projection observed.
 *
 * WHY the gate needs it: the other four inputs describe the CORPUS and the
 * RULES that read it -- the records that moved, the detectors that ran, the
 * contracts that parsed, the prices that costed. None of them describes the
 * store's SHAPE. A schema bump changes what a row means (which columns
 * exist, what a payload holds) and changes none of the four, so a
 * maintenance tick that ran after a store migrated saw "nothing changed" and
 * skipped the projection -- leaving the derived tables describing a shape
 * the store no longer has, for as long as the corpus itself stayed still.
 *
 * The version is compared as a string, exactly as the store stamps it.
 */
const PROJECTION_SCHEMA_VERSION_KEY = 'projection_schema_version'

/**
 * Metadata key: the store's own schema-version stamp.
 *
 * The store writes it when it is created and rewrites it on every migration
 * step, so it names the shape the rows in that store have.
 */
const STORE_SCHEMA_VERSION_KEY = 'schema_version'

/**
 * The pricing state of this build, as one comparable string.
 *
 * `getPricingGenerationKey` is the pricing module's own signature of
 * everything that can change a rendered cost with no session file changing:
 * the pricing-behaviour version, the freshness of the live LiteLLM cache, and
 * the content digest of the bundled snapshot and fallback tables. A build
 * that ships a repriced model changes the digest; a live repricing changes
 * the timestamp. Either must force one projection.
 */
function pricingFingerprint(): string {
  return getPricingGenerationKey()
}

/**
 * The parser-contract state of the corpus, as one comparable string.
 *
 * Empty when the store holds no checkpoints: a corpus that was never read
 * from a source has no contract to have moved.
 */
function parserContractFingerprint(store: CanonStore): string {
  return store
    .listSourceCheckpoints()
    .map((checkpoint) => {
      const current = descriptorFor(checkpoint.harnessId)?.parserContractVersion
      return `${checkpoint.harnessId}|${checkpoint.sourceKey}|${checkpoint.parserContractVersion}|${current ?? '-'}`
    })
    .sort()
    .join(';')
}

/**
 * Whether the derived tables still describe the corpus.
 *
 * Five inputs, and all five are required. The records-write marker says the
 * corpus itself moved (an ingest, a wipe, a repricing write-back). The
 * detector version says the rules that read the corpus moved. The parser
 * contracts say the reading of the corpus moved. The pricing fingerprint says
 * the rates the corpus is priced with moved. The schema version says the
 * store's SHAPE moved -- a migration reshaped the rows the derived tables
 * were built from. Any one of them can leave the derived tables describing a
 * corpus that no longer exists, or money figures that are no longer the
 * money, and a gate that watched only one of them would serve a stale view.
 */
export function projectionIsCurrent(store: CanonStore): boolean {
  const recordedMarker = store.getMetadata(PROJECTION_RECORDS_MARKER_KEY)
  if (recordedMarker === undefined) return false
  if (recordedMarker !== String(store.recordsWriteMarker())) return false
  // The store's own detector stamp against this build's: a stamp that names
  // an older generation means the derived findings were produced by rules
  // this build has replaced.
  if (store.getDetectorVersion() !== DETECTOR_VERSION) return false
  if (store.getMetadata(PROJECTION_PARSER_CONTRACTS_KEY) !== parserContractFingerprint(store)) return false
  // The pricing table the derived costs came from, against this build's: a
  // repriced model changes the money without changing the corpus.
  if (store.getMetadata(PROJECTION_PRICING_KEY) !== pricingFingerprint()) return false
  // The store's schema version against the one the last projection saw: a
  // store that was migrated since then holds different columns, and the
  // derived tables were built against the old ones. A record that names no
  // version at all -- a projection made by a build that had no such input --
  // is unknown, and unknown reads as stale rather than as current.
  const recordedSchema = store.getMetadata(PROJECTION_SCHEMA_VERSION_KEY)
  if (recordedSchema === undefined) return false
  if (recordedSchema !== store.getMetadata(STORE_SCHEMA_VERSION_KEY)) return false
  return true
}

/**
 * Record what the projection just built saw, so the next pass can tell
 * whether anything moved since.
 *
 * Read AFTER the projection: the projection's own repricing write-back bumps
 * the records marker, and a marker read before the pass would make every
 * later pass look like a change.
 *
 * Recorded by EVERY projection, not only by the maintenance pass: the refresh
 * projects too, and a gate whose state only the maintenance pass refreshed
 * would rebuild the corpus the refresh had just rebuilt.
 */
function recordProjectionState(store: CanonStore): void {
  store.setMetadata(PROJECTION_RECORDS_MARKER_KEY, String(store.recordsWriteMarker()))
  store.setMetadata(PROJECTION_PARSER_CONTRACTS_KEY, parserContractFingerprint(store))
  // The costs the projection just derived came from this build's pricing
  // table, so the store records which table that was — the same way it
  // records the detector generation that produced the findings.
  store.setMetadata(PROJECTION_PRICING_KEY, pricingFingerprint())
  // The shape the projection just built against, so a later migration is
  // visible to the gate: a store reshaped since then needs a new projection
  // even when the corpus itself did not move. A store that carries no
  // version stamp is recorded as this build's, so the state is never absent.
  store.setMetadata(
    PROJECTION_SCHEMA_VERSION_KEY,
    store.getMetadata(STORE_SCHEMA_VERSION_KEY) ?? String(SCHEMA_VERSION),
  )
  // The derived findings were just produced by this build's detectors, so the
  // store's stamp may name them. Stamping here is what makes the detector
  // input converge instead of forcing a rebuild on every pass.
  store.setDetectorVersion()
}

/**
 * Hand the event loop back for one turn.
 *
 * WHY: a full projection over a real corpus is ~26 s of CPU. Run to
 * completion on one thread, it starves every other caller of the loop — an
 * HTTP handler answering `GET /api/kyber/meta` measured 23 s during a
 * projection and 23 ms between them. A yield between units of work bounds a
 * waiter's wait to one unit of work plus its own cost, however long the pass
 * as a whole takes.
 *
 * `setImmediate`, not a resolved promise: a promise continuation is a
 * microtask, and microtasks never reach the check phase where the loop
 * processes I/O. Only a real turn lets a pending handler run.
 *
 * The `node:timers` setImmediate, not the global one: they are the same
 * function in Node, but a test that fakes timers replaces the global with a
 * fake that only fires when the test advances the clock. A projection that
 * waits for a turn nobody will deliver never finishes, and a test that
 * projects would hang rather than fail.
 */
export function yieldToEventLoop(): Promise<void> {
  return new Promise((resolve) => {
    scheduleImmediate(resolve)
  })
}

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
  const report = await buildSessions(store, { yieldToLoop: yieldToEventLoop })
  recordProjectionState(store)
  return report
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
