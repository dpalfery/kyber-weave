import { vi } from 'vitest'
import { createAnalyticsService, type AnalyticsData, type AnalyticsEnvelope, type AnalyticsSessionSummary, type AnalyticsSource } from '../service.js'

type AnalyticsSourceOverrides = Partial<AnalyticsSource>

/** Small canonical-projection fixtures; no test source exposes raw database rows. */
export function makeSession(overrides: Partial<AnalyticsSessionSummary> = {}): AnalyticsSessionSummary {
  return {
    id: 'session-a',
    runId: 'run-a',
    startedAt: '2026-10-20T10:00:00.000Z',
    harness: 'claude-code',
    repository: 'kyber-weave',
    branch: 'main',
    isSubagent: false,
    inputTokens: 0,
    freshInputTokens: 0,
    cacheReadTokens: 0,
    cacheCreationTokens: 0,
    outputTokens: 0,
    durationMs: 0,
    costs: [{ amount: 0, currency: 'USD', basis: 'published-table', status: 'measured' }],
    ...overrides,
  }
}

export function makeAnalyticsSource(overrides: AnalyticsSourceOverrides = {}) {
  const source = {
    status: vi.fn<AnalyticsSource['status']>(async () => ({ capabilities: ['query'], engineVersion: 'test' })),
    readCatalog: vi.fn<AnalyticsSource['readCatalog']>(async () => ({
      version: 1,
      metrics: [
        { id: 'session_count', unit: 'sessions', operators: ['count'] },
        { id: 'input_tokens', unit: 'tokens', operators: ['sum', 'min', 'max'] },
        { id: 'output_tokens', unit: 'tokens', operators: ['sum', 'min', 'max'] },
        { id: 'duration_ms', unit: 'milliseconds', operators: ['sum', 'min', 'max'] },
        { id: 'cost', unit: 'currency', operators: ['sum'], partitionBy: ['basis', 'currency', 'status'] },
        { id: 'cache_read_ratio', unit: 'ratio', operators: ['ratio'] },
      ],
      dimensions: ['harness', 'repository', 'branch', 'is_subagent', 'started_at_day', 'started_at_week'],
      limits: { defaultDays: 7, maxDays: 365, maxRows: 100, maxMetrics: 8, maxDimensions: 2 },
    })),
    candidateSessionIds: vi.fn<AnalyticsSource['candidateSessionIds']>(async (_scope, cap) => ['session-a'].slice(0, cap)),
    readSessionSummaries: vi.fn<AnalyticsSource['readSessionSummaries']>(async (_ids) => [makeSession()]),
    readSession: vi.fn<AnalyticsSource['readSession']>(async (_id) => undefined),
    readTurnPage: vi.fn<AnalyticsSource['readTurnPage']>(async (_request) => undefined),
    readFindings: vi.fn<AnalyticsSource['readFindings']>(async (_scope) => []),
    readRuns: vi.fn<AnalyticsSource['readRuns']>(async (_scope) => []),
    readOutcomes: vi.fn<AnalyticsSource['readOutcomes']>(async (_scope) => []),
    freshness: vi.fn<AnalyticsSource['freshness']>(async () => ({
      recordedAt: null,
      ingestAt: null,
      projectionAt: null,
      storeGeneration: 'generation-1',
    })),
  } satisfies AnalyticsSource

  if (overrides.status !== undefined) source.status.mockImplementation(overrides.status)
  if (overrides.readCatalog !== undefined) source.readCatalog.mockImplementation(overrides.readCatalog)
  if (overrides.candidateSessionIds !== undefined) source.candidateSessionIds.mockImplementation(overrides.candidateSessionIds)
  if (overrides.readSessionSummaries !== undefined) source.readSessionSummaries.mockImplementation(overrides.readSessionSummaries)
  if (overrides.readSession !== undefined) source.readSession.mockImplementation(overrides.readSession)
  if (overrides.readTurnPage !== undefined) source.readTurnPage.mockImplementation(overrides.readTurnPage)
  if (overrides.readFindings !== undefined) source.readFindings.mockImplementation(overrides.readFindings)
  if (overrides.readRuns !== undefined) source.readRuns.mockImplementation(overrides.readRuns)
  if (overrides.readOutcomes !== undefined) source.readOutcomes.mockImplementation(overrides.readOutcomes)
  if (overrides.freshness !== undefined) source.freshness.mockImplementation(overrides.freshness)

  return source
}

export function makeService(source = makeAnalyticsSource()) {
  return createAnalyticsService({
    source,
    clock: () => new Date('2026-10-24T12:00:00.000Z'),
    identity: { instanceId: 'instance-1', projectionGeneration: 'generation-1' },
  })
}

export function dataOf(result: AnalyticsEnvelope): AnalyticsData {
  if (result.error !== null || result.data === null) {
    throw new Error(`Expected a successful analytics response, received ${result.error?.code ?? 'null data'}.`)
  }
  return result.data
}

