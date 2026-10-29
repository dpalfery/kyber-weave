// Shared engine-owned projection cadence and lease (F1).
//
// The web `JobHost` ticks every 30 s and the OTLP receiver enforces its own
// 10-minute projection floor in memory, but the two run in different
// processes over separate store handles on the same canon.db. Neither floor
// constrains the other, so a dirty corpus could be fully re-projected every
// 30 s by maintenance while the receiver projected the same corpus on its
// own schedule — two concurrent full rebuilds, each rewriting every derived
// row.
//
// Both automatic paths therefore coordinate through two metadata keys in the
// canonical store itself, so no new file, directory plumbing, or scheduler
// wiring is needed and every claimant — maintenance, the receiver, and any
// future automatic path — reads and writes the same state:
//
//   * `projection_last_start_ms`: when the last automatic pass started. Every
//     automatic path delays its next pass until at least its minimum spacing
//     after that instant, so maintenance cannot bypass the receiver's floor.
//   * `projection_lease`: which claimant owns the current pass. A pass starts
//     only after winning an atomic compare-and-set on this key, so two
//     processes never project at once. A crashed holder reclaims through the
//     recorded pid (same fast path as the refresh lock) or the age gate.
//
// Manual paths (`kyber build`, `dash refresh`, the model-catalog rebuild)
// never consult either key: an explicit request always projects. The drain
// and close paths bypass the floor but still take the lease, so shutdown
// still rebuilds retained work without racing a live foreign pass.

import { randomBytes } from 'node:crypto'

import type { CanonStore } from './store.js'

/**
 * Minimum spacing between automatic projections, shared by every automatic
 * path. The receiver scheduler's in-memory floor already defaults to this;
 * maintenance applies the same span so neither cadence bypasses the other.
 */
export const SHARED_PROJECTION_MIN_INTERVAL_MS = 600_000

/**
 * When a lease with a live holder may be taken over anyway. Longer than any
 * healthy pass by an order of magnitude (a full rebuild is ~26 s of CPU),
 * shorter than the shared floor so a wedged holder cannot stall the next
 * cadence: the pid check reclaims a crashed holder on the first poll, and
 * this gate covers only the holder no pid probe can reach.
 */
export const PROJECTION_LEASE_STALE_MS = 90_000

/** How often a running pass refreshes its lease while it holds it. */
export const PROJECTION_LEASE_HEARTBEAT_MS = 10_000

/** Metadata key holding the serialized lease record, if any. */
export const PROJECTION_LEASE_KEY = 'projection_lease'

/** Metadata key holding the last automatic pass start, as millis since epoch. */
export const PROJECTION_LAST_START_KEY = 'projection_last_start_ms'

/** Who currently owns an automatic projection pass. */
export type ProjectionLease = {
  /** Human-readable claimant, e.g. 'maintenance' or 'receiver'. */
  readonly owner: string
  /** The owning process, for crash reclaim. */
  readonly pid: number
  /** Random per claim, so only the owner can heartbeat or release it. */
  readonly token: string
  /** When the claim was (last) written, as millis since epoch. */
  readonly sinceMs: number
}

/** A won claim: the lease plus the exact stored bytes it was won with. */
export type ClaimedProjectionLease = {
  readonly lease: ProjectionLease
  readonly raw: string
}

export type ClaimProjectionLeaseOptions = {
  readonly owner: string
  readonly nowMs: number
  readonly staleMs?: number
  readonly pidAlive?: (pid: number) => boolean
}

/**
 * Whether a pid names a live process on this host. Our own pid is always
 * alive; EPERM means the pid exists under another user, which is still
 * alive. Mirrors the refresh lock's probe and its caveat: a dead holder
 * whose pid was recycled reads as alive, and only the age gate reclaims it.
 */
