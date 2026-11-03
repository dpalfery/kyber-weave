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
import { CanonStore, type ModelCallCandidate } from './store.js'
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
/**
 * How long a Claude request-body log may wait for its model call (D3). The
 * sender logs the body when the request starts but exports the call's span
 * only when it ends, so the body routinely arrives first and a long call
 * outlasts the 5 s default. Every other log keeps `DEFAULT_PENDING_TTL_MS`.
 */
export const CLAUDE_REQUEST_BODY_PENDING_TTL_MS = 900_000
/** How far a model call's start may be from a body log's timestamp to be a candidate (D11). */
export const CLAUDE_BODY_MATCH_WINDOW_MS = 2_000
/** The `raw` key a record keeps its claiming request-body log under (D20). */
export const CLAUDE_REQUEST_BODY_CLAIM_KEY = 'kyber.claude_request_body'
/** Why a parseable body with no `tools` array leaves tool definitions unmeasured (D19). */
const NO_TOOLS_ARRAY_REASON =
  'This Claude Code request body carried no tools array, so the sender did not export tool definitions for this call.'

/** The request-body log a record's content currently comes from (D20). */
export type ClaudeRequestBodyClaim = {
  logId: string
  /** The claiming log's own timestamp, ISO; the later one wins. */
  timestamp: string
  requestBodyId?: string
  /**
   * The buckets this body supplied. A later winner drops exactly these from the
   * record, so span-sourced parts in other buckets survive. Claims written
   * before this field existed lack it and fall back to dropping
   * system_prompt and tool_definitions wholesale.
   */
  suppliedBuckets?: CanonicalContentKey[]
}

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

