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

export type ReceiverProber = { probe(url: string): Promise<ReceiverProbe> }

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
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(PROBE_TIMEOUT_MS) })
      if (response.status !== 200) return 'other-service'
      const body = (await response.json().catch(() => null)) as { service?: unknown } | null
      return body?.service === RECEIVER_SERVICE ? 'kyberdash-receiver' : 'other-service'
    } catch (err) {
      const code = ((err as { cause?: { code?: string } }).cause)?.code
      return code === 'ECONNREFUSED' ? 'refused' : 'indeterminate'
    }
  },
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
  private wasEnabled = false
  /** Latched when a stranger holds the port, so we do not retry in a loop until it changes. */
  private portHeldByOther = false

  constructor(options: ReceiverHostOptions) {
    this.store = options.store
    this.program = options.program
    this.programArgs = options.programArgs ?? []
    this.prober = options.prober ?? fetchProber
    this.spawner = options.spawner ?? nodeSpawner
  }

  getStatus(): ReceiverStatus { return this.status }

  /**
   * Probes, updates the status, and starts or stops the receiver as the setting says.
   * Returns the delay owed before the next hosting attempt, or null when none is pending -
   * which makes "not retrying in a loop" observable.
   */
  async poll(): Promise<number | null> {
    const enabled = readSetting(this.store, SETTING_KEYS.receiverHosted) === 'on'
    if (enabled !== this.wasEnabled) {
      // The user may have just stopped whatever held the port.
      this.portHeldByOther = false
      this.failures = 0
      this.wasEnabled = enabled
    }
    if (!enabled) this.stopChild()

    const probe = await this.prober.probe(healthzUrl())

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

    try {
      const child = this.spawner.spawn(this.program, [...this.programArgs, 'otel'])
      this.child = child
      // A child that exits frees us to start a replacement on the next poll.
      void child.exited.then(() => { if (this.child === child) this.child = null })
      this.failures = 0
      return 0
    } catch {
      this.failures += 1
      return restartDelayMs(this.failures)
    }
  }

  async close(): Promise<void> {
    this.stopChild()
  }

  private stopChild(): void {
    const child = this.child
    this.child = null
    child?.kill()
  }
}
