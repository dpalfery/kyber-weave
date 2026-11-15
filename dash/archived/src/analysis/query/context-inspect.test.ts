import { describe, expect, it } from 'vitest'
import { createContextFixture, largeContextText } from './fixtures/canonical-context.js'

type Response = Awaited<ReturnType<ReturnType<typeof createContextFixture>['service']['execute']>>

function requireData(response: Response): Record<string, unknown> {
  if (response.error !== null || response.data === null) {
    throw new Error(`Expected complete-context response, received ${response.error?.code ?? 'null data'}`)
  }
  return response.data
}

describe('canonical complete-context inspection', () => {
  it('enumerates sparse stable turn identities without returning their text', async () => {
    const fixture = createContextFixture()
    try {
      const response = await fixture.service.execute('inspect', {
        mode: 'manifest',
        target: { kind: 'session', sessionId: 'context-session' },
      })
      const data = requireData(response)
      const serialized = JSON.stringify(data)

      expect(data['items']).toEqual(expect.arrayContaining([
        expect.objectContaining({ turnIndex: 0 }),
        expect.objectContaining({ turnIndex: 3 }),
        expect.objectContaining({ origin: 'session_shared', spanId: 'context-shared-prompt' }),
      ]))
      expect(serialized).not.toContain('HISTORY_USER_MESSAGE')
      expect(serialized).not.toContain('LARGE_CONTEXT_BEGIN')
      expect(response.coverage).toMatchObject({ completeness: 'captured_only' })
    } finally {
      fixture.close()
    }
  })

  it('exposes turn block identities and role provenance in the manifest', async () => {
    const fixture = createContextFixture()
    try {
      const response = await fixture.service.execute('inspect', {
        mode: 'manifest',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
      })
      const data = requireData(response)
      const items = data['items'] as Array<Record<string, unknown>>
      const replay = await fixture.service.execute('inspect', {
        mode: 'manifest',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
      })
      expect(requireData(replay)['items']).toEqual(items)
      expect(items).toEqual(expect.arrayContaining([
        expect.objectContaining({ role: 'user', origin: 'turn' }),
        expect.objectContaining({ role: 'assistant', origin: 'turn' }),
      ]))
      for (const item of items) {
        expect(typeof item['partId']).toBe('string')
        expect(typeof item['blockId']).toBe('string')
      }
      expect(JSON.stringify(data)).not.toContain('HISTORY_USER_MESSAGE')
    } finally {
      fixture.close()
    }
  })

  it('pages the real compressed canonical parts as exact UTF-8 text with no gaps or duplicates', async () => {
    const fixture = createContextFixture({ largeText: true })
    try {
      const request = {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      }
      const joined = new Map<string, string>()
      const origins = new Set<string>()
      let cursor: string | undefined
      let pageCount = 0
      let leakedNoise = false

      do {
        const response = await fixture.service.execute('inspect', { ...request, ...(cursor === undefined ? {} : { cursor }) })
        const data = requireData(response)
        const chunks = data['chunks']
        expect(Array.isArray(chunks)).toBe(true)
        const page = JSON.stringify(response)
        expect(Buffer.byteLength(page, 'utf8')).toBeLessThanOrEqual(65_536)
        if (page.includes('unrelatedPayloadBeforeText')) leakedNoise = true
        for (const rawChunk of chunks as Array<Record<string, unknown>>) {
          expect(typeof rawChunk['text']).toBe('string')
          expect(Buffer.byteLength(rawChunk['text'] as string, 'utf8')).toBeLessThanOrEqual(16_384)
          expect(typeof rawChunk['partId']).toBe('string')
          const partId = rawChunk['partId'] as string
          joined.set(partId, (joined.get(partId) ?? '') + (rawChunk['text'] as string))
          if (typeof rawChunk['origin'] === 'string') origins.add(rawChunk['origin'])
        }
        const pagination = response.pagination
        expect(typeof pagination['hasMore']).toBe('boolean')
        cursor = typeof pagination['nextCursor'] === 'string' ? pagination['nextCursor'] : undefined
        pageCount += 1
        expect(pageCount).toBeLessThan(400)
      } while (cursor !== undefined)

      expect(pageCount).toBeGreaterThan(1)
      expect([...joined.values()].join('')).toContain(largeContextText(260_000))
      expect(leakedNoise).toBe(false)
      expect([...joined.values()].join('')).toContain('HISTORY_USER_MESSAGE')
      expect([...joined.values()].join('').split('HISTORY_USER_MESSAGE')).toHaveLength(3)
      expect([...joined.values()].join('')).toContain('HISTORY_ASSISTANT_REPLY')
      expect([...joined.values()].join('')).toContain('SESSION_SHARED_CONTEXT')
      expect([...joined.values()].join('')).toContain('SHARED_SESSION_START_PROMPT')
      expect(origins).toEqual(new Set(['turn', 'session_shared']))
    } finally {
      fixture.close()
    }
  })

  it('includes child execution context at run scope while keeping session scope local', async () => {
    const fixture = createContextFixture()
    try {
      const runResponse = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'run', runId: 'context-run' },
      })
      const runText = JSON.stringify(requireData(runResponse))
      expect(runText).toContain('SUBAGENT_CONTEXT_TEXT')

      const manifest = await fixture.service.execute('inspect', {
        mode: 'manifest',
        target: { kind: 'run', runId: 'context-run' },
      })
      const manifestText = JSON.stringify(requireData(manifest))
      const manifestItems = requireData(manifest)['items'] as Array<Record<string, unknown>>
      expect(manifestText).toContain('context-root-execution')
      expect(manifestText).toContain('context-child-execution')
      expect(manifestText).toContain('context-session')
      expect(manifestText).toContain('subagent-session')
      expect(manifestText).toContain('parentExecutionId')
      expect(manifestText).toContain('parentSessionId')
      expect(manifestText).toContain('isSubagent')
      expect(manifestItems).toEqual(expect.arrayContaining([
        expect.objectContaining({ sessionId: 'context-session', executionId: 'context-root-execution', isSubagent: false }),
        expect.objectContaining({ sessionId: 'subagent-session', executionId: 'context-child-execution', parentExecutionId: 'context-root-execution', parentSessionId: 'context-session', isSubagent: true }),
      ]))

      const sessionResponse = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'session', sessionId: 'context-session' },
      })
      const sessionText = JSON.stringify(requireData(sessionResponse))
      expect(sessionText).not.toContain('SUBAGENT_CONTEXT_TEXT')
      expect(sessionText).toContain('SHARED_SESSION_START_PROMPT')
    } finally {
      fixture.close()
    }
  })

  it('returns a labeled assembled-turn view without losing captured roles or shared context', async () => {
    const fixture = createContextFixture()
    try {
      const response = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'assembled_turn',
      })
      const data = requireData(response)
      const serialized = JSON.stringify(data)
      expect(data['representation']).toBe('assembled_turn')
      expect(serialized).toContain('HISTORY_USER_MESSAGE')
      expect(serialized).toContain('HISTORY_ASSISTANT_REPLY')
      expect(serialized).toContain('SHARED_SESSION_START_PROMPT')
      expect(response.coverage['completeness']).toBe('captured_only')
    } finally {
      fixture.close()
    }
  })

  it('keeps legacy content_json-only shared prompts discoverable', async () => {
    const fixture = createContextFixture()
    try {
      fixture.store.getDatabase()
        .prepare('UPDATE records SET parts_json = NULL, content_json = ? WHERE span_id = ?')
        .run(JSON.stringify({ system_prompt: 'LEGACY_SHARED_PROMPT' }), 'context-shared-prompt')

      const response = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'session', sessionId: 'context-session' },
      })
      expect(JSON.stringify(requireData(response))).toContain('LEGACY_SHARED_PROMPT')
    } finally {
      fixture.close()
    }
  })

  it('reports a sparse uncaptured turn as an explicit gap instead of substituting a neighbor', async () => {
    const fixture = createContextFixture()
    try {
      const response = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 3 },
      })
      const data = requireData(response)
      expect(data['chunks']).toEqual([])
      expect(response.coverage['gaps']).toEqual(expect.arrayContaining([
        expect.objectContaining({ turnIndex: 3 }),
      ]))
    } finally {
      fixture.close()
    }
  })

  it('rejects a content cursor replayed for another target or projection generation', async () => {
    const fixture = createContextFixture()
    try {
      const first = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      })
      const cursor = first.pagination['nextCursor']
      expect(typeof cursor).toBe('string')
      const replay = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      })
      expect(replay.data).toEqual(first.data)
      expect(replay.pagination).toEqual(first.pagination)

      const wrongTarget = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'other-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
        cursor,
      })
      expect(wrongTarget.error?.code).toBe('stale_cursor')

      fixture.setGeneration('projection-2')
      const oldGeneration = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
        cursor,
      })
      expect(oldGeneration.error?.code).toBe('stale_cursor')

      const wrongRepresentation = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'assembled_turn',
        byteLimit: 16_384,
        cursor,
      })
      expect(wrongRepresentation.error?.code).toBe('stale_cursor')

      const tampered = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
        cursor: `${String(cursor)}x`,
      })
      expect(tampered.error?.code).toBe('stale_cursor')

      const changedOptions = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 8_192,
        cursor,
      })
      expect(changedOptions.error?.code).toBe('stale_cursor')
    } finally {
      fixture.close()
    }
  })

  it('invalidates continuation after retained content is purged while the projection identity stays fixed', async () => {
    const fixture = createContextFixture()
    try {
      const first = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      })
      const cursor = first.pagination['nextCursor']
      expect(typeof cursor).toBe('string')
      fixture.store.getDatabase().prepare('UPDATE records SET parts_json = NULL WHERE span_id = ?').run('context-turn-0')

      const stale = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
        cursor,
      })
      expect(stale.error?.code).toBe('stale_cursor')
    } finally {
      fixture.close()
    }
  })

  it('cancels a context page without partial data and serves the next request', async () => {
    const fixture = createContextFixture()
    try {
      const request = {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      }
      const first = await fixture.service.execute('inspect', request)
      const cursor = first.pagination['nextCursor']
      expect(typeof cursor).toBe('string')

      const controller = new AbortController()
      controller.abort()
      const cancelled = await fixture.service.execute('inspect', { ...request, cursor }, { signal: controller.signal })
      expect(cancelled.error?.code).toBe('cancelled')
      expect(cancelled.data).toBeNull()

      const healthy = await fixture.service.execute('inspect', request)
      expect(healthy.error).toBeNull()
      expect(healthy.data).not.toBeNull()
    } finally {
      fixture.close()
    }
  })

  it('returns cancellation with no partial context when the caller has already stopped waiting', async () => {
    const fixture = createContextFixture()
    try {
      const controller = new AbortController()
      controller.abort()
      const response = await fixture.service.execute('inspect', {
        mode: 'content',
        target: { kind: 'run', runId: 'context-run' },
      }, { signal: controller.signal })

      expect(response.error?.code).toBe('cancelled')
      expect(response.data).toBeNull()
    } finally {
      fixture.close()
    }
  })

  it('pages a legacy turn block larger than 64 KiB decoded instead of refusing it', async () => {
    const fixture = createContextFixture()
    try {
      const request = {
        kind: 'turn',
        includeContent: true,
        sessionId: 'context-session',
        turnIndex: 0,
        blockId: 'tool_result_content',
        byteLimit: 16_384,
      }
      let cursor: string | undefined
      let text = ''
      let pageCount = 0
      do {
        const response = await fixture.service.execute('inspect', { ...request, ...(cursor === undefined ? {} : { cursor }) })
        // WHY the error must be null: the retained block decodes well past
        // 64 KiB, which the old whole-column cap refused as
        // content_unavailable instead of paging.
        expect(response.error).toBeNull()
        const content = (response.data?.['content'] ?? {}) as Record<string, unknown>
        expect(typeof content['text']).toBe('string')
        text += content['text'] as string
        cursor = typeof content['nextCursor'] === 'string' ? (content['nextCursor'] as string) : undefined
        pageCount += 1
        expect(pageCount).toBeLessThan(100)
      } while (cursor !== undefined)

      expect(pageCount).toBeGreaterThan(1)
      expect(text).toBe(largeContextText(30_000))
    } finally {
      fixture.close()
    }
  })

  it('holds no page content across a multi-page traversal', async () => {
    const fixture = createContextFixture()
    try {
      const request = {
        mode: 'content',
        target: { kind: 'turn', sessionId: 'context-session', turnIndex: 0 },
        representation: 'captured_parts',
        byteLimit: 16_384,
      }
      const retained: number[] = []
      let cursor: string | undefined
      let pageCount = 0
      do {
        const response = await fixture.service.execute('inspect', { ...request, ...(cursor === undefined ? {} : { cursor }) })
        expect(response.error).toBeNull()
        const stats = fixture.bridge.capturedContextTraversalStats()
        expect(stats.liveTraversals).toBeLessThanOrEqual(1)
        retained.push(stats.retainedBytes)
        cursor = typeof response.pagination['nextCursor'] === 'string' ? (response.pagination['nextCursor'] as string) : undefined
        pageCount += 1
        expect(pageCount).toBeLessThan(100)
      } while (cursor !== undefined)

      expect(pageCount).toBeGreaterThan(1)
      const drained = fixture.bridge.capturedContextTraversalStats()
      expect(drained.liveTraversals).toBe(0)
      // WHY below one page: entries hold only the cursor binding, never
      // page content, so retention stays flat while the traversal is live
      // and drains to zero with its last page — no matter how many pages
      // the traversal served.
      for (const bytes of retained) expect(bytes).toBeLessThan(60_000)
      expect(new Set(retained.slice(0, -1)).size).toBe(1)
      expect(retained[retained.length - 1]).toBe(0)
    } finally {
      fixture.close()
    }
  })

  it('answers cancelled when the read aborts mid-stream instead of reporting a corrupt column', async () => {
    const fixture = createContextFixture({ largeText: true })
    try {
      const target = { kind: 'turn', sessionId: 'context-session', turnIndex: 0 }
      const preAborted = new AbortController()
      preAborted.abort()
      // WHY pre-aborted first: the signal is already dead before the first
      // pump, so this pins the cancelled branch deterministically; the
      // setImmediate abort below pins it mid-stream.
      const refused = await fixture.bridge.analyticsCapturedContext({
        mode: 'content',
        target,
        representation: 'captured_parts',
        byteLimit: 16_384,
        projectionGeneration: 'projection-1',
      }, { signal: preAborted.signal })
      expect(refused).toMatchObject({ error: 'cancelled' })

      const midStream = new AbortController()
      const pending = fixture.bridge.analyticsCapturedContext({
        mode: 'content',
        target,
        representation: 'captured_parts',
        byteLimit: 16_384,
        projectionGeneration: 'projection-1',
      }, { signal: midStream.signal })
      setImmediate(() => midStream.abort())
      const interrupted = await pending
      expect(interrupted).toMatchObject({ error: 'cancelled' })
      expect(JSON.stringify(interrupted)).not.toContain('corrupt_column')

      const serviceAbort = new AbortController()
      const servicePending = fixture.service.execute('inspect', {
        mode: 'content',
        target,
        representation: 'captured_parts',
        byteLimit: 16_384,
      }, { signal: serviceAbort.signal })
      setImmediate(() => serviceAbort.abort())
      const envelope = await servicePending
      expect(envelope.error?.code).toBe('cancelled')
      expect(envelope.data).toBeNull()
    } finally {
      fixture.close()
    }
  })
})
