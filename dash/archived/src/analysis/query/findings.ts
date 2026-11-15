/**
 * Canonical findings exposure (`findings`): deterministic findings with
 * their mechanism, confidence, risk caveat, rank and evidence references.
 *
 * <remarks>
 * Model review output never rides along: it is advisory text about a
 * finding, not the finding itself, and promoting it would confuse an
 * observation with its review.
 * </remarks>
 */

import { isFiniteNumber, type AnalyticsScope, type AnalyticsSource } from './types.js'

export type FindingsSuccess = {
  items: Array<Record<string, unknown>>
  coverage: Record<string, unknown>
  evidence: Array<Record<string, unknown>>
  pagination: Record<string, unknown>
}

export async function executeFindings(
  source: AnalyticsSource,
  scope: AnalyticsScope,
  raw: Record<string, unknown> = {},
): Promise<FindingsSuccess> {
  const all = (await source.readFindings(scope)).map(({ modelReview: _review, ...finding }) => finding)
  const detector = typeof raw.detector === 'string' ? raw.detector : undefined
  const confidence = typeof raw.confidence === 'string' ? raw.confidence : undefined
  const filtered = all.filter((finding) => {
    if (detector !== undefined && finding.detectorId !== detector && finding.detector !== detector) return false
    if (confidence !== undefined && finding.confidence !== confidence) return false
    return true
  })
  const limit = Math.min(isFiniteNumber(raw.limit) ? Math.max(1, Math.floor(raw.limit)) : 100, 100)
  const cursor = typeof raw.cursor === 'string' ? raw.cursor : typeof raw.offset === 'number' ? String(raw.offset) : undefined
  const parsed = cursor === undefined ? 0 : Number.parseInt(cursor, 10)
  const offset = Number.isSafeInteger(parsed) && (parsed as number) >= 0 ? Math.min(parsed as number, filtered.length) : 0
  const items = filtered.slice(offset, offset + limit)
  return {
    items,
    coverage: { includedCount: filtered.length },
    evidence: items.filter((f) => typeof f.id === 'string').map((f) => ({ findingId: f.id as string })),
    pagination: { limit, returnedCount: items.length, nextCursor: offset + limit < filtered.length ? String(offset + limit) : null },
  }
}
