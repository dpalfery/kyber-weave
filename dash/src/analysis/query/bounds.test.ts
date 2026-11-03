import { describe, expect, it, vi } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService, makeSession } from './fixtures/analytics-source.js'

describe('query admission and paging bounds', () => {
  it('refuses an over-limit candidate set without returning partial totals', async () => {
    const ids = Array.from({ length: 10_001 }, (_, index) => `session-${index}`)
    const source = makeAnalyticsSource({ candidateSessionIds: async (_scope: unknown, cap: number) => ids.slice(0, cap) })
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    })

    expect(result.error).toMatchObject({ code: 'budget_exceeded' })
    expect(result.data).toBeNull()
    expect(source.candidateSessionIds).toHaveBeenCalledWith(expect.anything(), 10_001)
    expect(source.readSessionSummaries).not.toHaveBeenCalled()
  })

  it('reads large candidate scopes in batches of at most one hundred', async () => {
    const ids = Array.from({ length: 201 }, (_, index) => `session-${index}`)
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ids,
      readSessionSummaries: async (batch: readonly string[]) => batch.map((id) => makeSession({ id, inputTokens: 1 })),
    })
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    })

    expect(dataOf(result).groups[0].metrics.input_tokens.value).toBe(201)
    expect(source.readSessionSummaries.mock.calls.map(([batch]) => batch.length)).toEqual([100, 100, 1])
  })

  it('returns no partial aggregate when cancellation occurs between batches and remains usable', async () => {
    const controller = new AbortController()
    const ids = Array.from({ length: 101 }, (_, index) => `session-${index}`)
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ids,
      readSessionSummaries: vi.fn(async (batch: readonly string[]) => {
        if (batch[0] === 'session-100') controller.abort()
        return batch.map((id) => makeSession({ id, inputTokens: 1 }))
      }),
    })
    const service = makeService(source)
    const request = {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    }
    const cancelled = await service.execute('query', request, { signal: controller.signal })
    const next = await service.execute('status', {})

    expect(cancelled.error).toMatchObject({ code: 'cancelled' })
    expect(cancelled.data).toBeNull()
    expect(next.error).toBeNull()
  })

  it('rejects a cursor after projection generation changes', async () => {
    const source = makeAnalyticsSource()
    const service = makeService(source)
    const cursor = 'opaque-cursor-from-prior-generation'
    const result = await service.execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
      cursor,
    })

    expect(result.error).toMatchObject({ code: 'stale_cursor' })
  })

  it('defaults an absent scope to the previous seven days from the engine clock', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
    })

    expect(result.error).toBeNull()
    expect(result.scope).toMatchObject({
      from: '2026-10-17T12:00:00.000Z',
      to: '2026-10-24T12:00:00.000Z',
      timezone: 'UTC',
    })
    expect(source.candidateSessionIds).toHaveBeenCalledWith(
      expect.objectContaining({ from: '2026-10-17T12:00:00.000Z', to: '2026-10-24T12:00:00.000Z' }),
      expect.any(Number),
    )
  })

  it('rejects a request selecting more than eight metrics', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: Array.from({ length: 9 }, () => ({ id: 'input_tokens', operator: 'sum' })),
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    })

    expect(result.error).toMatchObject({ code: 'invalid_request' })
    expect(result.data).toBeNull()
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })

  it('rejects a request grouping by more than two dimensions', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      groupBy: ['harness', 'repository', 'branch'],
      scope: { from: '2026-10-17T00:00:00.000Z', to: '2026-10-24T00:00:00.000Z', timezone: 'UTC' },
    })

    expect(result.error).toMatchObject({ code: 'unsupported_query' })
    expect(result.data).toBeNull()
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })

  it('rejects a malformed time range before reading the canonical source', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('query', {
      catalogVersion: 1,
      metrics: [{ id: 'input_tokens', operator: 'sum' }],
      scope: { from: '2026-10-24T00:00:00.000Z', to: '2026-10-17T00:00:00.000Z', timezone: 'UTC' },
    })

    expect(result.error).toMatchObject({ code: 'invalid_request' })
    expect(result.data).toBeNull()
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })
})
