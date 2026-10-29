// ReceiverHost: the web server's port of the tray's receiver.rs.
//
// When settings.receiver.hosted is on it keeps one `kyberdash otel` child serving the
// OTLP port; when off it stops the one it started. Pausing jobs deliberately has no
// effect here - the receiver collects live data and is not a scheduled job.

import type { CanonStore } from '../canon/store.js'
import { SETTING_KEYS, readSetting } from '../settings/shared-settings.js'
import { nodeSpawner } from './host.js'
import type { JobChild, JobSpawner } from './host.js'

export const RECEIVER_PORT = 4318
export const RECEIVER_SERVICE = 'kyberdash-otlp'
const FIRST_BACKOFF_MS = 1000
const MAX_BACKOFF_MS = 60_000
const PROBE_TIMEOUT_MS = 1500

/** "Nothing is listening" is an answer, not a failure, so the probe never rejects. */
export type ReceiverProbe = 'kyberdash-receiver' | 'other-service' | 'refused' | 'indeterminate'

export type ReceiverStatus = 'hosted' | 'reachable' | 'port-held-by-other' | 'not-reachable' | 'unknown'

export type ReceiverProber = {
  probe(url: string): Promise<ReceiverProbe>
  /**
   * The same probe with the receiver's own health body attached, when the prober speaks
   * the extended `/healthz` contract. Absent on test fakes that only answer the enum —
   * the host then reports the detail fields as unknown rather than fabricating them.
   */
  probeHealth?(url: string): Promise<{ probe: ReceiverProbe; health: ReceiverHealthDetail | null }>
}

/**
 * The `/healthz` fields the coverage indicator reads. Every field is nullable because an
 * older receiver (or a stranger on the port) answers fewer of them — unknown stays
 * unknown, never a default that would read as healthy.
 */
export type ReceiverHealthDetail = {
  endpoint?: unknown
  listening?: unknown
  paused?: unknown
  checkedAt?: unknown
  lastAcceptedAt?: unknown
  failures5m?: unknown
  lastError?: unknown
  lastCode?: unknown
  lastErrorAt?: unknown
}

/**
 * Fresh receiver state for `GET /api/kyber/coverage`, preserving every existing coverage
 * property (the receiver object is additive). `healthy` is the one engine-computed
 * boolean behind the approved rule: green only when the receiver is enabled, answering as
 * ours or reachable, explicitly not paused, and with zero failures in the rolling
 * five-minute window. `not-reachable`, `port-held-by-other`, and `unknown` are never
 * green, and neither is any state whose paused flag or error window is unobservable —
 * "no failures seen" is not "no failures" when nothing reported the window.
 */
export type ReceiverState = {
  /** The `settings.receiver.hosted` switch, as the poll read it. */
  enabled: boolean
  /** Where the receiver listens, from its own `/healthz`, or null when unobserved. */
  endpoint: string | null
  status: ReceiverStatus
  /** True while the receiver socket is bound; null when no probe said. */
  listening: boolean | null
  /** True when this process started the answering receiver; null when indeterminable. */
  hostedByUs: boolean | null
  /** The receiver's own paused flag; null when no health body said. */
  paused: boolean | null
  /** ISO time of this poll; null only before the first poll. */
  checkedAt: string | null
  /** ISO time of the last persisted batch, from the receiver; null when unobserved. */
  lastAcceptedAt: string | null
  /** OTLP/persistence failures in the rolling five minutes; null when unobserved. */
  failures5m: number | null
  lastError: string | null
  lastCode: string | null
  lastErrorAt: string | null
  healthy: boolean
}

/** The approved indicator rule, in one place so every surface shares it. */
export function isReceiverHealthy(state: Omit<ReceiverState, 'healthy'>): boolean {
  if (!state.enabled) return false
  if (state.status !== 'hosted' && state.status !== 'reachable') return false
  if (state.paused !== false) return false
  if (state.failures5m !== 0) return false
  return true
}

export type ReceiverHostOptions = {
  readonly store: CanonStore
  readonly program: string
  /** As in JobHostOptions: the arguments between the program and `otel`, if any. */
  readonly programArgs?: readonly string[]
  readonly prober?: ReceiverProber
  readonly spawner?: JobSpawner
}

export function healthzUrl(): string {
  return `http://127.0.0.1:${RECEIVER_PORT}/healthz`
}

/** Backoff for a receiver that keeps failing to start: 1s, 2s, 4s ... capped at a minute. */
export function restartDelayMs(consecutiveFailures: number): number {
  if (consecutiveFailures <= 0) return 0
  return Math.min(FIRST_BACKOFF_MS * 2 ** Math.min(consecutiveFailures - 1, 16), MAX_BACKOFF_MS)
}

