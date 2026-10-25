import { describe, expect, it, vi } from 'vitest'
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

  it('rejects a malformed legacy turn read at the seam instead of asking the bridge', async () => {
    const source = makeAnalyticsSource({ readTurnPage: async () => undefined })
    for (const request of [
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: -1, blockId: 'block-a' },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 1.5, blockId: 'block-a' },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 0, blockId: 'block-a', byteLimit: 0 },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 0, blockId: 'block-a', byteLimit: -100 },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 0, blockId: 'block-a', byteLimit: Number.NaN },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 0, blockId: 'block-a', byteLimit: 1_000_000 },
    ]) {
      const result = await makeService(source).execute('inspect', request)

      expect(result.error, JSON.stringify(request)).toMatchObject({ code: 'invalid_request' })
    }
    expect(source.readTurnPage).not.toHaveBeenCalled()
  })

  it('does not mutate the session object its source returned', async () => {
    const detail = { id: 'session-a', label: 'visible label', turns: [{ index: 0 }], content: { text: 'private' } }
    const source = makeAnalyticsSource({ readSession: async () => detail })
    const result = await makeService(source).execute('inspect', { kind: 'session', id: 'session-a' })

    expect(result.error).toBeNull()
    expect(detail).toEqual({ id: 'session-a', label: 'visible label', turns: [{ index: 0 }], content: { text: 'private' } })
    expect(JSON.stringify(result)).not.toContain('private')
  })

  it('rejects unknown fields on captured and legacy requests before reading', async () => {
    const readCapturedContext = vi.fn(async () => ({ data: {}, coverage: {}, pagination: {} }))
    const source = { ...makeAnalyticsSource(), readCapturedContext }
    const service = makeService(source)
    const captured = {
      mode: 'manifest',
      target: { kind: 'session', sessionId: 'session-a' },
    }
    const clean = await service.execute('inspect', captured)

    expect(clean.error).toBeNull()
    expect(readCapturedContext).toHaveBeenCalledTimes(1)
    for (const request of [
      { ...captured, unknownField: true },
      { kind: 'session', id: 'session-a', unknownField: true },
      { kind: 'turn', includeContent: true, sessionId: 'session-a', turnIndex: 0, blockId: 'block-a', unknownField: true },
    ]) {
      const result = await service.execute('inspect', request)

      expect(result.error, JSON.stringify(request)).toMatchObject({ code: 'invalid_request' })
    }
    expect(readCapturedContext).toHaveBeenCalledTimes(1)
    expect(source.readSession).not.toHaveBeenCalled()
    expect(source.readTurnPage).not.toHaveBeenCalled()
  })
})
