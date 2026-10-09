// Receiver liveness probe for `kyber capture status` and `enable` (D9).
//
// Liveness goes through `GET /healthz` on the OTLP endpoint's origin and
// tells three states apart: the KyberDash receiver (whose healthz answers
// `{ service: 'kyberdash-otlp', ... }`), a foreign listener (anything else
// that answers), and no listener (the fetch fails). When the receiver is
// not running, the remedy names the tray's "host receiver" and "launch at
// login" settings, or `kyberdash kyber otel` — the command never installs a
// LaunchAgent, a systemd unit, or any other process manager.

/** How the probe classifies what answers (or does not) on the endpoint. */
export type ReceiverLiveness = 'kyberdash' | 'foreign' | 'none'

export type ReceiverProbeResult = {
  state: ReceiverLiveness
  detail: string
}

/** Injectable health fetch so tests pin the three states without a socket. */
export type HealthFetcher = (url: string) => Promise<{ status: number; body: string }>

const FETCH_TIMEOUT_MS = 5_000

/**
 * The liveness URL for an OTLP base endpoint such as `http://127.0.0.1:4318`.
 * Health is on the origin (`/healthz`), not under the traces path or query.
 */
export function healthzUrlForEndpoint(endpoint: string): string {
  const url = new URL(endpoint)
  url.pathname = '/healthz'
  url.search = ''
  url.hash = ''
  return url.toString()
}

/**
 * A health payload is a short JSON object. Capping the read keeps a foreign
 * listener from pinning the probe's heap, and cancelling stops the rest of
 * the body from arriving.
 */
export const MAX_HEALTH_BODY_BYTES = 8_192

async function readCappedText(response: Response, maxBytes: number): Promise<string> {
  const body = response.body
  if (body === null) return ''
  const reader = body.getReader()
  const chunks: Uint8Array[] = []
  let total = 0
  try {
    while (total < maxBytes) {
      const { done, value } = await reader.read()
      if (done) break
      if (value === undefined || value.byteLength === 0) continue
      const room = maxBytes - total
      if (value.byteLength <= room) {
        chunks.push(value)
        total += value.byteLength
        continue
      }
      chunks.push(value.subarray(0, room))
      total += room
      break
    }
  } finally {
    await reader.cancel()
  }
  const merged = new Uint8Array(total)
  let offset = 0
  for (const chunk of chunks) {
    merged.set(chunk, offset)
    offset += chunk.byteLength
  }
  return new TextDecoder('utf-8', { fatal: false }).decode(merged)
}

async function fetchWithTimeout(url: string): Promise<{ status: number; body: string }> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), FETCH_TIMEOUT_MS)
  try {
    const response = await fetch(url, { method: 'GET', signal: controller.signal })
    return { status: response.status, body: await readCappedText(response, MAX_HEALTH_BODY_BYTES) }
  } finally {
    clearTimeout(timer)
  }
}

/** The default fetcher: a plain GET with a short timeout. */
export function defaultFetchHealth(url: string): Promise<{ status: number; body: string }> {
  return fetchWithTimeout(url)
}

/** Probe the receiver and classify it as KyberDash, foreign, or absent. */
export async function probeReceiver(
  endpoint: string,
  fetchHealth: HealthFetcher = defaultFetchHealth,
): Promise<ReceiverProbeResult> {
  const url = healthzUrlForEndpoint(endpoint)
  let response: { status: number; body: string }
  try {
    response = await fetchHealth(url)
  } catch (error) {
    const reason = error instanceof Error ? error.message : String(error)
    return { state: 'none', detail: `nothing is listening at ${url} (${reason})` }
  }
  if (response.status !== 200) {
    return {
      state: 'foreign',
      detail: `GET ${url} answered HTTP ${response.status}: not the KyberDash receiver`,
    }
  }
  try {
    const parsed: unknown = JSON.parse(response.body)
    if (
      parsed !== null &&
      typeof parsed === 'object' &&
      (parsed as { service?: unknown }).service === 'kyberdash-otlp'
    ) {
      return { state: 'kyberdash', detail: `KyberDash receiver is listening at ${url}` }
    }
  } catch {
    // Not JSON from this receiver: a foreign listener with its own console.
  }
  return {
    state: 'foreign',
    detail: `something foreign is listening at ${url}: not the KyberDash receiver`,
  }
}

/**
 * What to do when the receiver is down. Names the tray's "host receiver"
 * and "launch at login" settings and the `kyberdash kyber otel` command;
 * installing a process manager is never part of the answer.
 */
export const RECEIVER_DOWN_REMEDY =
  'kyberdash: the OTLP receiver is not running. ' +
  'Enable the tray\'s "host receiver" and "launch at login" settings, ' +
  'or start one with `kyberdash kyber otel`. ' +
  'This command never installs a LaunchAgent, a systemd unit, or any other process manager.'
