import { describe, expect, it, vi } from 'vitest'
import { createCanonAnalyticsSource } from './canon-source.js'
import type { KyberBridge } from '../../server/bridge.js'

function stubBridge() {
  const analyticsTurnBlock = vi.fn(
    async (request: { sessionId: string; turnIndex: number; blockKey: string; byteLimit: number; offset?: number }) => ({
      text: `text@${request.offset ?? 0}`,
      byteLength: 7,
      nextOffset: 9,
    }),
  )
  return { analyticsTurnBlock, bridge: { analyticsTurnBlock } as unknown as KyberBridge }
}

describe('legacy turn-block cursor binding', () => {
  it('serves the first page without a cursor and seals the target into the next cursor', async () => {
    const { analyticsTurnBlock, bridge } = stubBridge()
    const source = createCanonAnalyticsSource(bridge, { projectionGeneration: () => 'generation-1' })

    const first = (await source.readTurnPage({ sessionId: 'session-a', turnIndex: 2, blockId: 'block-a', byteLimit: 100 })) as Record<string, unknown>

    expect(analyticsTurnBlock).toHaveBeenCalledWith({ sessionId: 'session-a', turnIndex: 2, blockKey: 'block-a', byteLimit: 100, offset: 0 })
    expect(typeof first['nextCursor']).toBe('string')
  })

  it('replays the cursor for the identical target but rejects another block, turn, session or limit', async () => {
    const { analyticsTurnBlock, bridge } = stubBridge()
    const source = createCanonAnalyticsSource(bridge, { projectionGeneration: () => 'generation-1' })
    const params = { sessionId: 'session-a', turnIndex: 2, blockId: 'block-a', byteLimit: 100 }
    const first = (await source.readTurnPage(params)) as Record<string, unknown>
    const cursor = first['nextCursor'] as string
    analyticsTurnBlock.mockClear()

    const replay = (await source.readTurnPage({ ...params, cursor })) as Record<string, unknown>
    expect(replay['text']).toBe('text@9')
    expect(analyticsTurnBlock).toHaveBeenCalledWith(expect.objectContaining({ offset: 9 }))

    for (const variant of [
      { ...params, blockId: 'block-b' },
      { ...params, turnIndex: 3 },
      { ...params, sessionId: 'session-b' },
      { ...params, byteLimit: 50 },
    ]) {
      analyticsTurnBlock.mockClear()
      const stale = await source.readTurnPage({ ...variant, cursor })
      expect(stale).toEqual({ kind: 'stale_cursor' })
      expect(analyticsTurnBlock).not.toHaveBeenCalled()
    }
  })

  it('rejects a cursor from another projection generation or a tampered cursor', async () => {
    const { analyticsTurnBlock, bridge } = stubBridge()
    let generation = 'generation-1'
    const source = createCanonAnalyticsSource(bridge, { projectionGeneration: () => generation })
    const params = { sessionId: 'session-a', turnIndex: 2, blockId: 'block-a', byteLimit: 100 }
    const first = (await source.readTurnPage(params)) as Record<string, unknown>
    const cursor = first['nextCursor'] as string

    generation = 'generation-2'
    analyticsTurnBlock.mockClear()
    expect(await source.readTurnPage({ ...params, cursor })).toEqual({ kind: 'stale_cursor' })
    expect(analyticsTurnBlock).not.toHaveBeenCalled()

    generation = 'generation-1'
    analyticsTurnBlock.mockClear()
    expect(await source.readTurnPage({ ...params, cursor: `${cursor}x` })).toEqual({ kind: 'stale_cursor' })
    expect(analyticsTurnBlock).not.toHaveBeenCalled()
  })
})