/**
 * A 200 whose body does not name the receiver is another program on the port. Treating it
 * as reachable would report a receiver that collects nothing.
 */
export const fetchProber: ReceiverProber = {
  async probe(url) {
    return (await fetchProber.probeHealth!(url)).probe
  },
  async probeHealth(url) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) })
      if (response.status !== 200) return { probe: 'other-service' as const, health: null }
      const body = (await response.json().catch(() => null)) as (ReceiverHealthDetail & { service?: unknown }) | null
      if (body?.service !== RECEIVER_SERVICE) return { probe: 'other-service' as const, health: null }
      return { probe: 'kyberdash-receiver' as const, health: body }
    } catch (err) {
      const code = ((err as { cause?: { code?: string } }).cause)?.code
      return { probe: (code === 'ECONNREFUSED' ? 'refused' : 'indeterminate') as ReceiverProbe, health: null }
    }
  },
}

function textOrNull(value: unknown): string | null {
  return typeof value === 'string' && value !== '' ? value : null
}

function booleanOrNull(value: unknown): boolean | null {
  return typeof value === 'boolean' ? value : null
}

function countOrNull(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) && value >= 0 ? Math.floor(value) : null
}

function statusFor(probe: ReceiverProbe, hostedByUs: boolean): ReceiverStatus {
  switch (probe) {
    case 'kyberdash-receiver': return hostedByUs ? 'hosted' : 'reachable'
    case 'other-service': return 'port-held-by-other'
    case 'refused': return 'not-reachable'
    case 'indeterminate': return 'unknown'
  }
}

export class ReceiverHost {
  private readonly store: CanonStore
  private readonly program: string
  private readonly programArgs: readonly string[]
  private readonly prober: ReceiverProber
  private readonly spawner: JobSpawner

  private child: JobChild | null = null
  private failures = 0
  private status: ReceiverStatus = 'unknown'
  private enabled = false
  private lastHealth: ReceiverHealthDetail | null = null
  private lastCheckedAt: string | null = null
  private wasEnabled = false
  /** Latched when a stranger holds the port, so we do not retry in a loop until it changes. */
  private portHeldByOther = false
  /**
   * Sealed on `close()`: no later poll probes or spawns, and a probe that
   * resolves after the seal creates nothing.
   */
  private closed = false
  /** The one in-flight poll, if any; concurrent callers share it (F6). */
  private inflight: Promise<number | null> | null = null
  /**
   * Every spawned child still alive. Normally at most one: a refused probe
   * never replaces a starting child, so the only tracked handle is never
   * lost. Entries leave only when that child's `exited` settles — ownership
   * runs until exit, and `close()` kills the whole set.
   */
  private readonly owned = new Set<JobChild>()

  constructor(options: ReceiverHostOptions) {
    this.store = options.store
    this.program = options.program
    this.programArgs = options.programArgs ?? []
    this.prober = options.prober ?? fetchProber
    this.spawner = options.spawner ?? nodeSpawner
  }

  getStatus(): ReceiverStatus { return this.status }

  /**
   * The fresh receiver state the coverage route serves. Every detail field is null until a
   * real `/healthz` body supplies it — the host never fabricates endpoint, paused, or
   * window facts from the probe enum alone.
   */
  getReceiverState(): ReceiverState {
    const health = this.lastHealth
    const withoutHealthy = {
      enabled: this.enabled,
      endpoint: textOrNull(health?.endpoint),
      status: this.status,
      listening: this.status === 'hosted' || this.status === 'reachable'
        ? true
        : this.status === 'not-reachable'
          ? false
          : booleanOrNull(health?.listening),
      // Attributes the answering receiver only: `hosted` (our child answered) proves
      // hosted-by-us and `reachable` (a receiver answered with no child of ours) proves
      // hosted elsewhere. Any other status means nothing answers, so there is no receiver
      // to attribute — null, even when a child we spawned is still starting up. Claiming
      // a silent port as hosted-by-us would contradict `listening: false` one line above.
      hostedByUs: this.status === 'hosted' ? true : this.status === 'reachable' ? false : null,
      paused: booleanOrNull(health?.paused),
      checkedAt: this.lastCheckedAt,
      lastAcceptedAt: textOrNull(health?.lastAcceptedAt),
      failures5m: countOrNull(health?.failures5m),
      lastError: textOrNull(health?.lastError),
      lastCode: textOrNull(health?.lastCode),
      lastErrorAt: textOrNull(health?.lastErrorAt),
    }
    return { ...withoutHealthy, healthy: isReceiverHealthy(withoutHealthy) }
  }

