import type { OtlpLog } from '../otel/receiver.js'
import { parseClaudeApiRequestBody } from './adapters/claude-code.js'
import { contentFromParts, type CanonicalRecord } from './types.js'
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

type PendingLog = OtlpLog & { pendingSince?: number }

const CLAUDE_API_REQUEST_BODY = 'claude_code.api_request_body'

function enrichRecord(store: CanonStore, record: CanonicalRecord, log: OtlpLog): void {
  const body = log.attributes[CLAUDE_API_REQUEST_BODY]
  const parts = parseClaudeApiRequestBody(body)
  const isClaudeLlmRequest =
    record.harness === 'claude-code' &&
    record.name === 'llm_request' &&
    record.op === 'llm.invoke'
  if (parts.length > 0 && isClaudeLlmRequest) {
    store.setContent(record.spanId, contentFromParts(parts), parts)
    return
  }
  store.enrich(record.spanId, log)
}

function target(store: CanonStore, log: OtlpLog) {
  if (log.traceId !== null && log.spanId !== null) {
    const exact = store.findByTraceSpan(log.traceId, log.spanId)
    if (exact !== undefined) return exact
  }
  if (log.sessionId !== null) return store.findBySessionTime(log.sessionId, log.timestamp, LOG_WINDOW_MS)
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
    if (log.traceId === null && log.spanId === null) {
      store.quarantineLog(log, 'could not correlate log to a canonical span')
      quarantined += 1
    } else {
      const pendingLog = {
        ...log,
        pendingSince: existingPending.get(log.logId)?.pendingSince ?? now(),
      }
      store.addPendingLog(pendingLog)
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
    store.deletePendingLog(log.logId)
    store.quarantineLog(log, 'identified log remained unmatched past pending expiry')
    quarantined += 1
  }
  return quarantined
}
