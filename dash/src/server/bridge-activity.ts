// Refresh windows, ingest activity and session rollups. Split from `./bridge.ts`,
// which re-exports every name here that it exported before.

import { sourceDisplayName } from '../canon/measurability.js'
import { type IngestActivity, type IngestActivitySource, type SessionRollup, type SessionSummary } from './bridge-types.js'

/** One week in milliseconds — the unit of the refresh coverage window. */
export const REFRESH_WEEK_MS = 7 * 24 * 60 * 60 * 1000

/**
 * Derive the coverage window of a refresh run (T4).
 *
 * <remarks>
 * The window a run covered is `[startedAt − historyWeeks, startedAt]`
 * (`utcHistoryWindow` in `refresh/source-reader.ts` anchors on the command
 * start, which is what `started_at` persists). Anything missing or
 * unparseable degrades to null/null: unknown stays unknown, never 0.
 * </remarks>
 */
export function refreshWindowBounds(
  startedAt: string | null | undefined,
  historyWeeks: number | null | undefined,
): { coveredFrom: string | null; coveredThrough: string | null } {
  if (startedAt === null || startedAt === undefined) return { coveredFrom: null, coveredThrough: null }
  if (historyWeeks === null || historyWeeks === undefined) return { coveredFrom: null, coveredThrough: null }
  const throughMs = Date.parse(startedAt)
  if (!Number.isFinite(throughMs) || !Number.isFinite(historyWeeks)) {
    return { coveredFrom: null, coveredThrough: null }
  }
  return {
    coveredFrom: new Date(throughMs - historyWeeks * REFRESH_WEEK_MS).toISOString(),
    coveredThrough: startedAt,
  }
}

/**
 * Assemble per-source ingest activity from record counts and log aggregates (T4).
 *
 * <remarks>
 * The union of both key sets is reported so a log-only source (e.g.
 * `otlp:logs`) still shows its received count beside zero records, and a
 * record-only source still shows its history beside zero ingested. Both
 * zeros are facts about their respective tables, not claims about the
 * receiver. Empty + empty is the only `unknown`.
 * </remarks>
 */
export function buildIngestActivity(
  recordCounts: ReadonlyMap<string, number>,
  logSums: ReadonlyMap<string, { total: number; lastAt: string | null }>,
  lastReceivedAt: string | null,
): IngestActivity {
  if (recordCounts.size === 0 && logSums.size === 0) {
    return {
      status: 'unknown',
      reason: 'no receiver activity recorded',
      sources: [],
      lastReceivedAt: null,
    }
  }
  const keys = [...new Set([...recordCounts.keys(), ...logSums.keys()])].sort()
  const sources: IngestActivitySource[] = keys.map((source) => {
    const labeled = sourceDisplayName(source)
    return {
      source,
      display: labeled.display,
      kind: labeled.kind,
      recordCount: recordCounts.get(source) ?? 0,
      ingestedCount: logSums.get(source)?.total ?? 0,
      lastReceivedAt: logSums.get(source)?.lastAt ?? null,
    }
  })
  return { status: 'known', sources, lastReceivedAt }
}

/**
 * A session's own counters plus its descendants', counted once. A child whose records are
 * already inside this session's counters (`included_in_parent`) adds nothing, and neither
 * does anything beneath it; every other child contributes its own rollup, leaf-first.
 * `visiting` guards a malformed parent cycle from recursing forever.
 */
export function computeSessionRollup(session: SessionSummary, visiting: Set<string> = new Set()): SessionRollup {
  visiting.add(session.session_id)
  let turnCount = session.turn_count ?? 0
  let requestCount = session.request_count ?? 0
  let totalInput = session.total_input ?? 0
  let totalOutput = session.total_output ?? 0
  let costUsdSum: number | null = session.cost_usd != null ? session.cost_usd : null

  for (const child of session.subagents ?? []) {
    if (child.included_in_parent === true || visiting.has(child.session_id)) continue
    const rolled = child.rollup ?? computeSessionRollup(child, visiting)
    turnCount += rolled.turn_count
    requestCount += rolled.request_count
    totalInput += rolled.total_input
    totalOutput += rolled.total_output
    if (rolled.cost_usd != null) costUsdSum = (costUsdSum ?? 0) + rolled.cost_usd
  }
  visiting.delete(session.session_id)

  return {
    turn_count: turnCount,
    request_count: requestCount,
    total_input: totalInput,
    total_output: totalOutput,
    cost_usd: costUsdSum,
  }
}
