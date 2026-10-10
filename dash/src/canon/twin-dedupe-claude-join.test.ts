import { describe, expect, it } from 'vitest'

import { dedupeTwinTurns } from './twin-dedupe.js'
import type { CanonicalRecord, ContentPart, TokenUsage } from './types.js'

// Claude Code's OTel `llm_request` row and its transcript-synthesized twin
// describe one API call. The OTel row stamps `gen_ai.response.id`; the file
// row carries the transcript's `message.id` as its native record id. When the
// two agree the turn is identified outright — no counter equality, no skew
// window — and D10 decides content per bucket, OTel first.

const SESSION = 'claude-session-synthetic'

const countersA: TokenUsage = {
  freshInput: 3,
  cacheRead: 41000,
  cacheCreation: 1200,
  output: 310,
  reportedInput: 42203,
  reportedOutput: 310,
}

const countersB: TokenUsage = {
  freshInput: 3,
  cacheRead: 40000,
  cacheCreation: 900,
  output: 290,
  reportedInput: 40903,
  reportedOutput: 290,
}

function otelTurn(spanId: string, over: Partial<CanonicalRecord> & { responseId?: string } = {}): CanonicalRecord {
  const { responseId, ...rest } = over
  return {
    spanId,
    traceId: 'a1b2c3d4e5f60718293a4b5c6d7e8f90',
    parentSpanId: null,
    source: 'claude-code',
    harness: 'claude-code',
    sessionId: SESSION,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-01T12:00:00.000Z',
    durationMs: 1200,
    status: 'ok',
    tokens: { ...countersA },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      'session.id': SESSION,
      ...(responseId === undefined ? {} : { 'gen_ai.response.id': responseId }),
    },
    ...rest,
  }
}

function fileTurn(
  spanId: string,
  over: Partial<CanonicalRecord> & { nativeRecordId?: string; recordDigest?: string } = {},
): CanonicalRecord {
  const { nativeRecordId, recordDigest, ...rest } = over
  return {
    spanId,
    traceId: `synth:claude-code:${SESSION}`,
    parentSpanId: null,
    source: 'codeburn/claude',
    harness: 'claude-code',
    sessionId: SESSION,
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-10-01T12:00:04.000Z',
    durationMs: 0,
    status: 'ok',
    tokens: { ...countersB },
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    raw: {
      provenance: {
        harnessId: 'claude-code',
        nativeSessionId: SESSION,
        ...(nativeRecordId === undefined ? {} : { nativeRecordId }),
        ...(recordDigest === undefined ? {} : { recordDigest }),
      },
    },
    ...rest,
  }
}

function withParts(parts: ContentPart[]): Pick<CanonicalRecord, 'parts' | 'content'> {
  const content: CanonicalRecord['content'] = {}
  for (const part of parts) {
    content[part.part] = content[part.part] === undefined ? part.text : `${content[part.part]}\n${part.text}`
  }
  return { parts, content }
}

const history: ContentPart[] = [
  { part: 'conversation_history', text: 'user: synthetic question' },
  { part: 'conversation_history', text: 'assistant: synthetic answer' },
]

describe('dedupeTwinTurns Claude OTel-to-transcript id join', () => {
  it('joins on gen_ai.response.id = message.id with OTel counters and file parts', () => {
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1' }),
      fileTurn('synth:claude-code:s:msg_1', { nativeRecordId: 'msg_1', ...withParts(history) }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.spanId).toBe('otel-1')
    // Counters come from OTel — never the file's, never a sum or a max.
    expect(out[0]!.tokens).toEqual(countersA)
    expect(out[0]!.parts).toEqual(history)
    expect(out[0]!.content).toEqual({ conversation_history: 'user: synthetic question\nassistant: synthetic answer' })
  })

  it('joins regardless of the skew window', () => {
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1' }),
      fileTurn('synth:claude-code:s:msg_1', {
        nativeRecordId: 'msg_1',
        timestamp: '2026-10-01T12:05:00.000Z',
        ...withParts(history),
      }),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-1'])
    expect(out[0]!.tokens).toEqual(countersA)
    expect(out[0]!.parts).toEqual(history)
  })

  it('resolves an omitted session attribute to the share key', () => {
    const out = dedupeTwinTurns(
      [
        otelTurn('otel-1', { responseId: 'msg_1', sessionId: undefined }),
        fileTurn('synth:claude-code:s:msg_1', { nativeRecordId: 'msg_1', ...withParts(history) }),
      ],
      SESSION,
    )

    expect(out.map((r) => r.spanId)).toEqual(['otel-1'])
  })

  it('does not join mismatched ids', () => {
    const otel = otelTurn('otel-1', { responseId: 'msg_1' })
    const file = fileTurn('synth:claude-code:s:msg_2', { nativeRecordId: 'msg_2', ...withParts(history) })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toEqual([otel, file])
  })

  it('does not treat a digest fallback as a native id', () => {
    // A transcript turn with no `message.id` gets a digest identity; it names
    // nothing the OTel row could carry, so it never proves a join.
    const otel = otelTurn('otel-1', { responseId: 'deadbeefdeadbeef' })
    const file = fileTurn('synth:claude-code:s:deadbeefdeadbeef', {
      nativeRecordId: 'deadbeefdeadbeef',
      recordDigest: 'deadbeefdeadbeef',
      ...withParts(history),
    })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toEqual([otel, file])
  })

  it('does not join the same id across sessions', () => {
    const otel = otelTurn('otel-1', { responseId: 'msg_1', sessionId: 'claude-session-other' })
    const file = fileTurn('synth:claude-code:s:msg_1', { nativeRecordId: 'msg_1', ...withParts(history) })
    const out = dedupeTwinTurns([otel, file])

    expect(out).toEqual([otel, file])
  })
})

