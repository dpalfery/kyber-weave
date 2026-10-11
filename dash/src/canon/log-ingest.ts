import type { OtlpLog } from '../otel/receiver.js'
import { claudeApiRequestBodyFailureReason, parseClaudeApiRequestBody } from './adapters/claude-code.js'
import {
  contentFromParts,
  isNotMeasurable,
  notMeasurable,
  type CanonicalContentKey,
  type CanonicalRecord,
  type ContentPart,
  type Measurability,
} from './types.js'
import { CanonStore } from './store.js'

export type { OtlpLog }

export type LogIngestOutcome = {
  enriched: number
  pending: number
  quarantined: number
}

export type LogIngestOptions = {
  /** Clock used for pending-log expiry; injectable to make reconciliation deterministic. */
  now?: () => number
  /** How long an identified log may wait for its span. */
  pendingTtlMs?: number
}

const LOG_WINDOW_MS = 5_000
export const DEFAULT_PENDING_TTL_MS = 5_000

type PendingLog = OtlpLog & { pendingSince?: number; harness?: string }

/** The `OtlpLog.body` value naming a Claude Code API request-body event (B3). */
const CLAUDE_API_REQUEST_BODY = 'claude_code.api_request_body'
/** The `attributes['event.name']` value naming the same event (B3). */
const CLAUDE_API_REQUEST_EVENT = 'api_request_body'
/** The instrumentation scope every Claude Code event log is emitted under (B3). */
const CLAUDE_EVENTS_SCOPE = 'com.anthropic.claude_code.events'

/**
 * The pending-log attribute a parked interaction prompt is stored under
 * (B8). The key is receiver-internal — no sender emits it — which is what
 * tells a waiting prompt apart from a genuinely pended OTLP log: expiry
 * quarantines a log that never met its span, but a prompt whose span is
 * already quarantined retires by deletion — the span's quarantine packet
 * is already its evidence copy, so a second one would only duplicate
 * prompt text (T6).
 */
export const PENDING_INTERACTION_PROMPT = 'kyber.interaction_prompt'

/**
 * The canonical buckets an `api_request_body` event can supply — exactly the
 * part kinds `parseClaudeApiRequestBody` emits. `instruction_context` is
 * absent on purpose: B5 rules out a heuristic split, so no body failure can
 * honestly name it.
 */
const CLAUDE_BODY_BUCKETS: readonly CanonicalContentKey[] = [
  'system_prompt',
  'tool_definitions',
  'conversation_history',
  'tool_result_content',
]

/**
 * A Claude Code event log (B3): the request-body event names itself through
 * `attributes['event.name']` or the log body, and any Claude event — body
 * carrier or not — shares the instrumentation scope. Scope alone never
 * proves a request body, but recognition is also what tags the harness on
 * rows a per-harness wipe must scope (D-Q3), so one predicate serves both.
 */
function isClaudeLog(log: OtlpLog): boolean {
  return (
    log.attributes?.['event.name'] === CLAUDE_API_REQUEST_EVENT ||
    log.body === CLAUDE_API_REQUEST_BODY ||
    log.scope?.name === CLAUDE_EVENTS_SCOPE
  )
}

/** The body_ref echo a failure reason may carry — it is persisted and served, so it is capped. */
const MAX_BODY_REF_ECHO_CHARS = 200

/**
 * Why a recognised api_request_body event carried no usable request JSON
 * (R8), or undefined when its `body` attribute may still yield parts.
 * `body_ref` is inspected only to name the failure — the path it points at
 * is a local file this receiver deliberately never opens; telemetry must
 * not become a file-read surface (B3). Every reason here belongs to this
 * one event type: the caller consults it only when the log named itself
 * api_request_body, so a Claude event recognised by scope alone can never
 * borrow the request body's explanations for a malformed `body` it happens
 * to carry.
 */
