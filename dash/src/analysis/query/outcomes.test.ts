import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('observed run outcomes', () => {
  it('reports sparse and conflicting evidence without manufacturing success', async () => {
    const source = makeAnalyticsSource({
      readOutcomes: async () => [
        { runId: 'run-no-signals', status: 'not_measurable', reason: 'No outcome signals were recorded.' },
        { runId: 'run-conflict', status: 'inconclusive', signals: ['test_passed', 'correction_required'] },
      ],
    })
    const result = await makeService(source).execute('outcomes', { runIds: ['run-no-signals', 'run-conflict'] })

    expect(dataOf(result).items).toEqual([
      expect.objectContaining({ runId: 'run-no-signals', status: 'not_measurable' }),
      expect.objectContaining({ runId: 'run-conflict', status: 'inconclusive' }),
    ])
    expect(JSON.stringify(dataOf(result))).not.toContain('successful')
    expect(result.coverage).toMatchObject({ eligibleCount: 1, unavailableCount: 1 })
  })

  it('does not convert an absent outcome into a successful run', async () => {
    const source = makeAnalyticsSource({ readOutcomes: async () => [] })
    const result = await makeService(source).execute('outcomes', { runIds: ['run-with-no-outcome'] })

    expect(dataOf(result).items).toEqual([])
    expect(result.coverage.unavailableCount).toBeGreaterThan(0)
  })
})
