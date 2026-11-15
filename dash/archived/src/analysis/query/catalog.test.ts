import { describe, expect, it } from 'vitest'
import { makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('analytics catalog', () => {
  it('publishes authoritative grains, permitted dimensions and bounded query limits', async () => {
    const result = await makeService().execute('catalog', {})

    expect(result.data).toMatchObject({
      version: 1,
      grains: ['session'],
      limits: {
        defaultDays: 7,
        maxDays: 365,
        maxRows: 100,
        maxMetrics: 8,
        maxDimensions: 2,
        maxCandidates: 10_000,
        maxReplyBytes: 65_536,
        maxContentPageBytes: 16_384,
      },
    })
    expect(result.data).toMatchObject({ dimensions: expect.arrayContaining([
      expect.objectContaining({ id: 'harness' }),
      expect.objectContaining({ id: 'repository' }),
      expect.objectContaining({ id: 'branch' }),
      expect.objectContaining({ id: 'is_subagent' }),
      expect.objectContaining({ id: 'started_at_day' }),
      expect.objectContaining({ id: 'started_at_week' }),
    ]) })
    expect(result.data).not.toMatchObject({ dimensions: expect.arrayContaining([
      expect.objectContaining({ id: 'model' }),
      expect.objectContaining({ id: 'tool' }),
      expect.objectContaining({ id: 'mcp_server' }),
      expect.objectContaining({ id: 'skill' }),
    ]) })
  })

  it('has a catalog-version mismatch error that names rediscovery as the remedy', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('query', { catalogVersion: 99, metrics: ['input_tokens'] })

    expect(result.error).toMatchObject({ code: 'unsupported_query', hint: expect.stringMatching(/catalog|rediscover/i) })
  })
})
