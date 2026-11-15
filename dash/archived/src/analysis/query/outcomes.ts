/**
 * Observed run outcomes (`outcomes`): sparse and conflicting evidence
 * reported as recorded, without manufacturing success.
 *
 * <remarks>
 * A run without an observed outcome stays absent — neither successful nor
 * failed — and counts against availability so a thin evidence base cannot
 * read as a clean bill of health.
 * </remarks>
 */

import type { AnalyticsScope, AnalyticsSource } from './types.js'

export type OutcomesSuccess = {
  items: Array<Record<string, unknown>>
  coverage: Record<string, unknown>
}

export async function executeOutcomes(
  source: AnalyticsSource,
  scope: AnalyticsScope,
  raw: Record<string, unknown>,
): Promise<OutcomesSuccess> {
  const runIds = Array.isArray(raw.runIds) ? raw.runIds.filter((id): id is string => typeof id === 'string') : []
  const items = await source.readOutcomes({ ...scope, ...(runIds.length > 0 ? { runId: runIds.join(',') } : {}) })
  const unavailableCount = Math.max(0, runIds.length - items.length) + items.filter((item) => item.status === 'not_measurable').length
  return {
    items: [...items],
    coverage: { eligibleCount: items.length - items.filter((item) => item.status === 'not_measurable').length, unavailableCount },
  }
}