/** A Claude `api_request_body` event specifically, as opposed to any other Claude event log. */
export function isClaudeRequestBodyLog(log: OtlpLog): boolean {
  return (
    log.attributes?.['event.name'] === CLAUDE_API_REQUEST_EVENT || log.body === CLAUDE_API_REQUEST_BODY
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

function readClaim(record: CanonicalRecord): ClaudeRequestBodyClaim | undefined {
  const raw: unknown = record.raw
  if (typeof raw !== 'object' || raw === null || Array.isArray(raw)) return undefined
  const claim = (raw as Record<string, unknown>)[CLAUDE_REQUEST_BODY_CLAIM_KEY]
  if (typeof claim !== 'object' || claim === null) return undefined
  const { logId, timestamp } = claim as Record<string, unknown>
  return typeof logId === 'string' && typeof timestamp === 'string'
    ? (claim as ClaudeRequestBodyClaim)
    : undefined
}

/** A timestamp as epoch ms; an unparseable one is -Infinity, older than any valid time. */
function timeOrOldest(value: string): number {
  const ms = Date.parse(value)
  return Number.isFinite(ms) ? ms : Number.NEGATIVE_INFINITY
}

/**
 * Whether the claimed body keeps the record against an incoming one (D20): the
 * later timestamp wins, an identical timestamp goes to the higher logId so
 * arrival order never decides, and an unparseable timestamp (either side)
 * counts as older than any valid one.
 */
function claimBeatsLog(claim: ClaudeRequestBodyClaim, log: OtlpLog): boolean {
  const claimed = timeOrOldest(claim.timestamp)
  const incoming = timeOrOldest(log.timestamp)
  if (claimed !== incoming) return claimed > incoming
  return claim.logId >= log.logId
}

/**
 * Apply one correlated log to its record. A Claude request body replaces the
 * body-derived buckets; when the record already holds a body the later log
 * timestamp wins and the earlier is consumed without effect (D20).
 */
export function enrichRecord(store: CanonStore, record: CanonicalRecord, log: OtlpLog): void {
  // B1: parsed request content belongs on a Claude Code model call, and
  // `harness` + `op` are the whole gate — the span name is deliberately not
  // consulted. Anthropic documents its emitted names as beta, so gating on
  // `claude_code.llm_request` would drop content on any rename (R2).
  if (record.harness === 'claude-code' && record.op === 'llm.invoke' && isClaudeLog(log)) {
    // The failure reasons describe the api_request_body event only: a log
    // recognised by instrumentation scope alone is some other Claude event,
    // and its attributes must not borrow the request body's explanations —
    // a malformed `body` there is just content for the generic enrich below.
    const isRequestBodyEvent = isClaudeRequestBodyLog(log)
    // D20: a body older than the one the record already holds is consumed,
    // not applied; arrival order never decides which request a call keeps.
    const claim = isRequestBodyEvent ? readClaim(record) : undefined
    if (claim !== undefined && claimBeatsLog(claim, log)) return
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
      // A later body that wins over an already-claimed one replaces what the
      // earlier body supplied, so its tools (or history) cannot survive and mask
      // the not-measurable reason (D19/D20). The claim records the buckets that
      // body supplied; parts in any other bucket are span- or transcript-sourced
      // and stay. A claim from before suppliedBuckets existed has no list and
      // drops system_prompt and tool_definitions wholesale, as it always did.
      const earlierSupplied: ReadonlySet<string> | undefined =
        isRequestBodyEvent && claim !== undefined
          ? new Set(claim.suppliedBuckets ?? ['system_prompt', 'tool_definitions'])
          : undefined
      const preserved = (record.parts ?? []).filter(
        (part) => !supplied.has(part.part) && !(earlierSupplied?.has(part.part) ?? false),
      )
      const merged = [...preserved, ...parts]
      store.setContent(record.spanId, contentFromParts(merged), merged)
      declareMeasurability(store, record, merged, isRequestBodyEvent)
      if (isRequestBodyEvent) {
        const requestBodyId = log.attributes['request_body_id']
        store.setRawEntry(record.spanId, CLAUDE_REQUEST_BODY_CLAIM_KEY, {
          logId: log.logId,
          timestamp: log.timestamp,
          ...(typeof requestBodyId === 'string' ? { requestBodyId } : {}),
          suppliedBuckets: [...supplied] as CanonicalContentKey[],
        } satisfies ClaudeRequestBodyClaim)
      }
      return
    }
  }
  store.enrich(record.spanId, log)
}

/**
 * Per-record availability after a body delivered `merged`: a bucket that just
 * gained parts is no longer `not_measurable` (B6), and a parseable request
 * body with no `tools` array declares tool definitions unmeasured with the
 * sender's reason instead of showing an empty block as data (D19).
 */
function declareMeasurability(
  store: CanonStore,
  record: CanonicalRecord,
  merged: readonly ContentPart[],
  isRequestBodyEvent: boolean,
): void {
  const measurability: Measurability = { ...record.measurability }
  let changed = false
  for (const bucket of new Set(merged.map((part) => part.part))) {
    const declared = measurability[bucket]
    if (declared !== undefined && isNotMeasurable(declared)) {
      delete measurability[bucket]
      changed = true
    }
  }
  if (isRequestBodyEvent && !merged.some((part) => part.part === 'tool_definitions')) {
    measurability.tool_definitions = notMeasurable(NO_TOOLS_ARRAY_REASON)
    changed = true
  }
  if (changed) store.setMeasurability(record.spanId, measurability)
}

/**
 * The model call a Claude request-body log belongs to: the candidate whose
 * start is nearest the log, within `CLAUDE_BODY_MATCH_WINDOW_MS`, that had not
 * already ended when the log was written (D11). No candidate, or two at equal
 * distance, gives `undefined` so the caller leaves the log pending rather than
 * attach it to a guess. Shared by the live path and the quarantine relink.
 */
export function nearestClaudeModelCall<
  T extends { timestamp: Date | string; durationMs?: number | null },
>(calls: readonly T[], logTimestamp: string): T | undefined {
  const at = Date.parse(logTimestamp)
  if (Number.isNaN(at)) return undefined
  const scored: { call: T; distance: number }[] = []
  for (const call of calls) {
    const start = call.timestamp instanceof Date ? call.timestamp.getTime() : Date.parse(String(call.timestamp))
    if (Number.isNaN(start)) continue
    const distance = Math.abs(start - at)
    if (distance > CLAUDE_BODY_MATCH_WINDOW_MS) continue
    // End exclusion needs a known duration; a missing or NaN one is unknown,
    // not an instant. The start window above still bounds such a call.
    if (typeof call.durationMs === 'number' && Number.isFinite(call.durationMs) && at > start + call.durationMs) continue
    scored.push({ call, distance })
  }
  scored.sort((a, b) => a.distance - b.distance)
  const [best, runnerUp] = scored
  if (best === undefined) return undefined
  if (runnerUp !== undefined && runnerUp.distance === best.distance) return undefined
  return best.call
}

/** Score lightweight candidates, then load only the chosen call whole. */
function fetchChosen(
  store: CanonStore,
  candidates: readonly ModelCallCandidate[],
  logTimestamp: string,
): CanonicalRecord | undefined {
  const chosen = nearestClaudeModelCall(candidates, logTimestamp)
  return chosen === undefined ? undefined : store.get(chosen.spanId)
}

/**
 * The pre-existing bounded session-time fallback for a log from any other
 * harness, kept only for the shape that always had it: a log with no trace and
 * no span whose session came from a `session_id`. A log whose session is the
 * `session.id` attribute (OpenCode) never had a session before the receiver
 * learned that key, and a log that names a trace or span is not context-free,
 * so neither gains a fallback here (D4, Goal 4).
 */
function legacySessionFallback(store: CanonStore, log: OtlpLog): CanonicalRecord | undefined {
  if (log.sessionId === null || log.traceId !== null || log.spanId !== null) return undefined
  if (log.attributes?.['session.id'] !== undefined) return undefined
  return store.findBySessionTime(log.sessionId, log.timestamp, LOG_WINDOW_MS, 'llm.invoke')
}

/**
 * Find the record a log enriches. Exact `(traceId, spanId)` first. A Claude
 * request-body log then falls to the nearest-start rule, scoped to its trace
 * (D11) or, with no trace id, to its session (D13). Any other Claude log keeps
 * the bounded session-time fallback; a non-Claude log has no fallback (D4).
 */
export function findLogTarget(store: CanonStore, log: OtlpLog): CanonicalRecord | undefined {
  if (log.traceId !== null && log.spanId !== null) {
    const exact = store.findByTraceSpan(log.traceId, log.spanId)
    if (exact !== undefined) return exact
  }
  if (!isClaudeLog(log)) return legacySessionFallback(store, log)
  if (isClaudeRequestBodyLog(log)) {
    if (log.traceId !== null) {
      return fetchChosen(store, store.claudeModelCallCandidatesInTrace(log.traceId), log.timestamp)
    }
    if (log.sessionId !== null) {
      return fetchChosen(store, store.claudeModelCallCandidatesInSession(log.sessionId), log.timestamp)
    }
    return undefined
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
  const session = beginLogIngestBatch(store, now(), pendingTtlMs)
  const { enriched, pending, quarantined } = ingestLogPage(logs, store, session)
  return { enriched, pending, quarantined: session.quarantined + quarantined }
}

/**
 * One scan of expiry plus dedup metadata shared by a whole multi-page replay
 * (F8): reconciling and re-reading the backlog metadata once per page is
 * N-squared work for many pages. Call once, then feed each page to
 * `ingestLogPage`, which performs no store-wide scans of its own. Re-pended
 * rows keep the same `logId`, so a pre-replay snapshot preserves
 * `pendingSince` semantics: rows behind the `log_id` cursor are never
 * revisited in the same replay, and partial per-page commits stay committed.
 */
export type LogIngestSession = {
  now: number
  quarantined: number
  existingPendingSince: Map<string, number | null>
}

export function beginLogIngestBatch(store: CanonStore, now: number, pendingTtlMs: number): LogIngestSession {
  return {
    now,
    quarantined: reconcileExpiredPending(store, now, pendingTtlMs),
    // Dedup reads metadata only (F8): the arrival time rides the row, so the
    // duplicate decision needs no payload — a large backlog of heavy bodies
    // costs one small-column scan, not a parse-everything snapshot per page.
    existingPendingSince: new Map(
      store.pendingLogMetadata().map((row) => [row.logId, row.pendingSince]),
    ),
  }
}

/**
 * Enrich, consume, or re-pend one page of logs against a session from
 * `beginLogIngestBatch` (F8): no expiry scan, no metadata scan — the page's
 * own reads (target lookup, enrich writes) are the only store work.
 */
export function ingestLogPage(
  logs: readonly OtlpLog[],
  store: CanonStore,
  session: LogIngestSession,
): LogIngestOutcome {
  let enriched = 0
  let pending = 0
  let quarantined = 0
  for (const log of logs) {
    if (store.isLogEnriched(log.logId)) continue
    const record = findLogTarget(store, log)
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
    // A context-free Claude body that still names its session pends (D13):
    // its call has not arrived yet, whatever other records the session holds
    // (a tool span is never a legal target, but it does not make the body
    // unmatchable). Everything else context-free quarantines.
    const waitsForSession = isClaudeRequestBodyLog(log) && log.sessionId !== null
    if (log.traceId === null && log.spanId === null && !waitsForSession) {
      store.quarantineLog(log, 'could not correlate log to a canonical span', harness)
      quarantined += 1
    } else {
      const pendingLog = {
        ...log,
        pendingSince: session.existingPendingSince.get(log.logId) ?? session.now,
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
 * when no later OTLP batch arrives. Expiry is SQL-selected (F8): only expired
 * rows' payloads are parsed — a backlog of live bodies is never decoded for
 * this decision.
 */
export function reconcileExpiredPending(store: CanonStore, now: number, ttlMs: number): number {
  let quarantined = 0
  for (const entry of store.takeExpiredPendingLogs(now, ttlMs, CLAUDE_REQUEST_BODY_PENDING_TTL_MS)) {
    const log = entry as PendingLog
    if (log.attributes?.[PENDING_INTERACTION_PROMPT] !== undefined) {
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
      log.harness ?? (isClaudeLog(log as OtlpLog) ? 'claude-code' : undefined),
    )
    quarantined += 1
  }
  return quarantined
}