function claudeBodyFailureReason(attributes: Record<string, unknown>): string | undefined {
  const truncated = attributes['body_truncated']
  if (truncated === true || truncated === 'true') {
    return 'The Claude Code request body was cut at the sender limit (body_truncated). Set CLAUDE_CODE_OTEL_CONTENT_MAX_LENGTH to raise it.'
  }
  const body = attributes['body']
  if (body === undefined || body === null) {
    const ref = attributes['body_ref']
    if (typeof ref !== 'string') {
      return 'The Claude Code api_request_body event carried no request body — neither a body attribute nor a body_ref was emitted.'
    }
    // The reference is sender-controlled and this reason lands in
    // measurability_json and on the API: echo a bounded prefix, never the
    // whole unbounded string.
    const echoed = ref.length > MAX_BODY_REF_ECHO_CHARS ? `${ref.slice(0, MAX_BODY_REF_ECHO_CHARS)}…` : ref
    return `The Claude Code request body arrived as a file reference (body_ref: ${echoed}), which this receiver does not read.`
  }
  return claudeApiRequestBodyFailureReason(body)
}

/**
 * Declare, for each bucket the body could have supplied and the record still
 * lacks, why nothing measurable arrived (B3/B6). The log still counts as
 * enriched — it correlated and was consumed; the failure is declared on the
 * record's measurability map rather than silently dropped.
 */
function declareBodyFailure(store: CanonStore, record: CanonicalRecord, reason: string): void {
  const supplied = new Set((record.parts ?? []).map((part) => part.part))
  const measurability: Measurability = { ...record.measurability }
  for (const bucket of CLAUDE_BODY_BUCKETS) {
    if (supplied.has(bucket) || record.content[bucket] !== undefined) continue
    measurability[bucket] = notMeasurable(reason)
  }
  store.setMeasurability(record.spanId, measurability)
}

/**
 * A bucket that just gained parts is no longer `not_measurable` (B6, per
 * record): its declaration is cleared so the session merge sees the data.
 */
function clearSuppliedBuckets(
  store: CanonStore,
  record: CanonicalRecord,
  parts: readonly ContentPart[],
): void {
  const gained = new Set(parts.map((part) => part.part))
  const measurability: Measurability = { ...record.measurability }
  let cleared = false
  for (const bucket of gained) {
    const declared = measurability[bucket]
    if (declared !== undefined && isNotMeasurable(declared)) {
      delete measurability[bucket]
      cleared = true
    }
  }
  if (cleared) store.setMeasurability(record.spanId, measurability)
}

function enrichRecord(store: CanonStore, record: CanonicalRecord, log: OtlpLog): void {
  // B1: parsed request content belongs on a Claude Code model call, and
  // `harness` + `op` are the whole gate — the span name is deliberately not
  // consulted. Anthropic documents its emitted names as beta, so gating on
  // `claude_code.llm_request` would drop content on any rename (R2).
  if (record.harness === 'claude-code' && record.op === 'llm.invoke' && isClaudeLog(log)) {
    // The failure reasons describe the api_request_body event only: a log
    // recognised by instrumentation scope alone is some other Claude event,
    // and its attributes must not borrow the request body's explanations —
    // a malformed `body` there is just content for the generic enrich below.
    const isRequestBodyEvent =
      log.attributes['event.name'] === CLAUDE_API_REQUEST_EVENT ||
      log.body === CLAUDE_API_REQUEST_BODY
    // R3: the request JSON is `attributes.body`; the key this used to read,
    // `attributes['claude_code.api_request_body']`, is never emitted.
    const failure = isRequestBodyEvent ? claudeBodyFailureReason(log.attributes) : undefined
    if (failure !== undefined) {
      declareBodyFailure(store, record, failure)
      return
    }
    const parts = parseClaudeApiRequestBody(log.attributes['body'])
    if (parts.length > 0) {
      // Per-bucket symmetry with the interaction delivery (ingest.ts): the
      // parse replaces the buckets it supplies, and a bucket an earlier
      // writer — the parked interaction prompt — delivered that this body
      // does not speak for survives the rewrite rather than being lost
      // until a re-settle.
      const supplied = new Set(parts.map((part) => part.part))
      const preserved = (record.parts ?? []).filter((part) => !supplied.has(part.part))
      const merged = [...preserved, ...parts]
      store.setContent(record.spanId, contentFromParts(merged), merged)
      clearSuppliedBuckets(store, record, merged)
      return
    }
  }
  store.enrich(record.spanId, log)
}

