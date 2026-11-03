/**
 * The published analytics catalog: authoritative grains, permitted
 * dimensions, metric ids with their operators, and the bounded query
 * limits. The source-to-metric mapping lives here so every transport
 * rediscovers the same contract from `dash_catalog`.
 *
 * <remarks>
 * Model, tool, MCP server and skill detail stay out of the dimensions on
 * purpose: per-model/tool drill-down reads through `dash_inspect`, never
 * through a grouped aggregate.
 * </remarks>
 */

export const CATALOG_VERSION = 1 as const
export const MAX_CANDIDATES = 10_000
export const SUMMARY_BATCH = 100
export const MAX_CONTENT_BYTES = 16_384
export const MAX_REPLY_BYTES = 65_536
export const DEFAULT_DAYS = 7
export const MAX_DAYS = 365
export const MAX_ROWS = 100
export const MAX_METRICS = 8
export const MAX_DIMENSIONS = 2

export const DIMENSIONS = ['harness', 'repository', 'branch', 'is_subagent', 'started_at_day', 'started_at_week'] as const
export type AnalyticsDimension = (typeof DIMENSIONS)[number]

export const METRICS = ['session_count', 'input_tokens', 'fresh_input_tokens', 'cache_read_tokens', 'cache_creation_tokens', 'output_tokens', 'duration_ms', 'cost', 'cache_read_ratio'] as const
export type AnalyticsMetricId = (typeof METRICS)[number]

export function isDimensionId(value: unknown): value is AnalyticsDimension {
  return typeof value === 'string' && (DIMENSIONS as readonly string[]).includes(value)
}

export function isMetricId(value: unknown): value is AnalyticsMetricId {
  return typeof value === 'string' && (METRICS as readonly string[]).includes(value)
}

/** Operators each metric supports, as published by `dash_catalog`. */
export function metricOperators(id: AnalyticsMetricId): readonly string[] {
  if (id === 'session_count') return ['count']
  if (id === 'cache_read_ratio') return ['ratio']
  return ['sum', 'min', 'max']
}

/** The versioned catalog document, before the source merges its own entry. */
export function catalogDocument(): Record<string, unknown> {
  return {
    version: CATALOG_VERSION,
    grains: ['session'],
    metrics: METRICS.map((id) => ({ id, operators: metricOperators(id) })),
    dimensions: DIMENSIONS.map((id) => ({ id })),
    limits: {
      defaultDays: DEFAULT_DAYS,
      maxDays: MAX_DAYS,
      maxRows: MAX_ROWS,
      maxMetrics: MAX_METRICS,
      maxDimensions: MAX_DIMENSIONS,
      maxCandidates: MAX_CANDIDATES,
      maxReplyBytes: MAX_REPLY_BYTES,
      maxContentPageBytes: MAX_CONTENT_BYTES,
    },
  }
}
