import { describe, expect, it, vi } from 'vitest'

import { CanonStore } from '../canon/store.js'
import { KyberBridge } from './bridge.js'
import type { CanonicalRecord, ContentPart } from '../canon/types.js'

const SESSION_ID = 'sess-fallback-cost-001'

function tokens() {
  return {
    freshInput: 100,
    cacheRead: 0,
    cacheCreation: 0,
    output: 10,
    reportedInput: 100,
    reportedOutput: 10,
  }
}

function record(
  spanId: string,
  parts: ContentPart[] | undefined,
  over: Partial<CanonicalRecord> = {},
): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-fallback-cost-001',
    parentSpanId: null,
    source: 'copilot',
    harness: 'copilot',
    sessionId: SESSION_ID,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-03T16:00:00.000Z',
    durationMs: 100,
    status: 'ok',
    tokens: tokens(),
    content: {},
    parts,
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

function seed(store: CanonStore): void {
  store.upsertMany([
    // The turn span carries no system prompt, so shared context must come
    // from the session-wide fallback scan.
    record('span-cost-turn-0', [{ part: 'tool_result_content', text: 'fallback turn output' }], {
      timestamp: '2026-09-03T16:00:00.000Z',
    }),
    record(
      'span-cost-sess-a',
      [{ part: 'system_prompt', text: 'COST_FALLBACK_ALPHA', tokens: 5 }],
      { op: 'session.start', name: 'session_scope', timestamp: '2026-09-03T15:59:00.000Z' },
    ),
    // Legacy content-only row (no parts): the fallback must read it from
    // content_json with no inflate.
    {
      ...record('span-cost-legacy', undefined, { timestamp: '2026-09-03T15:59:15.000Z' }),
      content: { system_prompt: 'COST_FALLBACK_LEGACY' },
    },
    record(
      'span-cost-sess-b',
      [{ part: 'system_prompt', text: 'COST_FALLBACK_BETA', tokens: 6 }],
      { op: 'session.start', name: 'session_scope', timestamp: '2026-09-03T15:59:30.000Z' },
    ),
    // Unrelated rows: large non-prompt parts plus a large raw blob. The
    // fallback must never materialize these into records.
    record(
      'span-cost-noise-1',
      [
        { part: 'tool_result_content', text: `NOISE_1_${'X'.repeat(200_000)}` },
        { part: 'conversation_history', text: `noise history 1 ${'Y'.repeat(50_000)}` },
      ],
      {
        timestamp: '2026-09-03T15:58:00.000Z',
        raw: { body: `raw-noise-1 ${'Z'.repeat(200_000)}` },
      },
    ),
    record('span-cost-noise-2', [{ part: 'tool_result_content', text: `NOISE_2_${'W'.repeat(200_000)}` }], {
      timestamp: '2026-09-03T16:01:00.000Z',
      raw: { body: `raw-noise-2 ${'V'.repeat(200_000)}` },
    }),
  ])
  store.upsertSession({
    sessionId: SESSION_ID,
    harness: 'copilot',
    label: 'Fallback cost',
    payload: {
      id: SESSION_ID,
      harness: 'copilot',
      turns: [{ index: 0, spanId: 'span-cost-turn-0', model: 'm' }],
    },
  })
}

describe('Shared-context fallback cost (PI follow-up)', () => {
  it('reads session-scope prompts without inflating unrelated rows', () => {
    const store = new CanonStore(':memory:')
    try {
      seed(store)
      const found = store.systemPromptPartsForSession(SESSION_ID)
      expect(found).toEqual([
        { spanId: 'span-cost-sess-a', text: 'COST_FALLBACK_ALPHA' },
        { spanId: 'span-cost-legacy', text: 'COST_FALLBACK_LEGACY' },
        { spanId: 'span-cost-sess-b', text: 'COST_FALLBACK_BETA' },
      ])
      expect(store.systemPromptPartsForSession(SESSION_ID, 'span-cost-sess-a')).toEqual([
        { spanId: 'span-cost-legacy', text: 'COST_FALLBACK_LEGACY' },
        { spanId: 'span-cost-sess-b', text: 'COST_FALLBACK_BETA' },
      ])
    } finally {
      store.close()
    }
  })

  it('serves the fallback without a full-session record walk', () => {
    const store = new CanonStore(':memory:')
    try {
      seed(store)
      const recordsForSession = vi.spyOn(store, 'recordsForSession')
      const get = vi.spyOn(store, 'get')
      const bridge = new KyberBridge({ store })
      try {
        const body = bridge.assembleTurnContent(SESSION_ID, 0, Number.POSITIVE_INFINITY)
        expect(body).not.toBeNull()
        // Capture order, exact texts, and span ids are unchanged.
        expect(body?.sharedContext?.parts).toEqual([
          { text: 'COST_FALLBACK_ALPHA', spanId: 'span-cost-sess-a' },
          { text: 'COST_FALLBACK_LEGACY', spanId: 'span-cost-legacy' },
          { text: 'COST_FALLBACK_BETA', spanId: 'span-cost-sess-b' },
        ])
        expect(body?.sharedContext?.seenBefore).toBe(false)
        expect(body?.assembledText).toContain('fallback turn output')
        expect(body?.truncated).toBeUndefined()
        // The old fallback called loadContentRecords(sessionId), which walks
        // every session row into full records; the narrow seam must not.
        expect(recordsForSession).not.toHaveBeenCalled()
        // Only the target span is fetched by id — never one get per row.
        expect(get).toHaveBeenCalledTimes(1)
        expect(get).toHaveBeenCalledWith('span-cost-turn-0')

        // Default budget behavior is unchanged for small texts: no flags.
        const capped = bridge.assembleTurnContent(SESSION_ID, 0)
        expect(capped?.sharedContext?.parts).toEqual(body?.sharedContext?.parts)
        expect(capped?.truncated).toBeUndefined()
        for (const part of capped?.sharedContext?.parts ?? []) {
          expect(part.truncated).toBeUndefined()
          expect(part.totalLength).toBeUndefined()
        }
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })
})