function target(store: CanonStore, log: OtlpLog) {
  if (log.traceId !== null && log.spanId !== null) {
    const exact = store.findByTraceSpan(log.traceId, log.spanId)
    if (exact !== undefined) return exact
  }
  if (log.sessionId !== null) {
    // R7: the session-time fallback may only land on a model call. An
    // unfiltered nearest pick hands the log to whatever tool span happened
    // to share the window, so the store is asked for the op up front.
    return store.findBySessionTime(log.sessionId, log.timestamp, LOG_WINDOW_MS, 'llm.invoke')
  }
  return undefined
}

export function ingestLogBatch(
  logs: readonly OtlpLog[],
  store: CanonStore,
  options: LogIngestOptions = {},
): LogIngestOutcome {
  const now = options.now ?? Date.now
  const pendingTtlMs = Math.max(0, options.pendingTtlMs ?? DEFAULT_PENDING_TTL_MS)
  let enriched = 0
  let pending = 0
  let quarantined = reconcileExpiredPending(store, now(), pendingTtlMs)
  const existingPending = new Map(
    (store.getPendingLogs() as PendingLog[]).map((log) => [log.logId, log]),
  )
  for (const log of logs) {
    if (store.isLogEnriched(log.logId)) continue
    const record = target(store, log)
    if (record !== undefined) {
      enrichRecord(store, record, log)
      store.deletePendingLog(log.logId)
      store.markLogEnriched(log.logId)
      enriched += 1
      continue
    }
    // The recognition tag rides the row (B3/D-Q3): a Claude log the wipe
    // must be able to scope carries 'claude-code'; anything else stays NULL.
    const harness = isClaudeLog(log) ? 'claude-code' : undefined
    if (log.traceId === null && log.spanId === null) {
      store.quarantineLog(log, 'could not correlate log to a canonical span', harness)
      quarantined += 1
    } else {
      const pendingLog = {
        ...log,
        pendingSince: existingPending.get(log.logId)?.pendingSince ?? now(),
      }
      store.addPendingLog(pendingLog, harness)
      pending += 1
    }
  }
  return { enriched, pending, quarantined }
}

/**
 * Quarantine identified logs that have waited longer than the enrichment TTL.
 *
 * This is exported so the collector lifecycle can reconcile pending logs even
 * when no later OTLP batch arrives.
 */
export function reconcileExpiredPending(store: CanonStore, now: number, ttlMs: number): number {
  let quarantined = 0
  for (const log of store.getPendingLogs() as PendingLog[]) {
    // Older rows predate pendingSince; treating reconciliation as their arrival
    // keeps upgrades from unexpectedly quarantining a backlog immediately.
    const pendingSince = log.pendingSince ?? now
    if (now - pendingSince < ttlMs) continue
    if (log.attributes[PENDING_INTERACTION_PROMPT] !== undefined) {
      // A parked interaction prompt is not a log waiting for its span —
      // the span is already quarantined, and its quarantine packet is
      // already the prompt's evidence copy — so expiry retires the row
      // rather than moving it (B8/T6). The wait is TTL-bounded like any
      // other's: left unbounded, a prompt whose children never arrive
      // would sit in pending_logs until the next span batch or a wipe.
      store.deletePendingLog(log.logId)
      continue
    }
    store.deletePendingLog(log.logId)
    // The harness tag written with the pending row travels to quarantine with
    // it; a row old enough to predate tagging answers from its own signals.
    store.quarantineLog(
      log,
      'identified log remained unmatched past pending expiry',
      log.harness ?? (isClaudeLog(log) ? 'claude-code' : undefined),
    )
    quarantined += 1
  }
  return quarantined
}
