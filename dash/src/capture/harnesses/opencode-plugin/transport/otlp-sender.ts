// T5 OTLP/HTTP sender (span-anchored, Q3): plugin hook events become OTLP
// spans posted to the loopback receiver (live OTLP/HTTP to 127.0.0.1:4318,
// Q2). Each hook boundary maps to one instantaneous span anchored on the
// session trace (`session.id` is a retained correlation key at the decode
// seam, never identity), so a hook event and its span are the same fact.
//
// The sender itself is stateless and blocking-agnostic: boundedness, retry
// and drop accounting live in `outbox.ts`. This module only builds a valid
// `ExportTraceServiceRequest` body and POSTs it.

import type { OpencodeHookEvent } from '../plugin.js'
import {
  OPENCODE_CONTRACT_VERSION,
  OPENCODE_CONTRACT_VERSION_KEY,
  OPENCODE_SESSION_ID_KEY,
} from '../../../../canon/adapters/opencode-envelope.js'

/** OTLP/HTTP trace export path on the loopback receiver. */
export const OTLP_TRACES_PATH = '/v1/traces'

/** Live loopback OTLP/HTTP endpoint (Q2): never leaves this machine. */
export const DEFAULT_OTLP_ENDPOINT = `http://127.0.0.1:4318${OTLP_TRACES_PATH}`

export type OtlpFetch = (url: string, init?: RequestInit) => Promise<Response>

/** A non-2xx answer from the receiver; `statusCode` drives 429 handling. */
export class OtlpDeliveryError extends Error {
  readonly statusCode: number

  constructor(statusCode: number, statusText: string) {
    super(`OTLP delivery failed: ${statusCode} ${statusText}`.trim())
    this.name = 'OtlpDeliveryError'
    this.statusCode = statusCode
  }
}

export type OtlpWireAttribute = { key: string; value: Record<string, unknown> }

export type OtlpWireSpan = {
  traceId: string
  spanId: string
  name: string
  kind: string
  startTimeUnixNano: string
  endTimeUnixNano: string
  attributes: OtlpWireAttribute[]
}

export type OtlpTracesBody = {
  resourceSpans: Array<{
    resource: { attributes: OtlpWireAttribute[] }
    scopeSpans: Array<{
      scope: { name: string; version: string }
      spans: OtlpWireSpan[]
    }>
  }>
}

const SCOPE_NAME = 'kyberdash.opencode-plugin'
const SCOPE_VERSION = '1.0.0'
const SERVICE_NAME = 'kyberdash-opencode-plugin'

function hash32(input: string, seed: number): number {
  let hash = seed >>> 0
  for (let index = 0; index < input.length; index += 1) {
    hash = Math.imul(hash ^ input.charCodeAt(index), 16777619)
  }
  return hash >>> 0
}

/** Deterministic lower-case hex of exactly `hexChars` characters. */
function hexId(input: string, hexChars: number): string {
  let out = ''
  let round = 0
  while (out.length < hexChars) {
    const seed = (0x811c9dc5 ^ Math.imul(round + 1, 0x01000193)) >>> 0
    out += hash32(`${round}:${input}`, seed).toString(16).padStart(8, '0')
    round += 1
  }
  return out.slice(0, hexChars)
}

function nanosOf(timestamp: string): string {
  const millis = Date.parse(timestamp)
  const safe = Number.isFinite(millis) ? millis : 0
  return (BigInt(safe) * 1_000_000n).toString()
}

function eventToSpan(event: OpencodeHookEvent, index: number): OtlpWireSpan {
  const attributes: OtlpWireAttribute[] = [
    // `session.id` is a retained correlation key (see otel/identity.ts).
    { key: 'session.id', value: { stringValue: event.sessionId } },
    // The T1 envelope identity: the contract version this build emits under
    // plus the envelope session marker. Without these the receiver
    // version-quarantines every span the installed pipeline posts, so
    // production telemetry is never claimed (see
    // `canon/adapters/opencode-envelope.ts`).
    { key: OPENCODE_CONTRACT_VERSION_KEY, value: { stringValue: OPENCODE_CONTRACT_VERSION } },
    { key: OPENCODE_SESSION_ID_KEY, value: { stringValue: event.sessionId } },
    { key: 'opencode.hook', value: { stringValue: event.kind } },
  ]
  if (event.turnIndex !== undefined) {
    attributes.push({ key: 'turn.index', value: { intValue: String(event.turnIndex) } })
  }
  if (event.spanId !== undefined) {
    attributes.push({ key: 'span.id', value: { stringValue: event.spanId } })
  }

  const started = nanosOf(event.timestamp)
  return {
    // One trace per session: every hook boundary of a session shares it,
    // so the receiver can correlate the lifecycle span-anchored (Q3).
    traceId: hexId(`trace:${event.sessionId}`, 32),
    spanId: hexId(
      `span:${event.sessionId}:${event.kind}:${event.spanId ?? ''}:${event.timestamp}:${index}`,
      16,
    ),
    name: `opencode.${event.kind}`,
    kind: 'SPAN_KIND_INTERNAL',
    startTimeUnixNano: started,
    endTimeUnixNano: started,
    attributes,
  }
}

/** Build the OTLP JSON `ExportTraceServiceRequest` for hook events. */
export function buildOtlpTracesBody(events: readonly OpencodeHookEvent[]): OtlpTracesBody {
  return {
    resourceSpans: [
      {
        resource: {
          attributes: [{ key: 'service.name', value: { stringValue: SERVICE_NAME } }],
        },
        scopeSpans: [
          {
            scope: { name: SCOPE_NAME, version: SCOPE_VERSION },
            spans: events.map((event, index) => eventToSpan(event, index)),
          },
        ],
      },
    ],
  }
}

export type OtlpSenderOptions = {
  endpoint?: string
  fetchFn?: OtlpFetch
}

export type OtlpSender = {
  readonly endpoint: string
  send: (events: readonly OpencodeHookEvent[]) => Promise<void>
}

/** POST hook events as OTLP spans; throws `OtlpDeliveryError` on non-2xx. */
export function createOtlpSender(options: OtlpSenderOptions = {}): OtlpSender {
  const endpoint = options.endpoint ?? DEFAULT_OTLP_ENDPOINT
  const fetchFn = options.fetchFn ?? ((url, init) => fetch(url, init))

  const send = async (events: readonly OpencodeHookEvent[]): Promise<void> => {
    if (events.length === 0) return
    let response: Response
    try {
      response = await fetchFn(endpoint, {
        method: 'POST',
        headers: { 'content-type': 'application/json' },
        body: JSON.stringify(buildOtlpTracesBody(events)),
      })
    } catch (error) {
      // A refused loopback connection surfaces here; the outbox retries it.
      throw error instanceof Error ? error : new Error(String(error))
    }
    if (!response.ok) {
      throw new OtlpDeliveryError(response.status, response.statusText)
    }
  }

  return { endpoint, send }
}
