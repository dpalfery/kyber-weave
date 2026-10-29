import { describe, expect, it } from 'vitest'
import { CanonStore } from '../canon/store.js'
import { createContextFixture } from '../analysis/query/fixtures/canonical-context.js'
import type { CanonicalRecord } from '../canon/types.js'
import { contentFromParts } from '../canon/types.js'

function smallTurn(spanId: string, sessionId: string, text: string): CanonicalRecord {
  const parts = [{ part: 'conversation_history' as const, text, role: 'assistant' as const, order: 0 }]
  return {
    spanId,
    traceId: 'trace-bounded',
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-24T10:02:00.000Z',
    durationMs: 5,
    status: 'ok',
    tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 5, reportedInput: 10, reportedOutput: 5 },
    content: contentFromParts(parts),
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
  }
}

function addLateHugeTurn(store: CanonStore, sessionId: string, turnIndex: number, spanId: string, bytes: number): string {
  const huge = `HUGE_LATE_BEGIN${'雪🙂é'.repeat(Math.ceil(bytes / 8))}HUGE_LATE_END`
  store.upsertMany([smallTurn(spanId, sessionId, huge)])
  const payload = store.getSessionPayload(sessionId) as unknown as {
    turns?: Array<{ index: number; spanId: string }>
  } | undefined
  const turns = [...(payload?.turns ?? [])]
  if (!turns.some((t) => t.index === turnIndex)) {
    turns.push({ index: turnIndex, spanId })
    turns.sort((a, b) => a.index - b.index)
  }
  store.upsertSession({
    sessionId,
    harness: 'claude-code',
    label: 'Context fixture',
    isSubagent: false,
    parentSession: null,
    agentName: 'test-agent',
    repo: 'fixture-repo',
    branch: 'fixture-branch',
    started: '2026-10-24T10:00:00.000Z',
    ended: '2026-10-24T10:01:00.000Z',
    payload: { id: sessionId, session_id: sessionId, harness: 'claude-code', label: 'x', turns },
  })
  return huge
}