export function ownerProcessAlive(pid: number): boolean {
  if (!Number.isInteger(pid) || pid <= 0) return false
  if (pid === process.pid) return true
  try {
    process.kill(pid, 0)
    return true
  } catch (err) {
    return (err as NodeJS.ErrnoException | undefined)?.code === 'EPERM'
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

/**
 * Read a lease record from untrusted metadata. Anything malformed reads as
 * absent — a corrupt lease must never brick automatic projection, and the
 * next successful claim overwrites it with a valid record.
 */
export function parseProjectionLease(value: unknown): ProjectionLease | undefined {
  if (typeof value !== 'string') return undefined
  let parsed: unknown
  try {
    parsed = JSON.parse(value) as unknown
  } catch {
    return undefined
  }
  if (!isRecord(parsed)) return undefined
  const { owner, pid, token, sinceMs } = parsed
  if (typeof owner !== 'string' || owner.length === 0) return undefined
  if (typeof pid !== 'number' || !Number.isSafeInteger(pid) || pid <= 0) return undefined
  if (typeof token !== 'string' || token.length === 0) return undefined
  if (typeof sinceMs !== 'number' || !Number.isFinite(sinceMs)) return undefined
  return { owner, pid, token, sinceMs }
}

function serializeLease(lease: ProjectionLease): string {
  return JSON.stringify({ owner: lease.owner, pid: lease.pid, token: lease.token, sinceMs: lease.sinceMs })
}

function effectiveStaleMs(staleMs: number | undefined): number {
  return typeof staleMs === 'number' && Number.isFinite(staleMs) && staleMs >= 0
    ? staleMs
    : PROJECTION_LEASE_STALE_MS
}

/**
 * Whether the lease is free to claim: absent, malformed, older than the
 * staleness gate, or held by a process this host can see is dead. A live
 * holder is never stale by pid, which is the whole safety argument — only
 * one claimant projects at a time.
 */
export function isProjectionLeaseStale(
  lease: ProjectionLease | undefined,
  nowMs: number,
  staleMs: number,
  pidAlive: (pid: number) => boolean = ownerProcessAlive,
): boolean {
  if (lease === undefined) return true
  if (!Number.isFinite(nowMs)) return false
  if (nowMs - lease.sinceMs > effectiveStaleMs(staleMs)) return true
  return !pidAlive(lease.pid)
}

/**
 * When the last automatic pass started, or null when no pass has ever
 * stamped it or the stamp does not parse. Unknown reads as absent — the
 * next due check treats it as due, matching how the staleness gate treats
 * unknown as stale rather than as current.
 */
export function readSharedProjectionStart(store: CanonStore): number | null {
  const raw = store.getMetadata(PROJECTION_LAST_START_KEY)
  if (raw === undefined) return null
  const parsed = Number(raw)
  return Number.isFinite(parsed) && parsed >= 0 ? parsed : null
}

/**
 * Whether an automatic pass may start now under the shared floor. True when
 * no pass ever stamped the floor, when the stamp is unreadable, or when at
 * least `minIntervalMs` has passed since the last start. A clock that went
 * backwards is never a reason to project early, and an unreadable clock
 * never authorizes a pass.
 */
export function sharedProjectionDue(store: CanonStore, nowMs: number, minIntervalMs: number): boolean {
  if (!Number.isFinite(nowMs)) return false
  const lastStart = readSharedProjectionStart(store)
  if (lastStart === null) return true
  const elapsed = nowMs - lastStart
  if (elapsed < 0) return false
  const interval = Number.isFinite(minIntervalMs) && minIntervalMs > 0 ? minIntervalMs : 0
  return elapsed >= interval
}

/**
 * Try to become the one automatic projector. The claim is a single
 * conditional metadata write, so two processes racing here serialize in
 * SQLite and exactly one wins; the loser reads its loss from the write
 * count rather than from a value it read earlier.
 *
 * A won claim also stamps the shared floor at `nowMs`, so every other
 * automatic path — whatever clock domain its own scheduling uses — measures
 * its spacing from this pass. The stamp and the lease land as two writes;
 * the lease is the serialization and the stamp is advisory, so their order
 * needs no transaction: at worst a crashed claimer leaves a fresh floor
 * with no pass behind it, which only delays — never duplicates — the next
 * pass.
 *
 * Returns null when the clock is unreadable, when a live holder owns the
 * lease, or when the compare-and-set lost its race.
 */
export function tryClaimProjectionLease(
  store: CanonStore,
  options: ClaimProjectionLeaseOptions,
): ClaimedProjectionLease | null {
  const { nowMs } = options
  if (!Number.isFinite(nowMs)) return null
  const staleMs = effectiveStaleMs(options.staleMs)
  const pidAlive = options.pidAlive ?? ownerProcessAlive
  const owner = typeof options.owner === 'string' && options.owner.length > 0 ? options.owner : 'unknown'

  const raw = store.getMetadata(PROJECTION_LEASE_KEY)
  const current = raw === undefined ? undefined : parseProjectionLease(raw)
  if (current !== undefined && !isProjectionLeaseStale(current, nowMs, staleMs, pidAlive)) return null

  const lease: ProjectionLease = { owner, pid: process.pid, token: randomBytes(16).toString('hex'), sinceMs: nowMs }
  const next = serializeLease(lease)
  if (!store.compareAndSetMetadata(PROJECTION_LEASE_KEY, raw, next)) return null
  try {
    store.setMetadata(PROJECTION_LAST_START_KEY, String(nowMs))
  } catch (err) {
    releaseProjectionLease(store, { lease, raw: next })
    throw err
  }
  return { lease, raw: next }
}

/**
 * Refresh our own lease mid-pass. Returns the advanced claim, or null when
 * the lease no longer names us — a stolen or released lease is never
 * re-stamped over its successor. A pass whose heartbeat is lost keeps its
 * work (records stay committed) but must not assume it still owns the
 * cadence; the heartbeat driver stops ticking on null.
 */
export function heartbeatProjectionLease(
  store: CanonStore,
  claimed: ClaimedProjectionLease,
  nowMs: number,
): ClaimedProjectionLease | null {
  if (!Number.isFinite(nowMs)) return null
  const lease: ProjectionLease = { ...claimed.lease, sinceMs: nowMs }
  const next = serializeLease(lease)
  if (!store.compareAndSetMetadata(PROJECTION_LEASE_KEY, claimed.raw, next)) return null
  return { lease, raw: next }
}

/**
 * Keep our lease fresh for the duration of a pass. The timer is unref'd so
 * it can never hold the process open, and a failed beat stops the timer
 * rather than throwing out of a tick — the pass's own `finally` still
 * releases whatever claim remains.
 */
export function startLeaseHeartbeat(
  store: CanonStore,
  claimed: ClaimedProjectionLease,
  intervalMs: number = PROJECTION_LEASE_HEARTBEAT_MS,
): { stop: () => void; current: () => ClaimedProjectionLease } {
  const every = Number.isFinite(intervalMs) && intervalMs > 0 ? intervalMs : PROJECTION_LEASE_HEARTBEAT_MS
  let current = claimed
  const timer = setInterval(() => {
    try {
      const next = heartbeatProjectionLease(store, current, Date.now())
      if (next === null) {
        clearInterval(timer)
        return
      }
      current = next
    } catch {
      clearInterval(timer)
    }
  }, every)
  timer.unref?.()
  return {
    stop: () => {
      clearInterval(timer)
    },
    current: () => current,
  }
}

/**
 * Give up our claim. True when the lease is now free — including when it
 * was already gone — and false when it names another owner, whose lease is
 * never released from underneath them.
 */
export function releaseProjectionLease(store: CanonStore, claimed: ClaimedProjectionLease): boolean {
  const current = store.getMetadata(PROJECTION_LEASE_KEY)
  // Already gone reads as released: release is cleanup, and a lease nobody
  // holds needs no winner to declare it free. A lease naming another owner
  // is never released from underneath them; the compare-and-set below is
  // what makes that atomic against a steal racing this call.
  if (current === undefined) return true
  if (current !== claimed.raw) return false
  return store.compareAndSetMetadata(PROJECTION_LEASE_KEY, claimed.raw, null)
}
