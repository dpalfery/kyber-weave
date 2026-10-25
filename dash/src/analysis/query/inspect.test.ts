import { describe, expect, it } from 'vitest'
import { dataOf, makeAnalyticsSource, makeService } from './fixtures/analytics-source.js'

describe('explicit evidence inspection', () => {
  it('does not return conversation content unless the caller opts in', async () => {
    const source = makeAnalyticsSource({
      readSession: async () => ({ id: 'session-a', label: 'visible label', turns: [{ index: 0, blocks: [{ id: 'block-a', text: 'private turn text' }] }] }),
    })
    const result = await makeService(source).execute('inspect', { kind: 'session', id: 'session-a' })

    expect(result.error).toBeNull()
    expect(dataOf(result)).toMatchObject({ id: 'session-a', label: 'visible label' })
    expect(JSON.stringify(result)).not.toContain('private turn text')
    expect(source.readTurnPage).not.toHaveBeenCalled()
  })

  it('fetches only the explicitly identified turn and bounded text page', async () => {
    const source = makeAnalyticsSource({
      readTurnPage: async () => ({ text: 'selected block', nextCursor: null, byteLength: 14 }),
    })
    const result = await makeService(source).execute('inspect', {
      kind: 'turn',
      sessionId: 'session-a',
      turnIndex: 2,
      includeContent: true,
      blockId: 'block-2',
      byteLimit: 16384,
    })

    expect(dataOf(result).content).toMatchObject({ text: 'selected block', byteLength: 14 })
    expect(source.readTurnPage).toHaveBeenCalledWith(expect.objectContaining({ sessionId: 'session-a', turnIndex: 2, blockId: 'block-2' }))
    expect(source.readSession).not.toHaveBeenCalled()
  })

  it('does not fall back to a neighboring turn when the requested index is absent', async () => {
    const source = makeAnalyticsSource({ readTurnPage: async () => undefined })
    const result = await makeService(source).execute('inspect', {
      kind: 'turn',
      sessionId: 'session-a',
      turnIndex: 4,
      includeContent: true,
      blockId: 'block-4',
    })

    expect(result.error).toMatchObject({ code: 'content_unavailable' })
  })
})
