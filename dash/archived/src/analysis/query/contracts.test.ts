import { describe, expect, it } from 'vitest'
import { makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('Analytics MCP operation envelopes', () => {
  it('returns the same versioned evidence envelope for status and catalog', async () => {
    const service = makeService()

    for (const operation of ['status', 'catalog'] as const) {
      const result = await service.execute(operation, {})

      expect(result.contractVersion).toBe(1)
      expect(result.scope).toBeDefined()
      expect(result.data).not.toBeNull()
      expect(result.measurement).toBeDefined()
      expect(result.coverage).toBeDefined()
      expect(result.freshness).toMatchObject({ generatedAt: expect.any(String) })
      expect(result.evidence).toBeDefined()
      expect(result.pagination).toBeDefined()
      expect(result.error).toBeNull()
    }
  })

  it('rejects unknown request keys before reading the canonical source', async () => {
    const source = makeAnalyticsSource()
    const service = makeService(source)

    const result = await service.execute('query', {
      catalogVersion: 1,
      metrics: ['input_tokens'],
      joins: ['records'],
    })

    expect(result.error).toMatchObject({ code: 'invalid_request' })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
    expect(source.readSessionSummaries).not.toHaveBeenCalled()
  })

  it('requires rediscovery when the catalog version is stale', async () => {
    const source = makeAnalyticsSource()
    const service = makeService(source)

    const result = await service.execute('query', {
      catalogVersion: 0,
      metrics: ['input_tokens'],
    })

    expect(result.error).toMatchObject({ code: 'unsupported_query' })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })
})