  /**
   * Probes, updates the status, and starts or stops the receiver as the setting says.
   * Returns the delay owed before the next hosting attempt, or null when none is pending -
   * which makes "not retrying in a loop" observable.
   *
   * Single-flight (F6): concurrent callers share one probe-and-decide pass,
   * so two polls racing a refused answer start one child, not two.
   */
  async poll(): Promise<number | null> {
    if (this.closed) return null
    if (this.inflight !== null) return this.inflight
    const running = this.doPoll()
    this.inflight = running
    try {
      return await running
    } finally {
      if (this.inflight === running) this.inflight = null
    }
  }

  private async doPoll(): Promise<number | null> {
    const enabled = readSetting(this.store, SETTING_KEYS.receiverHosted) === 'on'
    if (enabled !== this.wasEnabled) {
      // The user may have just stopped whatever held the port.
      this.portHeldByOther = false
      this.failures = 0
      this.wasEnabled = enabled
    }
    this.enabled = enabled
    if (!enabled) this.stopChild()

    // One fetch when the prober speaks the health contract, so the enum and the detail
    // always describe the same response; fakes that only answer the enum keep working.
    let probe: ReceiverProbe
    let health: ReceiverHealthDetail | null = null
    if (this.prober.probeHealth !== undefined) {
      const answer = await this.prober.probeHealth(healthzUrl())
      probe = answer.probe
      health = answer.health
    } else {
      probe = await this.prober.probe(healthzUrl())
    }
    // A non-receiver answer carries no receiver facts: keep a stranger's body (or a
    // previous receiver's) from posing as current state.
    this.lastHealth = probe === 'kyberdash-receiver' ? health : null
    this.lastCheckedAt = new Date().toISOString()

    if (probe === 'other-service') {
      this.portHeldByOther = true
      this.stopChild()
    } else if (probe === 'refused') {
      this.portHeldByOther = false
    }

    this.status = statusFor(probe, this.child !== null)

    if (!enabled || this.portHeldByOther) return null
    if (probe === 'kyberdash-receiver') {
      this.failures = 0
      return null
    }
    // Nothing decided, so nothing started: spawning against an indeterminate probe is
    // how two receivers end up fighting for one port.
    if (probe !== 'refused') return null

    // Sealed after close, or a previous child still owned (starting or
    // exiting): a refused probe never replaces the only tracked handle, and a
    // probe that resolves after close creates nothing. Startup slower than
    // the poll interval therefore keeps one child, never a pile of them.
    if (this.closed || this.owned.size > 0) return null

    try {
      const child = this.spawner.spawn(this.program, [...this.programArgs, 'otel', '--serve'])
      this.child = child
      this.owned.add(child)
      // Ownership runs until exit: the handle clears only when this child
      // settles, so a still-starting child blocks any replacement.
      void child.exited.then(
        () => {
          this.owned.delete(child)
          if (this.child === child) this.child = null
        },
        () => {
          this.owned.delete(child)
          if (this.child === child) this.child = null
        },
      )
      this.failures = 0
      return 0
    } catch {
      this.failures += 1
      return restartDelayMs(this.failures)
    }
  }

  /**
   * Seal the host and stop every owned child. Kills are sent to the whole
   * owned set (never just the latest handle), and close waits until each
   * child exits — no post-close spawn and no orphaned live children.
   */
  async close(): Promise<void> {
    if (this.closed) return
    this.closed = true
    this.stopChild()
    // Kill the whole owned set, not just the latest handle: a child that
    // lost its handle while exiting must still be stopped.
    for (const child of [...this.owned]) {
      try {
        child.kill()
      } catch {
        // Already gone; its `exited` settlement still releases ownership.
      }
    }
    await Promise.all(
      [...this.owned].map((child) => child.exited.then(
        () => undefined,
        () => undefined,
      )),
    )
  }

  /**
   * Signal the currently tracked child to stop. The handle clears, but the
   * child stays in the owned set until its `exited` settles — ownership runs
   * until exit, and the spawn guard keeps blocking while it does, so a
   * still-starting child is never replaced or orphaned.
   */
  private stopChild(): void {
    const child = this.child
    this.child = null
    if (child === null) return
    try {
      child.kill()
    } catch {
      // A child that is already gone needs no signal; its `exited`
      // settlement still releases ownership.
    }
  }
}