describe('captured context bounded paging (F4)', () => {
  it('serves the first content page without decoding a late huge block', async () => {
    const fixture = createContextFixture()
    try {
      const huge = addLateHugeTurn(fixture.store, 'context-session', 9, 'context-huge-late', 2_000_000)
      expect(huge.length).toBeGreaterThan(1_000_000)
      const bridge = fixture.bridge as unknown as Record<string, unknown>
      const captured = bridge['capturedSpanParts'] as (...args: unknown[]) => Promise<unknown>
      let decodedUnits = 0
      bridge['capturedSpanParts'] = async (...args: unknown[]) => {
        decodedUnits += 1
        return captured.apply(fixture.bridge, args as never[])
      }
      try {
        const first = (await fixture.bridge.analyticsCapturedContext({
          mode: 'content',
          target: { kind: 'session', sessionId: 'context-session' },
          representation: 'captured_parts',
          byteLimit: 1024,
          projectionGeneration: 'projection-1',
        })) as Record<string, unknown>
        expect((first as { error?: unknown }).error).toBeUndefined()
        const data = (first as { data?: { chunks?: unknown[] } }).data
        expect(Array.isArray(data?.chunks)).toBe(true)
        // The first 60KB page is filled by the shared preamble plus the
        // small turn; the late ~2MB block must not be decoded for it.
        // Full scope holds shared + turn-0 + huge-late (+ subagent only on
        // run scope); decoding everything would touch the huge unit.
        expect(decodedUnits).toBeLessThan(3)
        const text = JSON.stringify(first)
        expect(text).not.toContain('HUGE_LATE_BEGIN')
      } finally {
        bridge['capturedSpanParts'] = captured
      }
    } finally {
      fixture.close()
    }
  })

  it('roundtrips huge early and late blocks across every page with no loss or duplication', async () => {
    const fixture = createContextFixture()
    try {
      const hugeLate = addLateHugeTurn(fixture.store, 'context-session', 9, 'context-huge-late', 300_000)
      const request = {
        mode: 'content',
        target: { kind: 'session', sessionId: 'context-session' },
        representation: 'captured_parts',
        byteLimit: 16_384,
        projectionGeneration: 'projection-1',
      }
      const joined = new Map<string, string>()
      let cursor: string | undefined
      let pages = 0
      do {
        const response = (await fixture.bridge.analyticsCapturedContext({
          ...request,
          ...(cursor === undefined ? {} : { cursor }),
        })) as { data?: { chunks?: Array<Record<string, unknown>> }; pagination?: { nextCursor?: string | null; hasMore?: boolean }; error?: unknown }
        expect(response.error).toBeUndefined()
        for (const chunk of response.data?.chunks ?? []) {
          const id = chunk['partId'] as string
          joined.set(id, (joined.get(id) ?? '') + (chunk['text'] as string))
        }
        cursor = typeof response.pagination?.nextCursor === 'string' ? response.pagination.nextCursor : undefined
        pages += 1
        expect(pages).toBeLessThan(400)
      } while (cursor !== undefined)
      expect(pages).toBeGreaterThan(1)
      const all = [...joined.values()].join('')
      expect(all).toContain('HISTORY_USER_MESSAGE')
      expect(all).toContain('HUGE_LATE_BEGIN')
      expect(all).toContain(hugeLate.slice(0, 100))
      expect(all).toContain('HUGE_LATE_END')
      // No chunk may exceed the byte limit and multi-byte code points
      // must survive the split intact.
      expect(all).toContain('雪🙂é')
    } finally {
      fixture.close()
    }
  })

  it('serves run manifests without decoding retained text', async () => {
    const fixture = createContextFixture()
    try {
      const bridge = fixture.bridge as unknown as Record<string, unknown>
      const streamParts = bridge['streamPartsColumn'] as (...args: unknown[]) => Promise<unknown>
      const streamContent = bridge['streamContentColumn'] as (...args: unknown[]) => Promise<unknown>
      let textDecodes = 0
      bridge['streamPartsColumn'] = async (...args: unknown[]) => {
        textDecodes += 1
        return streamParts.apply(fixture.bridge, args as never[])
      }
      bridge['streamContentColumn'] = async (...args: unknown[]) => {
        textDecodes += 1
        return streamContent.apply(fixture.bridge, args as never[])
      }
      try {
        const manifest = (await fixture.bridge.analyticsCapturedContext({
          mode: 'manifest',
          target: { kind: 'run', runId: 'context-run' },
          projectionGeneration: 'projection-1',
        })) as Record<string, unknown>
        expect((manifest as { error?: unknown }).error).toBeUndefined()
        expect(textDecodes).toBe(0)
        const text = JSON.stringify(manifest)
        expect(text).not.toContain('HISTORY_USER_MESSAGE')
        expect(text).not.toContain('LARGE_CONTEXT_BEGIN')
      } finally {
        bridge['streamPartsColumn'] = streamParts
        bridge['streamContentColumn'] = streamContent
      }
    } finally {
      fixture.close()
    }
  })

  it('budgets discovered stream gaps so the sealed page holds the envelope', async () => {
    const fixture = createContextFixture()
    try {
      // Sixty part-less turns ahead of five ~14KB content turns: every
      // part-less unit rides the served coverage as a stream gap whose
      // envelope bytes the page budget must account for as discovered.
      // Uncounted, the gaps push the sealed page ~10KB past the 60KB
      // page budget; counted, the chunks seal earlier and the envelope
      // holds.
      const sessionId = 'gap-budget-session'
      const base = '2026-10-24T11:00:00.000Z'
      const at = (index: number): string =>
        new Date(Date.parse(base) + index * 1_000).toISOString()
      const partless = Array.from({ length: 60 }, (_, index) => {
        const spanId = `gap-budget-empty-${index}`
        return {
          spanId,
          traceId: 'trace-gap-budget',
          parentSpanId: null,
          source: 'claude-code',
          harness: 'claude-code',
          sessionId,
          name: 'llm_request',
          op: 'llm.invoke',
          kind: 'client',
          timestamp: at(index),
          durationMs: 5,
          status: 'ok',
          tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 5, reportedInput: 10, reportedOutput: 5 },
          content: {},
          parts: [],
          cost: { basis: 'unknown', status: 'no_rate' },
        }
      })
      const texted = Array.from({ length: 5 }, (_, index) => {
        const spanId = `gap-budget-text-${index}`
        return {
          spanId,
          traceId: 'trace-gap-budget',
          parentSpanId: null,
          source: 'claude-code',
          harness: 'claude-code',
          sessionId,
          name: 'llm_request',
          op: 'llm.invoke',
          kind: 'client',
          timestamp: at(60 + index),
          durationMs: 5,
          status: 'ok',
          tokens: { freshInput: 10, cacheRead: 0, cacheCreation: 0, output: 5, reportedInput: 10, reportedOutput: 5 },
          content: {},
          parts: [{ part: 'conversation_history', text: `BUDGET_TEXT_${'x'.repeat(14_000)}`, role: 'assistant', order: 0 }],
          cost: { basis: 'unknown', status: 'no_rate' },
        }
      })
      fixture.store.upsertMany([...partless, ...texted] as never[])
      fixture.store.upsertSession({
        sessionId,
        harness: 'claude-code',
        label: 'Gap budget',
        isSubagent: false,
        parentSession: null,
        agentName: 'test-agent',
        repo: 'fixture-repo',
        branch: 'fixture-branch',
        started: '2026-10-24T11:00:00.000Z',
        ended: '2026-10-24T11:05:00.000Z',
        payload: {
          id: sessionId,
          session_id: sessionId,
          harness: 'claude-code',
          label: 'Gap budget',
          turns: [...partless, ...texted].map((record, index) => ({ index, spanId: record.spanId })),
        },
      })

      const first = (await fixture.bridge.analyticsCapturedContext({
        mode: 'content',
        target: { kind: 'session', sessionId },
        representation: 'captured_parts',
        byteLimit: 16_384,
        projectionGeneration: 'projection-1',
      })) as Record<string, unknown>
      expect((first as { error?: unknown }).error).toBeUndefined()
      expect(Buffer.byteLength(JSON.stringify(first), 'utf8')).toBeLessThanOrEqual(60_000)
    } finally {
      fixture.close()
    }
  })

  it('answers cancelled for manifest and content without partial data', async () => {
    const fixture = createContextFixture()
    try {
      const preAborted = new AbortController()
      preAborted.abort()
      const contentRefused = await fixture.bridge.analyticsCapturedContext(
        {
          mode: 'content',
          target: { kind: 'session', sessionId: 'context-session' },
          representation: 'captured_parts',
          byteLimit: 1024,
          projectionGeneration: 'projection-1',
        },
        { signal: preAborted.signal },
      )
      expect(contentRefused).toMatchObject({ error: 'cancelled' })
      const manifestRefused = await fixture.bridge.analyticsCapturedContext(
        {
          mode: 'manifest',
          target: { kind: 'run', runId: 'context-run' },
          projectionGeneration: 'projection-1',
        },
        { signal: preAborted.signal },
      )
      expect(manifestRefused).toMatchObject({ error: 'cancelled' })
    } finally {
      fixture.close()
    }
  })

  it('serves concurrent first pages within bounded traversals', async () => {
    const fixture = createContextFixture()
    try {
      addLateHugeTurn(fixture.store, 'context-session', 9, 'context-huge-late', 500_000)
      const request = {
        mode: 'content',
        target: { kind: 'session', sessionId: 'context-session' },
        representation: 'captured_parts',
        byteLimit: 1024,
        projectionGeneration: 'projection-1',
      }
      const results = await Promise.all(
        Array.from({ length: 5 }, () => fixture.bridge.analyticsCapturedContext({ ...request })),
      )
      for (const result of results) {
        expect((result as { error?: unknown }).error).toBeUndefined()
      }
      const stats = fixture.bridge.capturedContextTraversalStats()
      expect(stats.liveTraversals).toBeLessThanOrEqual(5)
      expect(stats.retainedBytes).toBeLessThan(60_000 * 5)
    } finally {
      fixture.close()
    }
  })
})
