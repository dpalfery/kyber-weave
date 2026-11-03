import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService, makeSession } from './fixtures/analytics-source.js'

describe('session metadata search', () => {
  it('treats a query as literal metadata and never scans conversation content', async () => {
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ['visible-label', 'transcript-only'],
      readSessionSummaries: async () => [
        makeSession({ id: 'visible-label', label: 'build failing parser' }),
        makeSession({ id: 'transcript-only', label: 'unrelated' }),
      ],
    })
    const result = await makeService(source).execute('search-sessions', { query: 'build failing parser', limit: 25 })

    expect(dataOf(result).items.map((item) => item.id)).toEqual(['visible-label'])
    expect(source.readSession).not.toHaveBeenCalled()
    expect(source.readTurnPage).not.toHaveBeenCalled()
    expect(source.readSessionSummaries).toHaveBeenCalledTimes(1)
  })

  it('rejects a search phrase longer than the metadata-search bound', async () => {
    const source = makeAnalyticsSource()
    const result = await makeService(source).execute('search-sessions', { query: 'x'.repeat(201) })

    expect(result.error).toMatchObject({ code: 'invalid_request' })
    expect(source.candidateSessionIds).not.toHaveBeenCalled()
  })

  it('returns at most twenty-five metadata rows by default', async () => {
    const ids = Array.from({ length: 30 }, (_, index) => `session-${index}`)
    const source = makeAnalyticsSource({
      candidateSessionIds: async () => ids,
      readSessionSummaries: async (batch: readonly string[]) => batch.map((id) => makeSession({ id, label: 'matching label' })),
    })
    const result = await makeService(source).execute('search-sessions', { query: 'matching' })

    expect(dataOf(result).items).toHaveLength(25)
    expect(result.pagination).toMatchObject({ limit: 25, returnedCount: 25 })
  })
})