describe('dedupeTwinTurns per-bucket precedence, OTel first (D10)', () => {
  it('fills only the buckets the OTel record lacks, each from one source', () => {
    const otelParts: ContentPart[] = [
      { part: 'system_prompt', text: 'synthetic system prompt' },
      { part: 'tool_definitions', text: '{"name":"synthetic_tool"}' },
    ]
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1', ...withParts(otelParts) }),
      fileTurn('synth:claude-code:s:msg_1', {
        nativeRecordId: 'msg_1',
        ...withParts([{ part: 'system_prompt', text: 'file system prompt' }, ...history]),
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual([...otelParts, ...history])
    expect(out[0]!.content).toEqual({
      system_prompt: 'synthetic system prompt',
      tool_definitions: '{"name":"synthetic_tool"}',
      conversation_history: 'user: synthetic question\nassistant: synthetic answer',
    })
  })

  it('keeps only the OTel parts when both carry the same bucket (id join)', () => {
    const otelHistory: ContentPart[] = [{ part: 'conversation_history', text: 'user: otel view' }]
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1', ...withParts(otelHistory) }),
      fileTurn('synth:claude-code:s:msg_1', { nativeRecordId: 'msg_1', ...withParts(history) }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual(otelHistory)
    expect(out[0]!.content).toEqual({ conversation_history: 'user: otel view' })
  })

  it('keeps only the OTel parts when both carry the same bucket (counter join)', () => {
    // D10 governs every OTel-and-file pairing, not only the id join.
    const otelHistory: ContentPart[] = [{ part: 'conversation_history', text: 'user: otel view' }]
    const out = dedupeTwinTurns([
      otelTurn('otel-1', withParts(otelHistory)),
      fileTurn('synth:claude-code:s:digest', { tokens: { ...countersA }, ...withParts(history) }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual(otelHistory)
    expect(out[0]!.content).toEqual({ conversation_history: 'user: otel view' })
  })

  it('applies to flat content when the OTel record carries no parts', () => {
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1', content: { conversation_history: 'otel flat' } }),
      fileTurn('synth:claude-code:s:msg_1', { nativeRecordId: 'msg_1', ...withParts(history) }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts ?? []).toEqual([])
    expect(out[0]!.content).toEqual({ conversation_history: 'otel flat' })
  })
})

describe('dedupeTwinTurns D10 flat-only buckets survive the merge (T6 rework 1)', () => {
  it('promotes an OTel flat-only bucket when the file fills another bucket', () => {
    const fileSys = 'file system prompt'
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1', content: { conversation_history: 'otel flat' } }),
      fileTurn('synth:claude-code:s:msg_1', {
        nativeRecordId: 'msg_1',
        ...withParts([
          { part: 'conversation_history', text: 'user: synthetic question' },
          { part: 'system_prompt', text: fileSys },
        ]),
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual([
      { part: 'conversation_history', text: 'otel flat' },
      { part: 'system_prompt', text: fileSys },
    ])
    expect(out[0]!.content).toEqual({
      conversation_history: 'otel flat',
      system_prompt: fileSys,
    })
  })

  it('promotes a filled file flat-only bucket when the OTel record has parts', () => {
    const otelSys: ContentPart[] = [{ part: 'system_prompt', text: 'synthetic system prompt' }]
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'msg_1', ...withParts(otelSys) }),
      fileTurn('synth:claude-code:s:msg_1', {
        nativeRecordId: 'msg_1',
        content: { conversation_history: 'file history flat' },
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.parts).toEqual([
      { part: 'system_prompt', text: 'synthetic system prompt' },
      { part: 'conversation_history', text: 'file history flat' },
    ])
    // One source per bucket (D10): system_prompt from OTel, history from the file.
    expect(out[0]!.content).toEqual({
      system_prompt: 'synthetic system prompt',
      conversation_history: 'file history flat',
    })
  })

  it('keeps every content bucket visible in parts whenever parts is non-empty', () => {
    const cases = [
      dedupeTwinTurns([
        otelTurn('otel-1', { responseId: 'msg_1', content: { conversation_history: 'otel flat' } }),
        fileTurn('synth:claude-code:s:msg_1', {
          nativeRecordId: 'msg_1',
          ...withParts([
            { part: 'conversation_history', text: 'user: synthetic question' },
            { part: 'system_prompt', text: 'file system prompt' },
          ]),
        }),
      ]),
      dedupeTwinTurns([
        otelTurn(
          'otel-1',
          { responseId: 'msg_1', ...withParts([{ part: 'system_prompt', text: 'synthetic system prompt' }]) },
        ),
        fileTurn('synth:claude-code:s:msg_1', {
          nativeRecordId: 'msg_1',
          content: { conversation_history: 'file history flat' },
        }),
      ]),
    ]
    for (const out of cases) {
      expect(out).toHaveLength(1)
      const merged = out[0]!
      if ((merged.parts ?? []).length > 0) {
        for (const bucket of Object.keys(merged.content)) {
          expect(merged.parts!.some((part) => part.part === bucket)).toBe(true)
        }
      }
    }
  })

  it('still pairs identical counters with different ids through the exact-counter path', () => {
    // Intentional: real Claude OTel ids are request ids, not message ids, so
    // rows that name different ids still join when their counters agree (#182).
    const out = dedupeTwinTurns([
      otelTurn('otel-1', { responseId: 'req_abc' }),
      fileTurn('synth:claude-code:s:msg_xyz', {
        nativeRecordId: 'msg_xyz',
        tokens: { ...countersA },
        ...withParts(history),
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.spanId).toBe('otel-1')
    expect(out[0]!.tokens).toEqual(countersA)
    expect(out[0]!.parts).toEqual(history)
  })
})
