// Opencode plugin turn mapping + log correlation (T4): turn outcomes onto
// /v1/logs, correlated by (trace_id, span_id) then session+bounds (ADR 0009).
//
// The dash-CLI-managed plugin observes session/turn/span hooks with the shapes
// surveyed in T0 (`fixtures/span-payload-skeleton.json`: session id, turn
// index, agent, model provider/id, prompt text, tool call, token counters,
// errors) and carries spans through the T3 mapper onto the T1 envelope
// (`mapper.ts` + `canon/adapters/opencode-envelope.ts`). Spans alone leave a
// tool call without its outcome: the result (success, duration, size) is known
// only when the tool finishes, after the span was emitted. T4 closes that gap
// on the log signal — one `tool_result` log per tool outcome, under scope
// `com.opencode`, stamped with the span's correlation identity when the hook
// observed it and with the session identity always, so the existing log path
// (`canon/log-ingest.ts`) enriches the one span-shaped record the outcome
// belongs to and never inserts a parallel one.
//
// Why the log carries both identities: exact `(traceId, spanId)` is the join,
// and the session id plus timestamp is the ADR 0009 fallback when the outcome
// outruns its span or the span id was never observed. A log that correlates
// to nothing quarantines with its reason and counts — content without a home
// is evidence, not something to drop — and the log id is the receiver's own
// `deriveLogId` over the same identity, so a duplicate delivery enriches once.
//
// Wiring is to the mapper/skeleton only: the payload identity reuses the
// mapper's `OpencodePluginSpanPayload` shape, and the scope/body vocabulary is
// the `com.opencode` `tool_result` the todo names. No receiver, transport,
// outbox, web, tray, resolver, or API change lives here.

import { deriveLogId, type OtlpLog } from '../../../otel/receiver.js'
import type { OpencodePluginSpanPayload } from './mapper.js'

/** The instrumentation scope a turn outcome log is emitted under. */
export const OPENCODE_TOOL_RESULT_SCOPE = 'com.opencode'

/** The log body a turn outcome log carries. */
export const OPENCODE_TOOL_RESULT_BODY = 'tool_result'

/** The session+bounds fallback window (ADR 0009 rule), in milliseconds. */
export const OPENCODE_TURN_LOG_WINDOW_MS = 5_000

/**
 * A turn's tool outcome as the plugin hook observed it. The correlation
 * identity reuses the mapper payload's fields (`traceId`, `spanId`,
 * `sessionID`, `turnIndex`, `tool`, `callID`, `messageID`, `agent`) so a span
 * the mapper built and a log this module builds always name the same turn;
 * the outcome fields are what the span cannot know yet (`success`,
 * `durationMs`, `resultText`, `resultSizeBytes`).
 */
export type OpencodeTurnOutcomePayload = {
  traceId?: string | null
  spanId?: string | null
  sessionID: string
  turnIndex?: number
  tool?: string
  callID?: string
  messageID?: string
  agent?: string
  success?: boolean
  durationMs?: number
  resultText?: string
  resultSizeBytes?: number
  timestampNanos?: string | number | bigint
  timestamp?: string
  hook?: string
}

/** The structured outcome a `tool_result` log carries, or undefined. */
export type OpencodeTurnOutcome = {
  tool?: string
  agent?: string
  success?: boolean
  durationMs?: number
  resultSizeBytes?: number
}

function nonEmpty(value: unknown): value is string {
  return typeof value === 'string' && value !== ''
}

function finiteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}

function toNanoString(value: string | number | bigint): string {
  return typeof value === 'bigint' ? value.toString() : String(value)
}

