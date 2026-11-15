/**
 * Shared analytics shapes: operations, scopes, session summaries and the
 * narrow source seam every transport shares.
 *
 * <remarks>
 * Types only, plus the two tiny runtime guards every query module needs.
 * Absence stays absent here: every measured counter is `number | null`, so
 * a missing counter can never read as a measured zero downstream.
 * </remarks>
 */

export type AnalyticsOperation =
  | 'status'
  | 'catalog'
  | 'query'
  | 'search-sessions'
  | 'inspect'
  | 'findings'
  | 'compare'
  | 'outcomes'

export type AnalyticsScope = {
  from?: string
  to?: string
  timezone?: string
  grain?: string
  harness?: string
  repository?: string
  branch?: string
  runId?: string
  sessionId?: string
}

export type AnalyticsSessionSummary = {
  id: string
  runId?: string | null
  label?: string | null
  startedAt?: string | null
  harness?: string | null
  repository?: string | null
  branch?: string | null
  isSubagent?: boolean
  includedInParent?: boolean
  inputTokens?: number | null
  freshInputTokens?: number | null
  cacheReadTokens?: number | null
  cacheCreationTokens?: number | null
  outputTokens?: number | null
  durationMs?: number | null
  costs?: readonly { amount?: number | null; currency?: string | null; basis?: string | null; status?: string | null }[]
}

export type AnalyticsSource = {
  status(): Promise<unknown>
  readCatalog(): Promise<unknown>
  candidateSessionIds(scope: AnalyticsScope, cap: number): Promise<readonly string[]>
  readSessionSummaries(ids: readonly string[]): Promise<readonly AnalyticsSessionSummary[]>
  readSession(id: string): Promise<unknown | undefined>
  readTurnPage(request: { sessionId: string; turnIndex: number; blockId: string; byteLimit: number; cursor?: string }): Promise<unknown | undefined>
  /** Complete captured-context traversal is deliberately a separate bounded port. */
  readCapturedContext?(request: Record<string, unknown>, options?: ExecutionOptions): Promise<unknown>
  readFindings(scope: AnalyticsScope): Promise<readonly Record<string, unknown>[]>
  readRuns(scope: AnalyticsScope): Promise<readonly Record<string, unknown>[]>
  readOutcomes(scope: AnalyticsScope): Promise<readonly Record<string, unknown>[]>
  freshness(): Promise<Record<string, unknown>>
}

export type AnalyticsEnvelope = {
  contractVersion: 1
  scope: AnalyticsScope
  data: AnalyticsData | null
  measurement: Record<string, unknown>
  coverage: Record<string, unknown>
  freshness: Record<string, unknown>
  evidence: readonly Record<string, unknown>[]
  pagination: Record<string, unknown>
  error: { code: string; message: string; hint: string } | null
}

/** Common result shape; operation-specific keys are populated by `execute`. */
export type AnalyticsData = {
  groups: Array<{ key: string; contributorCount: number; metrics: Record<string, Record<string, unknown>> }>
  items: Array<Record<string, unknown>>
  deltas: Record<string, Record<string, unknown>>
  content: Record<string, unknown>
  [key: string]: unknown
}

export type ExecutionOptions = { signal?: AbortSignal }

export type OperationError = { code: string; message: string; hint: string }

/** A plain JSON object, or null when the value is anything else. */
export function isRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? value as Record<string, unknown> : null
}

/** A finite number, or null when the value is missing, unknown or unmeasurable. */
export function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value)
}
