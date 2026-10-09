// Receiver pause port for a database clean (issue #312): probe the loopback
// OTLP health endpoint, verify the port is ours, and pause/resume ingestion
// around the wipe. The receiver sheds load with 503 + Retry-After while
// paused (exporters retry, so nothing is dropped); a lease TTL auto-resumes
// a cleaner that crashes mid-wipe.
//
// Why a port and not a lock: the receiver is a sibling process holding its
// own read-write store handle. The refresh lock cannot reach it — it gates
// only `dash refresh` — and stopping the process would drop the collector a
// user may run standalone. A pause the receiver enforces itself is lossless.

import {
  DEFAULT_OTLP_PORT,
  OTLP_ADMIN_PAUSE_PATH,
  OTLP_ADMIN_RESUME_PATH,
  OTLP_HEALTHZ_PATH,
} from '../otel/receiver.js'

/** The loopback receiver base URL the clean pauses. */
export const RECEIVER_BASE_URL = `http://127.0.0.1:${DEFAULT_OTLP_PORT}`

/** Service identity the health endpoint reports for our receiver. */
const RECEIVER_SERVICE = 'kyberdash-otlp'

/** Pause lease handed to the receiver: a crashed cleaner resumes after 10 minutes. */
export const CLEAN_PAUSE_LEASE_MS = 10 * 60 * 1000

export type ReceiverPauseOutcome = {
  /** True when a receiver was found and paused; false when the port was free or foreign. */
  paused: boolean
}

type HealthBody = {
  service?: unknown
  paused?: unknown
}

/**
 * Pause ingestion when the port holds our receiver. Free port or foreign
 * service: proceed without pausing (the refresh lock still guards refresh).
 * Our receiver that refuses the pause: throw — failing closed, before any
 * row is wiped, so the writer can never race the wipe transaction.
 */
export async function pauseReceiver(
  baseUrl: string = RECEIVER_BASE_URL,
  leaseMs: number = CLEAN_PAUSE_LEASE_MS,
): Promise<ReceiverPauseOutcome> {
  if (!(await isOurReceiver(baseUrl))) return { paused: false }
  let response: Response
  try {
    response = await fetch(`${baseUrl}${OTLP_ADMIN_PAUSE_PATH}`, {
      method: 'POST',
      headers: { 'content-type': 'application/json' },
      body: JSON.stringify({ leaseMs }),
    })
  } catch (err) {
    throw new Error(`cleanDatabase: receiver pause request failed: ${message(err)}`)
  }
  if (!response.ok) {
    throw new Error(`cleanDatabase: receiver pause refused (HTTP ${response.status})`)
  }
  return { paused: true }
}

/** Resume ingestion; a no-op when nothing was paused. Best-effort by design. */
export async function resumeReceiver(baseUrl: string = RECEIVER_BASE_URL): Promise<void> {
  if (!(await isOurReceiver(baseUrl))) return
  try {
    await fetch(`${baseUrl}${OTLP_ADMIN_RESUME_PATH}`, { method: 'POST' })
  } catch {
    // The receiver auto-resumes on lease expiry; a failed resume request
    // must never fail a clean that already succeeded.
  }
}

/** True when the port answers healthz with our service identity. */
async function isOurReceiver(baseUrl: string): Promise<boolean> {
  let response: Response
  try {
    response = await fetch(`${baseUrl}${OTLP_HEALTHZ_PATH}`)
  } catch {
    return false
  }
  if (!response.ok) return false
  let body: HealthBody
  try {
    body = (await response.json()) as HealthBody
  } catch {
    return false
  }
  return body.service === RECEIVER_SERVICE
}

function message(err: unknown): string {
  return err instanceof Error ? err.message : String(err)
}
