import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('canonical findings exposure', () => {
  it('preserves existing mechanism, confidence, risk caveat, rank and evidence references', async () => {
    const finding = {
      id: 'finding-1',
      mechanism: 'Repeated schema loading was observed.',
      confidence: 'high',
      outcomeRiskCaveat: 'Review before changing instructions.',
      rankScore: 12,
      evidenceLinks: [{ sessionId: 'session-a', turnIndex: 1 }, { sessionId: 'session-b', turnIndex: 2 }],
      measurementClass: 'deterministic',
    }
    const source = makeAnalyticsSource({ readFindings: async () => [finding] })
    const result = await makeService(source).execute('findings', {})

    expect(dataOf(result).items[0]).toEqual(expect.objectContaining(finding))
    expect(result.evidence).toContainEqual(expect.objectContaining({ findingId: 'finding-1' }))
  })

  it('keeps model review output separate from canonical findings', async () => {
    const source = makeAnalyticsSource({
      readFindings: async () => [{ id: 'deterministic-finding', confidence: 'medium', modelReview: 'Should not be promoted.' }],
    })
    const result = await makeService(source).execute('findings', {})

    expect(dataOf(result).items.map((item) => item.id)).toEqual(['deterministic-finding'])
    expect(JSON.stringify(dataOf(result))).not.toContain('Should not be promoted.')
  })
})
