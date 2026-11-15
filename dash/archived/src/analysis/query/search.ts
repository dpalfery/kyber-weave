/**
 * Literal metadata search over session summaries (`search-sessions`).
 *
 * <remarks>
 * The phrase matches identity metadata only — label, harness, repository and
 * branch — never conversation content, so a search can never disclose
 * transcript text. At most one summary batch is read; the default
 * returns twenty-five metadata rows.
 * </remarks>
 */

import { MAX_CANDIDATES, SUMMARY_BATCH } from './catalog.js'
import { isFiniteNumber, type AnalyticsScope, type AnalyticsSource, type OperationError } from './types.js'

export type SearchSuccess = {
  items: Array<Record<string, unknown>>
  coverage: Record<string, unknown>
  evidence: Array<Record<string, unknown>>
  pagination: Record<string, unknown>
}

export type SearchResult =
  | { ok: true; result: SearchSuccess }
  | { ok: false; error: OperationError }

export async function executeSearch(
  source: AnalyticsSource,
  scope: AnalyticsScope,
  raw: Record<string, unknown>,
): Promise<SearchResult> {
  if (typeof raw.query !== 'string' || raw.query.length === 0 || raw.query.length > 200) {
    return { ok: false, error: { code: 'invalid_request', message: 'Search text must be between 1 and 200 characters.', hint: 'Use a shorter literal metadata phrase.' } }
  }
  const ids = await source.candidateSessionIds(scope, MAX_CANDIDATES + 1)
  if (ids.length > MAX_CANDIDATES) {
    return { ok: false, error: { code: 'budget_exceeded', message: 'The search scope exceeds the session limit.', hint: 'Narrow the scope before searching.' } }
  }
  const summaries = await source.readSessionSummaries(ids.slice(0, SUMMARY_BATCH))
  const phrase = raw.query.toLowerCase()
  const limit = Math.min(isFiniteNumber(raw.limit) ? Math.max(1, Math.floor(raw.limit)) : 25, 100)
  const matched = summaries.flatMap((summary) => {
    const fields: Array<[string, unknown]> = [
      ['label', summary.label],
      ['harness', summary.harness],
      ['repository', summary.repository],
      ['branch', summary.branch],
    ]
    const field = fields.find(([, value]) => typeof value === 'string' && value.toLowerCase().includes(phrase))
    return field === undefined ? [] : [{ summary, field: field[0] as string }]
  })
  const page = matched.slice(0, limit)
  return {
    ok: true,
    result: {
      items: page.map(({ summary }) => summary) as unknown as Array<Record<string, unknown>>,
      coverage: { candidateCount: ids.length, includedCount: summaries.length },
      evidence: page.map(({ summary, field }) => ({ sessionId: summary.id, selection: `${field} literal match` })),
      pagination: { limit, returnedCount: page.length, nextCursor: null },
    },
  }
}
