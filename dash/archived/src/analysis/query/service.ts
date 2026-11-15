/**
 * Bounded, read-only analytics over canonical session summaries.
 *
 * The service deliberately accepts a narrow source rather than a database handle: this
 * keeps request validation and aggregation shared by every transport, while the source
 * remains responsible for using the projection's materialized readers.
 */

import { executeQuery } from './aggregate.js'
import { catalogDocument } from './catalog.js'
import { executeCompare } from './compare.js'
import { executeFindings } from './findings.js'
import { executeInspect } from './inspect.js'
import { executeOutcomes } from './outcomes.js'
import { defaultScope, parseScope } from './scope.js'
import { executeSearch } from './search.js'
import {
  isRecord,
  type AnalyticsEnvelope,
  type AnalyticsOperation,
  type AnalyticsScope,
  type AnalyticsSource,
  type ExecutionOptions,
} from './types.js'

export type {
  AnalyticsData,
  AnalyticsEnvelope,
  AnalyticsOperation,
  AnalyticsScope,
  AnalyticsSessionSummary,
  AnalyticsSource,
  ExecutionOptions,
} from './types.js'

type ServiceOptions = { source: AnalyticsSource; clock: () => Date; identity: { instanceId: string; projectionGeneration: string | (() => string) } }

/** The operation discriminator determines which populated data member applies. */
function analyticsData(value: Record<string, unknown>): AnalyticsEnvelope['data'] {
  return value as AnalyticsEnvelope['data']
}

function errorEnvelope(scope: AnalyticsScope, freshness: Record<string, unknown>, code: string, message: string, hint: string): AnalyticsEnvelope {
  return { contractVersion: 1, scope, data: null, measurement: {}, coverage: {}, freshness, evidence: [], pagination: {}, error: { code, message, hint } }
}

export function createAnalyticsService(options: ServiceOptions): { execute(operation: AnalyticsOperation, request: unknown, options?: ExecutionOptions): Promise<AnalyticsEnvelope> } {
  const projectionGeneration = (): string => typeof options.identity.projectionGeneration === 'function' ? options.identity.projectionGeneration() : options.identity.projectionGeneration
  const freshness = async (): Promise<Record<string, unknown>> => ({ generatedAt: options.clock().toISOString(), instanceId: options.identity.instanceId, projectionGeneration: projectionGeneration(), ...(await options.source.freshness()) })
  const execute = async (operation: AnalyticsOperation, request: unknown, execution?: ExecutionOptions): Promise<AnalyticsEnvelope> => {
    let currentFreshness: Record<string, unknown>
    try {
      currentFreshness = await freshness()
    } catch {
      currentFreshness = { generatedAt: options.clock().toISOString(), instanceId: options.identity.instanceId, projectionGeneration: projectionGeneration(), recordedAt: null, reason: 'Freshness metadata is unavailable.' }
    }
    const raw = isRecord(request)
    const fallbackScope = defaultScope(options.clock)
    if (execution?.signal?.aborted) return errorEnvelope(fallbackScope, currentFreshness, 'cancelled', 'The analytics request was cancelled.', 'Retry the complete request.')
    if (raw === null) return errorEnvelope(fallbackScope, currentFreshness, 'invalid_request', 'The request must be an object.', 'Send a JSON object matching the operation catalog.')
    // An explicit scope the caller wrote down must parse: a malformed range
    // answers invalid_request here, before any canonical read, and never
    // falls back to the default window.
    const parsed = parseScope(raw.scope, options.clock)
    if (!parsed.ok) return errorEnvelope(fallbackScope, currentFreshness, parsed.error.code, parsed.error.message, parsed.error.hint)
    const scope = parsed.scope
    try {
      if (operation === 'status') return { contractVersion: 1, scope, data: analyticsData({ value: await options.source.status() }), measurement: {}, coverage: {}, freshness: currentFreshness, evidence: [], pagination: {}, error: null }
      if (operation === 'catalog') return { contractVersion: 1, scope, data: analyticsData({ ...catalogDocument(), source: await options.source.readCatalog() }), measurement: {}, coverage: {}, freshness: currentFreshness, evidence: [], pagination: {}, error: null }
      if (operation === 'findings') {
        const found = await executeFindings(options.source, scope, raw)
        return { contractVersion: 1, scope, data: analyticsData({ items: found.items }), measurement: {}, coverage: found.coverage, freshness: currentFreshness, evidence: found.evidence, pagination: found.pagination, error: null }
      }
      if (operation === 'outcomes') {
        const outcome = await executeOutcomes(options.source, scope, raw)
        return { contractVersion: 1, scope, data: analyticsData({ items: outcome.items }), measurement: {}, coverage: outcome.coverage, freshness: currentFreshness, evidence: [], pagination: { returnedCount: outcome.items.length, nextCursor: null }, error: null }
      }
      if (operation === 'compare') {
        const compared = await executeCompare(options.source, scope, raw)
        if (!compared.ok) return errorEnvelope(scope, currentFreshness, compared.error.code, compared.error.message, compared.error.hint)
        return { contractVersion: 1, scope, data: analyticsData({ deltas: compared.result.deltas }), measurement: {}, coverage: {}, freshness: currentFreshness, evidence: [], pagination: {}, error: null }
      }
      if (operation === 'inspect') {
        const inspected = await executeInspect(options.source, raw, execution)
        if (!inspected.ok) return errorEnvelope(scope, currentFreshness, inspected.error.code, inspected.error.message, inspected.error.hint)
        return {
          contractVersion: 1,
          scope,
          data: analyticsData(inspected.result.data),
          measurement: {},
          coverage: inspected.result.coverage ?? {},
          freshness: currentFreshness,
          evidence: inspected.result.evidence,
          pagination: inspected.result.pagination ?? {},
          error: inspected.result.error,
        }
      }
      if (operation === 'search-sessions') {
        const searched = await executeSearch(options.source, scope, raw)
        if (!searched.ok) return errorEnvelope(scope, currentFreshness, searched.error.code, searched.error.message, searched.error.hint)
        return { contractVersion: 1, scope, data: analyticsData({ items: searched.result.items }), measurement: {}, coverage: searched.result.coverage, freshness: currentFreshness, evidence: searched.result.evidence, pagination: searched.result.pagination, error: null }
      }
      if (operation !== 'query') return errorEnvelope(scope, currentFreshness, 'unsupported_query', 'The requested operation is unsupported.', 'Rediscover the analytics catalog.')
      const queried = await executeQuery(options.source, scope, raw, execution)
      if (!queried.ok) return errorEnvelope(scope, currentFreshness, queried.error.code, queried.error.message, queried.error.hint)
      return { contractVersion: 1, scope, data: analyticsData({ groups: queried.result.groups }), measurement: {}, coverage: queried.result.coverage, freshness: currentFreshness, evidence: queried.result.evidence, pagination: queried.result.pagination, error: null }
    } catch (cause: unknown) {
      return errorEnvelope(scope, currentFreshness, 'internal_error', 'The analytics engine could not complete the request.', cause instanceof Error ? 'Inspect server diagnostics and retry.' : 'Retry the request.')
    }
  }
  return { execute }
}
