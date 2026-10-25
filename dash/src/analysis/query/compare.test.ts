import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('like-for-like comparisons', () => {
  it('rejects comparisons whose scopes use different metric units or grain', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('compare', {
      scopes: [
        { from: '2026-10-01T00:00:00.000Z', to: '2026-10-08T00:00:00.000Z', grain: 'session' },
        { from: '2026-10-08T00:00:00.000Z', to: '2026-10-15T00:00:00.000Z', grain: 'turn' },
      ],
      metrics: ['input_tokens'],
    })

    expect(result.error).toMatchObject({ code: 'unsupported_query' })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })

  it('marks relative deltas unavailable for zero denominators and never claims causality', async () => {
    const source = makeAnalyticsSource({
      readRuns: async () => [
        { id: 'before', inputTokens: 0 },
        { id: 'after', inputTokens: 12 },
      ],
    })
    const result = await makeService(source).execute('compare', {
      runIds: ['before', 'after'],
      metrics: ['input_tokens'],
    })

    expect(dataOf(result).deltas.input_tokens).toMatchObject({ relative: null, reason: expect.any(String) })
    expect(JSON.stringify(dataOf(result)).toLowerCase()).not.toContain('caused')
  })

  it('requires exactly two run ids instead of defaulting to the first runs found', async () => {
    // WHY no default pair: the run-compare design keeps the selected pair
    // manual-only (issue #190) — an empty or misspelled request must fail
    // loudly, never silently compare whatever the store listed first.
    for (const request of [{}, { runIds: [] }, { runIds: ['only-one'] }, { runIds: ['a', 'b', 'c'] }]) {
      const source = makeAnalyticsSource({
        readRuns: async () => [
          { id: 'before', inputTokens: 10 },
          { id: 'after', inputTokens: 12 },
        ],
      })
      const result = await makeService(source).execute('compare', request)

      expect(result.error, JSON.stringify(request)).toMatchObject({ code: 'invalid_request' })
      expect(source.readRuns).not.toHaveBeenCalled()
    }
  })

  it('answers not_found for a run id the inventory does not resolve', async () => {
    const source = makeAnalyticsSource({
      readRuns: async () => [
        { id: 'before', inputTokens: 10 },
        { id: 'after', inputTokens: 12 },
      ],
    })
    const result = await makeService(source).execute('compare', {
      runIds: ['before', 'misspelled'],
      metrics: ['input_tokens'],
    })

    expect(result.error).toMatchObject({ code: 'not_found' })
    expect(JSON.stringify(result.error)).toContain('misspelled')
  })
})
