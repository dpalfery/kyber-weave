import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService, makeSession } from './fixtures/analytics-source.js'

const scope = {
  from: '2026-10-17T00:00:00.000Z',
  to: '2026-10-24T00:00:00.000Z',
  timezone: 'UTC',
}

function queryRequest(overrides: Record<string, unknown> = {}) {
  return {
    catalogVersion: 1,
    scope,
    metrics: [{ id: 'input_tokens', operator: 'sum' }],
    groupBy: ['harness'],
    ...overrides,
  }
}

describe('bounded session analytics', () => {
  it('keeps measured zero eligible while reporting a missing counter as unavailable', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['zero-measured', 'missing-counters'],
      readSessionSummaries: async () => [
        makeSession({ id: 'zero-measured', inputTokens: 0 }),
        makeSession({ id: 'missing-counters', inputTokens: null }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest())

    expect(result.error).toBeNull()
    expect(dataOf(result).groups[0]).toMatchObject({
      contributorCount: 2,
      metrics: { input_tokens: { value: 0, eligibleCount: 1, unavailableCount: 1 } },
    })
    expect(result.coverage).toMatchObject({ metrics: { input_tokens: { candidateCount: 2, includedCount: 2, eligibleCount: 1 } } })
  })

  it('computes a ratio from summed eligible numerators and denominators', async () => {
    const source = makeAnalyticsSource({
      readSessionSummaries: async () => [
        makeSession({ id: 'ratio-a', cacheReadTokens: 9, inputTokens: 10 }),
        makeSession({ id: 'ratio-b', cacheReadTokens: 1, inputTokens: 10 }),
      ],
    })
    const result = await makeService(source).execute(
      'query',
      queryRequest({ metrics: [{ id: 'cache_read_ratio', operator: 'ratio' }], groupBy: [] }),
    )

    expect(dataOf(result).groups[0].metrics.cache_read_ratio).toMatchObject({
      value: 0.5,
      numerator: 10,
      denominator: 20,
    })
  })

  it('keeps cost totals partitioned by both currency and pricing basis', async () => {
    const source = makeAnalyticsSource({
      readSessionSummaries: async () => [
        makeSession({ id: 'cost-a', costs: [{ amount: 2, currency: 'USD', basis: 'published-table', status: 'measured' }] }),
        makeSession({ id: 'cost-b', costs: [{ amount: 3, currency: 'USD', basis: 'harness-reported', status: 'measured' }] }),
        makeSession({ id: 'cost-c', costs: [{ amount: 4, currency: 'EUR', basis: 'published-table', status: 'measured' }] }),
      ],
    })
    const result = await makeService(source).execute(
      'query',
      queryRequest({ metrics: [{ id: 'cost', operator: 'sum' }], groupBy: [] }),
    )

    expect(dataOf(result).groups[0].metrics.cost.partitions).toEqual([
      expect.objectContaining({ currency: 'EUR', basis: 'published-table', value: 4 }),
      expect.objectContaining({ currency: 'USD', basis: 'harness-reported', value: 3 }),
      expect.objectContaining({ currency: 'USD', basis: 'published-table', value: 2 }),
    ])
  })

  it('does not count a child twice when its summary is already included in the parent', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['parent', 'parent#child'],
      readSessionSummaries: async () => [
        makeSession({ id: 'parent', inputTokens: 30 }),
        makeSession({ id: 'parent#child', inputTokens: 10, isSubagent: true, includedInParent: true }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest({ groupBy: [] }))

    expect(dataOf(result).groups[0].metrics.input_tokens.value).toBe(30)
    expect(result.coverage.exclusions).toContainEqual(expect.objectContaining({ reason: 'included_in_parent', sessionId: 'parent#child' }))
  })

  it('excludes unknown timestamps from a time-scoped result and counts them', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['dated', 'unknown-start'],
      readSessionSummaries: async () => [
        makeSession({ id: 'dated', inputTokens: 5 }),
        makeSession({ id: 'unknown-start', startedAt: null, inputTokens: 99 }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest({ groupBy: [] }))

    expect(dataOf(result).groups[0].metrics.input_tokens.value).toBe(5)
    expect(result.coverage.unknownCounts).toMatchObject({ timestamp: 1 })
  })

  it('rejects unsupported grouping dimensions before reading summaries', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute(
      'query',
      queryRequest({ groupBy: ['tool_name'] }),
    )

    expect(result.error).toMatchObject({ code: 'unsupported_query', hint: expect.stringContaining('inspect') })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })

  it('reports exact scoped totals with per-metric eligibility denominators', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['exact-a', 'exact-b', 'exact-missing'],
      readSessionSummaries: async () => [
        makeSession({ id: 'exact-a', inputTokens: 3 }),
        makeSession({ id: 'exact-b', inputTokens: 4 }),
        makeSession({ id: 'exact-missing', inputTokens: null }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest({ groupBy: [] }))

    expect(result.error).toBeNull()
    expect(dataOf(result).groups).toHaveLength(1)
    expect(dataOf(result).groups[0]).toMatchObject({
      contributorCount: 3,
      metrics: { input_tokens: { value: 7, eligibleCount: 2, unavailableCount: 1 } },
    })
  })

  it('supports min, max and session count operators over the admitted scope', async () => {
    const source = makeAnalyticsSource({
      readSessionSummaries: async () => [
        makeSession({ id: 'op-a', inputTokens: 3 }),
        makeSession({ id: 'op-b', inputTokens: 9 }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest({
      metrics: [
        { id: 'session_count', operator: 'count' },
        { id: 'input_tokens', operator: 'min' },
      ],
      groupBy: [],
    }))

    expect(dataOf(result).groups[0].metrics).toMatchObject({
      session_count: { value: 2 },
      input_tokens: { value: 3 },
    })
  })

  it('orders groups deterministically by group key', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['zebra-session', 'alpha-session'],
      readSessionSummaries: async () => [
        makeSession({ id: 'zebra-session', harness: 'zebra-harness', inputTokens: 1 }),
        makeSession({ id: 'alpha-session', harness: 'alpha-harness', inputTokens: 2 }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest())

    expect(dataOf(result).groups.map((group) => group.key)).toEqual(['alpha-harness', 'zebra-harness'])
    expect(result.pagination).toMatchObject({ ordering: 'group-key asc' })
  })

  it('groups subagent sessions separately from their parents', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['parent-session', 'child-session'],
      readSessionSummaries: async () => [
        makeSession({ id: 'parent-session', inputTokens: 30 }),
        makeSession({ id: 'child-session', inputTokens: 10, isSubagent: true }),
      ],
    })
    const result = await makeService(source).execute('query', queryRequest({ groupBy: ['is_subagent'] }))

    expect(dataOf(result).groups.map((group) => group.key).sort()).toEqual(['false', 'true'])
    expect(dataOf(result).groups).toHaveLength(2)
  })

  it('bounds aggregate evidence to a deterministic contributor sample', async () => {
    const ids = Array.from({ length: 30 }, (_, index) => `session-${index}`)
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ids,
      readSessionSummaries: async (batch: readonly string[]) => batch.map((id) => makeSession({ id, inputTokens: 1 })),
    })
    const result = await makeService(source).execute('query', queryRequest({ groupBy: [] }))

    expect(dataOf(result).groups[0].metrics.input_tokens.value).toBe(30)
    expect(result.evidence).toHaveLength(25)
  })
})