function timeOrNow(timestampNanos: string | number | bigint | undefined, timestamp: string | undefined): {
  iso: string
  nanos: bigint
} {
  // Best-effort harness clock: junk input falls back to now rather than
  // throwing the turn outcome away. Mirrors the mapper's safe fallback.
  if (timestampNanos !== undefined) {
    try {
      const nanos = BigInt(toNanoString(timestampNanos))
      const ms = Number(nanos / 1_000_000n)
      if (Number.isFinite(ms)) return { iso: new Date(ms).toISOString(), nanos }
    } catch {
      // Fall through to the ISO string, then to now.
    }
  }
  if (timestamp !== undefined) {
    const ms = Date.parse(timestamp)
    if (Number.isFinite(ms)) {
      return { iso: new Date(ms).toISOString(), nanos: BigInt(ms) * 1_000_000n }
    }
  }
  const nowMs = Date.now()
  return { iso: new Date(nowMs).toISOString(), nanos: BigInt(nowMs) * 1_000_000n }
}

/**
 * Carry one turn outcome onto a decoded OTLP log ready for `ingestLogBatch`.
 * The correlation identity is the span's when the hook observed it
 * (`traceId`/`spanId`) and always the session id, so the exact join works when
 * the span exists and the session+bounds fallback can bridge a late span.
 * The log id is the receiver's `deriveLogId` over that same identity, so a
 * duplicate delivery of the outcome enriches the record once and replays
 * idempotently through the existing `enriched_logs` guard.
 */
export function mapOpencodeTurnToOtlpLog(payload: OpencodeTurnOutcomePayload): OtlpLog {
  const { iso, nanos } = timeOrNow(payload.timestampNanos, payload.timestamp)
  const attributes: Record<string, unknown> = {
    'session.id': payload.sessionID,
    'opencode.session.id': payload.sessionID,
  }
  if (finiteNumber(payload.turnIndex)) {
    attributes['opencode.turn.index'] = payload.turnIndex
  }
  if (nonEmpty(payload.tool)) attributes['tool_name'] = payload.tool
  if (nonEmpty(payload.agent)) attributes['agent'] = payload.agent
  if (typeof payload.success === 'boolean') attributes['success'] = payload.success
  if (finiteNumber(payload.durationMs)) attributes['duration_ms'] = payload.durationMs
  if (nonEmpty(payload.tool)) attributes['opencode.tool.name'] = payload.tool
  if (nonEmpty(payload.messageID)) attributes['opencode.message.id'] = payload.messageID
  if (nonEmpty(payload.callID)) {
    attributes['opencode.call.id'] = payload.callID
    if (nonEmpty(payload.tool)) attributes['opencode.tool.call.id'] = payload.callID
  }
  if (nonEmpty(payload.hook)) attributes['opencode.hook'] = payload.hook
  if (finiteNumber(payload.resultSizeBytes)) {
    attributes['tool_result_size_bytes'] = payload.resultSizeBytes
  }
  if (nonEmpty(payload.resultText)) attributes['opencode.tool.result'] = payload.resultText
  const traceId = nonEmpty(payload.traceId) ? payload.traceId : null
  const spanId = nonEmpty(payload.spanId) ? payload.spanId : null
  return {
    logId: deriveLogId({
      traceId,
      spanId,
      sessionId: payload.sessionID,
      timeUnixNano: nanos,
      body: OPENCODE_TOOL_RESULT_BODY,
      attributes,
    }),
    traceId,
    spanId,
    sessionId: payload.sessionID,
    timestamp: iso,
    body: OPENCODE_TOOL_RESULT_BODY,
    attributes,
    resource: { 'service.name': 'opencode' },
    scope: { name: OPENCODE_TOOL_RESULT_SCOPE },
  }
}

/**
 * True when the log is a turn outcome: the `tool_result` body under the
 * `com.opencode` scope. The body alone is not enough — another harness could
 * log the same word — and the scope alone is not either.
 */
export function isOpencodeToolResultLog(
  log: Pick<OtlpLog, 'body' | 'scope' | 'attributes'>,
): boolean {
  return log.body === OPENCODE_TOOL_RESULT_BODY && log.scope?.name === OPENCODE_TOOL_RESULT_SCOPE
}

/**
 * Read the structured outcome back off a turn outcome log. Anything that is
 * not a turn outcome log gives undefined, so callers never mistake another
 * harness's log for an outcome.
 */
