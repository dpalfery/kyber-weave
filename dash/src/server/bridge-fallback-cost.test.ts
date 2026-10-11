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
    // content_json with no inflate. Stamped `session.start` so it is a
    // genuine non-turn session prompt — turn spans (`llm.invoke`) are
    // excluded from the session scan by operation, never by text.
    {
      ...record('span-cost-legacy', undefined, {
        op: 'session.start',
        name: 'session_scope',
        timestamp: '2026-09-03T15:59:15.000Z',
      }),
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
        { spanId: 'span-cost-sess-a', text: 'COST_FALLBACK_ALPHA', timestamp: '2026-09-03T15:59:00.000Z' },
        { spanId: 'span-cost-legacy', text: 'COST_FALLBACK_LEGACY', timestamp: '2026-09-03T15:59:15.000Z' },
        { spanId: 'span-cost-sess-b', text: 'COST_FALLBACK_BETA', timestamp: '2026-09-03T15:59:30.000Z' },
      ])
      expect(store.systemPromptPartsForSession(SESSION_ID, 'span-cost-sess-a')).toEqual([
        { spanId: 'span-cost-legacy', text: 'COST_FALLBACK_LEGACY', timestamp: '2026-09-03T15:59:15.000Z' },
        { spanId: 'span-cost-sess-b', text: 'COST_FALLBACK_BETA', timestamp: '2026-09-03T15:59:30.000Z' },
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

describe('Mixed session and turn prompts (PI blocking finding)', () => {
  const MIXED_ID = 'sess-mixed-prompts-001'
  const TWO_TURN_ID = 'sess-mixed-two-turn-001'
  const DUP_SPAN_ID = 'sess-mixed-dup-span-001'

  function mixedRecord(
    spanId: string,
    parts: ContentPart[] | undefined,
    over: Partial<CanonicalRecord> = {},
  ): CanonicalRecord {
    return {
      ...record(spanId, parts),
      sessionId: MIXED_ID,
      traceId: 'trace-mixed-001',
      ...over,
    }
  }

  function seedMixed(store: CanonStore): void {
    store.upsertMany([
      // The turn span carries its own system prompt AND non-turn session
      // spans carry theirs: both must survive, in capture order.
      mixedRecord('span-mix-turn-0', [{ part: 'system_prompt', text: 'TURN_PROMPT_MIXED', tokens: 7 }], {
        timestamp: '2026-09-03T16:00:00.000Z',
      }),
      mixedRecord(
        'span-mix-sess-a',
        [
          { part: 'system_prompt', text: 'SESSION_PROMPT_MIXED_A', tokens: 5 },
          { part: 'system_prompt', text: 'SESSION_PROMPT_MIXED_B', tokens: 5 },
        ],
        { op: 'session.start', name: 'session_scope', timestamp: '2026-09-03T15:59:00.000Z' },
      ),
      // Same text as the turn prompt, but a distinct captured span: it must
      // stay repeated, never text-deduplicated away.
      mixedRecord('span-mix-sess-b', [{ part: 'system_prompt', text: 'TURN_PROMPT_MIXED', tokens: 7 }], {
        op: 'session.start',
        name: 'session_scope',
        timestamp: '2026-09-03T15:59:30.000Z',
      }),
    ])
    store.upsertSession({
      sessionId: MIXED_ID,
      harness: 'copilot',
      label: 'Mixed prompts',
      payload: {
        id: MIXED_ID,
        harness: 'copilot',
        turns: [{ index: 0, spanId: 'span-mix-turn-0', model: 'm' }],
      },
    })
  }

  function seedTwoTurn(store: CanonStore): void {
    const base: Partial<CanonicalRecord> = { sessionId: TWO_TURN_ID, traceId: 'trace-mixed-two-001' }
    store.upsertMany([
      { ...record('span-m2-t0', [{ part: 'system_prompt', text: 'T0_PROMPT', tokens: 3 }], base), timestamp: '2026-09-03T16:00:00.000Z' },
      {
        ...record('span-m2-sess', [{ part: 'system_prompt', text: 'MID_SESSION_PROMPT', tokens: 4 }], base),
        op: 'session.start',
        name: 'session_scope',
        timestamp: '2026-09-03T16:00:30.000Z',
      },
      { ...record('span-m2-t1', [{ part: 'system_prompt', text: 'T1_PROMPT', tokens: 3 }], base), timestamp: '2026-09-03T16:01:00.000Z' },
      {
        ...record('span-m2-late', [{ part: 'system_prompt', text: 'LATE_SESSION_PROMPT', tokens: 4 }], base),
        op: 'session.start',
        name: 'session_scope',
        timestamp: '2026-09-03T16:02:00.000Z',
      },
    ])
    store.upsertSession({
      sessionId: TWO_TURN_ID,
      harness: 'copilot',
      label: 'Two turns with session prompts',
      payload: {
        id: TWO_TURN_ID,
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-m2-t0', model: 'm' },
          { index: 1, spanId: 'span-m2-t1', model: 'm' },
        ],
      },
    })
  }

  function seedDupSpan(store: CanonStore): void {
    const base: Partial<CanonicalRecord> = { sessionId: DUP_SPAN_ID, traceId: 'trace-mixed-dup-001' }
    store.upsertMany([
      { ...record('span-dup-x', [{ part: 'system_prompt', text: 'DUP_PROMPT', tokens: 2 }], base), timestamp: '2026-09-03T16:00:00.000Z' },
    ])
    store.upsertSession({
      sessionId: DUP_SPAN_ID,
      harness: 'copilot',
      label: 'One span serving two turns',
      payload: {
        id: DUP_SPAN_ID,
        harness: 'copilot',
        turns: [
          { index: 0, spanId: 'span-dup-x', model: 'm' },
          { index: 1, spanId: 'span-dup-x', model: 'm' },
        ],
      },
    })
  }

  it('keeps turn and non-turn prompts together in capture order without text deduplication', () => {
    const store = new CanonStore(':memory:')
    try {
      seedMixed(store)
      const bridge = new KyberBridge({ store })
      try {
        const body = bridge.assembleTurnContent(MIXED_ID, 0, Number.POSITIVE_INFINITY)
        expect(body).not.toBeNull()
        // Capture order, exact texts, distinct span ids: the session spans
        // first (multi-part span keeps parts order), then the turn span.
        // The turn text stays twice — once per captured span. Turn-derived
        // occurrences carry their contributing turnIndex; session-scope
        // parts carry none and are never guessed as a turn.
        expect(body?.sharedContext?.parts).toEqual([
          { text: 'SESSION_PROMPT_MIXED_A', spanId: 'span-mix-sess-a' },
          { text: 'SESSION_PROMPT_MIXED_B', spanId: 'span-mix-sess-a' },
          { text: 'TURN_PROMPT_MIXED', spanId: 'span-mix-sess-b' },
          { text: 'TURN_PROMPT_MIXED', spanId: 'span-mix-turn-0', turnIndex: 0 },
        ])
        expect(body?.sharedContext?.seenBefore).toBe(false)
        // The turn's own block still renders its prompt.
        expect(body?.blocks.find((b) => b.key === 'system_prompt')?.text).toBe('TURN_PROMPT_MIXED')
        expect(body?.truncated).toBeUndefined()
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('interleaves a between-turns session prompt and withholds prompts captured after the turn', () => {
    const store = new CanonStore(':memory:')
    try {
      seedTwoTurn(store)
      const bridge = new KyberBridge({ store })
      try {
        // Turn 0 must not present what was captured after it saw the API.
        const first = bridge.assembleTurnContent(TWO_TURN_ID, 0, Number.POSITIVE_INFINITY)
        expect(first?.sharedContext?.parts).toEqual([{ text: 'T0_PROMPT', spanId: 'span-m2-t0', turnIndex: 0 }])
        expect(first?.sharedContext?.parts.some((p) => p.text === 'MID_SESSION_PROMPT')).toBe(false)
        expect(first?.sharedContext?.parts.some((p) => p.text === 'LATE_SESSION_PROMPT')).toBe(false)

        // Turn 1 sees the earlier turn once, the between-turns session
        // prompt in capture position, and its own prompt — but not the late one.
        const second = bridge.assembleTurnContent(TWO_TURN_ID, 1, Number.POSITIVE_INFINITY)
        expect(second?.sharedContext?.parts).toEqual([
          { text: 'T0_PROMPT', spanId: 'span-m2-t0', turnIndex: 0 },
          { text: 'MID_SESSION_PROMPT', spanId: 'span-m2-sess' },
          { text: 'T1_PROMPT', spanId: 'span-m2-t1', turnIndex: 1 },
        ])
        expect(second?.sharedContext?.parts.some((p) => p.text === 'LATE_SESSION_PROMPT')).toBe(false)
        // The repeated earliest prompt carries earlier-turn evidence.
        expect(second?.sharedContext?.seenBefore).toBe(true)
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('represents one span serving two turn indexes as two occurrences', () => {
    const store = new CanonStore(':memory:')
    try {
      seedDupSpan(store)
      const bridge = new KyberBridge({ store })
      try {
        const body = bridge.assembleTurnContent(DUP_SPAN_ID, 1, Number.POSITIVE_INFINITY)
        // Full-turn semantics: complete turn copies repeat a prompt, so the
        // same span id referenced at turn 0 and turn 1 yields two API-seen
        // occurrences with different turnIndex values.
        expect(body?.sharedContext?.parts).toEqual([
          { text: 'DUP_PROMPT', spanId: 'span-dup-x', turnIndex: 0 },
          { text: 'DUP_PROMPT', spanId: 'span-dup-x', turnIndex: 1 },
        ])
        // The selected turn's own occurrence still renders once in its block.
        expect(body?.blocks.find((b) => b.key === 'system_prompt')?.text).toBe('DUP_PROMPT')
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('keeps same-turn repeated identical parts repeated with one turnIndex', () => {
    const store = new CanonStore(':memory:')
    try {
      const base: Partial<CanonicalRecord> = { sessionId: 'sess-mixed-repeat-001', traceId: 'trace-mixed-repeat-001' }
      store.upsertMany([
        { ...record('span-rep-t0', [{ part: 'system_prompt', text: 'REP_PROMPT' }, { part: 'system_prompt', text: 'REP_PROMPT' }], base), timestamp: '2026-09-03T16:00:00.000Z' },
      ])
      store.upsertSession({
        sessionId: 'sess-mixed-repeat-001',
        harness: 'copilot',
        label: 'Repeated in one turn',
        payload: { id: 'sess-mixed-repeat-001', harness: 'copilot', turns: [{ index: 0, spanId: 'span-rep-t0', model: 'm' }] },
      })
      const bridge = new KyberBridge({ store })
      try {
        const body = bridge.assembleTurnContent('sess-mixed-repeat-001', 0, Number.POSITIVE_INFINITY)
        expect(body?.sharedContext?.parts).toEqual([
          { text: 'REP_PROMPT', spanId: 'span-rep-t0', turnIndex: 0 },
          { text: 'REP_PROMPT', spanId: 'span-rep-t0', turnIndex: 0 },
        ])
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('keeps the mixed union on the narrow seam without a full-session record walk', () => {
    const store = new CanonStore(':memory:')
    try {
      seedMixed(store)
      const recordsForSession = vi.spyOn(store, 'recordsForSession')
      const get = vi.spyOn(store, 'get')
      const bridge = new KyberBridge({ store })
      try {
        const body = bridge.assembleTurnContent(MIXED_ID, 0, Number.POSITIVE_INFINITY)
        expect(body?.sharedContext?.parts).toHaveLength(4)
        expect(recordsForSession).not.toHaveBeenCalled()
        // Only the target span is fetched by id — the session prompts come
        // from the narrow timestamped projection.
        expect(get).toHaveBeenCalledTimes(1)
        expect(get).toHaveBeenCalledWith('span-mix-turn-0')
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })

  it('excludes a turn-derived prompt missing from the payload turn list from the session scan', () => {
    const P3_ID = 'sess-p3-hidden-turn-001'
    const store = new CanonStore(':memory:')
    try {
      const base: Partial<CanonicalRecord> = { sessionId: P3_ID, traceId: 'trace-p3-001' }
      store.upsertMany([
        {
          ...record('span-p3-sess', [{ part: 'system_prompt', text: 'P3_SESSION_PROMPT', tokens: 4 }], base),
          op: 'session.start',
          name: 'session_scope',
          timestamp: '2026-09-03T15:59:00.000Z',
        },
        // Turn-derived by operation (`llm.invoke`) but absent from the
        // payload turn list below: without the operation filter it would
        // masquerade as an unindexed session-scope prompt. Identity is never
        // inferred from text — this text is distinct on purpose.
        {
          ...record('span-p3-hidden', [{ part: 'system_prompt', text: 'P3_HIDDEN_TURN_PROMPT', tokens: 4 }], base),
          timestamp: '2026-09-03T15:59:30.000Z',
        },
        {
          ...record('span-p3-t0', [{ part: 'system_prompt', text: 'P3_T0_PROMPT', tokens: 3 }], base),
          timestamp: '2026-09-03T16:00:00.000Z',
        },
        {
          ...record('span-p3-t1', [{ part: 'system_prompt', text: 'P3_T1_PROMPT', tokens: 3 }], base),
          timestamp: '2026-09-03T16:01:00.000Z',
        },
      ])
      store.upsertSession({
        sessionId: P3_ID,
        harness: 'copilot',
        label: 'Hidden turn prompt',
        payload: {
          id: P3_ID,
          harness: 'copilot',
          turns: [
            { index: 0, spanId: 'span-p3-t0', model: 'm' },
            { index: 1, spanId: 'span-p3-t1', model: 'm' },
          ],
        },
      })
      const recordsForSession = vi.spyOn(store, 'recordsForSession')
      const get = vi.spyOn(store, 'get')
      const bridge = new KyberBridge({ store })
      try {
        // Turn 0: genuine session prompt plus its own occurrence — the
        // hidden turn record stays out, and the future turn stays withheld.
        const first = bridge.assembleTurnContent(P3_ID, 0, Number.POSITIVE_INFINITY)
        expect(first?.sharedContext?.parts).toEqual([
          { text: 'P3_SESSION_PROMPT', spanId: 'span-p3-sess' },
          { text: 'P3_T0_PROMPT', spanId: 'span-p3-t0', turnIndex: 0 },
        ])
        expect(first?.sharedContext?.parts.some((p) => p.text === 'P3_HIDDEN_TURN_PROMPT')).toBe(false)
        expect(first?.sharedContext?.parts.some((p) => p.text === 'P3_T1_PROMPT')).toBe(false)

        // Turn 1: prior and current turn occurrences each appear once, in
        // capture order around the genuine session prompt.
        const second = bridge.assembleTurnContent(P3_ID, 1, Number.POSITIVE_INFINITY)
        expect(second?.sharedContext?.parts).toEqual([
          { text: 'P3_SESSION_PROMPT', spanId: 'span-p3-sess' },
          { text: 'P3_T0_PROMPT', spanId: 'span-p3-t0', turnIndex: 0 },
          { text: 'P3_T1_PROMPT', spanId: 'span-p3-t1', turnIndex: 1 },
        ])
        expect(second?.sharedContext?.parts.some((p) => p.text === 'P3_HIDDEN_TURN_PROMPT')).toBe(false)
        expect(second?.sharedContext?.seenBefore).toBe(true)
        expect(second?.blocks.find((b) => b.key === 'system_prompt')?.text).toBe('P3_T1_PROMPT')
        // The exclusion rides on the narrow op-filtered projection — no
        // full-session record walk pays for it.
        expect(recordsForSession).not.toHaveBeenCalled()
        expect(get).toHaveBeenCalledWith('span-p3-t0')
        expect(get).toHaveBeenCalledWith('span-p3-t1')
      } finally {
        bridge.close()
      }
    } finally {
      store.close()
    }
  })
})
