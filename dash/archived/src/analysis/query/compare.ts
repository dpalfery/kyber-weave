/**
 * Like-for-like run comparison (`compare`): relative input-token deltas
 * between selected runs, without causality claims.
 *
 * <remarks>
 * Scopes must share the session grain — comparing a session-grain scope
 * against a turn-grain one divides incompatible units. A zero or
 * unavailable baseline yields a null relative delta with a reason, never a
 * fabricated percentage, and the result never says one run caused the
 * other's figures.
 * </remarks>
 */

import { MAX_CANDIDATES } from './catalog.js'
import { isFiniteNumber, isRecord, type AnalyticsScope, type AnalyticsSource, type OperationError } from './types.js'

export type CompareSuccess = {
  deltas: Record<string, Record<string, unknown>>
}

export type CompareResult =
  | { ok: true; result: CompareSuccess }
  | { ok: false; error: OperationError }

export async function executeCompare(
  source: AnalyticsSource,
  scope: AnalyticsScope,
  raw: Record<string, unknown>,
): Promise<CompareResult> {
  if (Array.isArray(raw.scopes)) {
    return compareScopes(source, scope, raw.scopes)
  }
  const ids = Array.isArray(raw.runIds) ? raw.runIds.filter((id): id is string => typeof id === 'string') : []
  // WHY no default pair: the run-compare design keeps the selected pair
  // manual-only (issue #190) — an empty or miscounted request fails loudly
  // before any canonical read, never silently comparing the newest runs.
  if (ids.length !== 2) {
    return { ok: false, error: { code: 'invalid_request', message: 'A run comparison needs exactly two run ids.', hint: 'Send two run ids from the run inventory.' } }
  }
  const runs = await source.readRuns(scope)
  const selected = runs.filter((run) => ids.includes(String(run.id ?? run.runId)))
  if (selected.length !== 2) {
    const known = new Set(runs.map((run) => String(run.id ?? run.runId)))
    const missing = ids.filter((id) => !known.has(id))
    return {
      ok: false,
      error: {
        code: 'not_found',
        message: missing.length > 0 ? `The requested runs were not found: ${missing.join(', ')}.` : 'The run comparison needs two distinct known runs.',
        hint: 'Use the run inventory to discover run identifiers.',
      },
    }
  }
  const before = selected[0]?.inputTokens
  const after = selected[1]?.inputTokens
  return {
    ok: true,
    result: { deltas: { input_tokens: relativeDelta(before, after) } },
  }
}

/** A null relative delta with the reason a zero or unavailable baseline carries. */
function relativeDelta(before: unknown, after: unknown): Record<string, unknown> {
  const relative = isFiniteNumber(before) && before !== 0 && isFiniteNumber(after) ? (after - before) / before : null
  return { relative, ...(relative === null ? { reason: 'A zero or unavailable baseline has no relative delta.' } : {}) }
}

/**
 * Two-scope comparison: each side resolves its own session set through the
 * projection, then the summed input-token figures are compared like-for-like.
 * The sides must agree on grain, metrics and grouping — differing units
 * would divide incompatible quantities — so per-side overrides that diverge
 * are refused before any canonical read.
 */
async function compareScopes(
  source: AnalyticsSource,
  scope: AnalyticsScope,
  scopes: unknown[],
): Promise<CompareResult> {
  if (scopes.length !== 2 || scopes.some((entry) => isRecord(entry) === null)) {
    return { ok: false, error: { code: 'invalid_request', message: 'A scope comparison needs exactly two scopes.', hint: 'Send two session-grain scopes to compare.' } }
  }
  const sides = scopes.map((entry) => isRecord(entry) as Record<string, unknown>)
  if (sides.some((side) => side.grain !== undefined && side.grain !== 'session')) {
    return { ok: false, error: { code: 'unsupported_query', message: 'Comparison scopes must use the session grain.', hint: 'Use catalog grain session for both scopes.' } }
  }
  // Metrics and grouping are shared by the two sides: a side carrying its own
  // list that disagrees with the other side compares different quantities.
  for (const key of ['metrics', 'groupBy'] as const) {
    const first = key in sides[0] ? JSON.stringify(sides[0][key]) : undefined
    const second = key in sides[1] ? JSON.stringify(sides[1][key]) : undefined
    if (first !== second) {
      return { ok: false, error: { code: 'unsupported_query', message: 'Comparison sides must share metrics and grouping.', hint: 'Use identical metrics and grouping for both scopes.' } }
    }
  }
  const totals: Array<number | null> = []
  for (const side of sides) {
    const { grain: _grain, metrics: _metrics, groupBy: _groupBy, ...filters } = side
    const sideScope: AnalyticsScope = { ...scope, ...(filters as Partial<AnalyticsScope>) }
    const ids = await source.candidateSessionIds(sideScope, MAX_CANDIDATES + 1)
    if (ids.length > MAX_CANDIDATES) {
      return { ok: false, error: { code: 'budget_exceeded', message: 'A comparison side exceeds the session limit.', hint: 'Narrow the scope before comparing.' } }
    }
    const summaries = await source.readSessionSummaries(ids)
    const eligible = summaries.map((summary) => summary.inputTokens).filter((value): value is number => isFiniteNumber(value))
    totals.push(eligible.length === 0 ? null : eligible.reduce((sum, value) => sum + value, 0))
  }
  const [before, after] = totals
  return {
    ok: true,
    result: { deltas: { input_tokens: relativeDelta(before, after) } },
  }
}