export function opencodeTurnOutcome(
  log: Pick<OtlpLog, 'body' | 'scope' | 'attributes'>,
): OpencodeTurnOutcome | undefined {
  if (!isOpencodeToolResultLog(log)) return undefined
  const outcome: OpencodeTurnOutcome = {}
  const tool = log.attributes['tool_name']
  if (nonEmpty(tool)) outcome.tool = tool
  const agent = log.attributes['agent']
  if (nonEmpty(agent)) outcome.agent = agent
  const success = log.attributes['success']
  if (typeof success === 'boolean') outcome.success = success
  const durationMs = log.attributes['duration_ms']
  if (finiteNumber(durationMs)) outcome.durationMs = durationMs
  const resultSizeBytes = log.attributes['tool_result_size_bytes']
  if (finiteNumber(resultSizeBytes)) outcome.resultSizeBytes = resultSizeBytes
  return outcome
}

/** The span-identity view the session+bounds fallback matches over. */
export type OpencodeTurnRecordView = {
  spanId: string
  traceId: string | null
  sessionId?: string | null
  timestamp: string | Date
  durationMs?: number | null
  op?: string
}

function timeOf(value: string | Date): number {
  return value instanceof Date ? value.getTime() : Date.parse(String(value))
}

/**
 * Find the record a turn outcome log joins, without touching the store: exact
 * `(traceId, spanId)` first, then the session id plus timestamp window. An
 * exact identity that names nothing gives undefined rather than a
 * session guess — the log waits for its span (pending) instead of joining the
 * wrong one — and a tie or no candidate in the window is unmatched. A tool
 * outcome prefers the `tool.invoke` span when one is in range, since the
 * session window can also hold the turn's model span.
 */
export function findOpencodeTurnTarget<T extends OpencodeTurnRecordView>(
  records: readonly T[],
  log: Pick<OtlpLog, 'traceId' | 'spanId' | 'sessionId' | 'timestamp'>,
  windowMs: number = OPENCODE_TURN_LOG_WINDOW_MS,
): T | undefined {
  if (log.traceId !== null && log.spanId !== null) {
    const exact = records.find((record) => record.traceId === log.traceId && record.spanId === log.spanId)
    return exact
  }
  if (log.sessionId === null || log.sessionId === undefined) return undefined
  const at = Date.parse(log.timestamp)
  if (Number.isNaN(at)) return undefined
  const inWindow = records.filter((record) => {
    if (record.sessionId !== log.sessionId) return false
    const start = timeOf(record.timestamp)
    if (Number.isNaN(start)) return false
    return Math.abs(start - at) <= windowMs
  })
  if (inWindow.length === 0) return undefined
  const tools = inWindow.filter((record) => record.op === 'tool.invoke')
  const candidates = tools.length > 0 ? tools : inWindow
  const scored = candidates.map((record) => ({ record, distance: Math.abs(timeOf(record.timestamp) - at) }))
  scored.sort((a, b) => a.distance - b.distance)
  const [best, runnerUp] = scored
  if (best === undefined) return undefined
  if (runnerUp !== undefined && runnerUp.distance === best.distance) return undefined
  return best.record
}

/**
 * The span identity a turn outcome joins, read off a mapper payload. The
 * plugin stamps the hooks it observes onto mapper-shaped payloads, so the
 * turn module takes that same shape here rather than redeclaring the
 * skeleton's session/turn/span fields.
 */
export function opencodeTurnSpanIdentity(payload: Pick<OpencodePluginSpanPayload, 'traceId' | 'spanId' | 'sessionID' | 'turnIndex'>): {
  traceId: string
  spanId: string
  sessionID: string
  turnIndex?: number
} {
  return {
    traceId: payload.traceId,
    spanId: payload.spanId,
    sessionID: payload.sessionID,
    ...(finiteNumber(payload.turnIndex) ? { turnIndex: payload.turnIndex } : {}),
  }
}
