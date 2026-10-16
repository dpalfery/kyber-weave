// Pi tool-record derivation from the OTLP path.
//
// Pi's collector emits one span per turn (`pi.llm.request`) and carries the
// whole conversation inline as `gen_ai.input.messages`. The tool calls and
// their results live inside that JSON, but no child span is emitted for them,
// so the store had 19 `llm.invoke` Pi records and zero `tool.invoke` ones.
// These tests pin the derivation that reads the calls back out and reuses the
// synthesizer's one tool-record mechanism (`synthesizeToolCall`) to build them.
//
// The load-bearing hazards the tests exist for:
//   * the same conversation prefix repeats on every turn span of a session, so
//     per-record derivation without a parent-independent identity would store
//     one copy of each call per turn (measured at ~8x);
//   * once the conversation passes the emitter's 262144-character cap the
//     messages are no longer valid JSON, so the scan must count only the
//     complete objects it can see and say out loud that the list is partial.

import { describe, expect, it } from 'vitest'

import type { OtlpSpan } from '../../otel/receiver.js'
import { ingestBatch } from '../ingest.js'
import { CanonStore } from '../store.js'
import { piAdapter } from './pi.js'
import { derivePiToolRecords, extractPiToolMessages, PI_TOOL_SPAN_PREFIX } from './pi-tools.js'
import { rawSpan } from './testing.js'

// ---------------------------------------------------------------------------
// Fixtures in the shape @dpalfery/pi-statusline exports
// ---------------------------------------------------------------------------

const toolCallPart = (id: string, name: string, args: Record<string, unknown>): unknown => ({
  type: 'tool_call',
  raw: { type: 'toolCall', id, name, arguments: args },
})

const toolResultPart = (id: string, name: string, text: string, isError = false): unknown => ({
  type: 'tool_result',
  raw: { toolCallId: id, toolName: name, content: [{ type: 'text', text }], isError },
})

/** A complete, parseable conversation with two tool calls and both results. */
const CONVERSATION = JSON.stringify([
  { role: 'user', parts: [{ type: 'text', text: 'please read the file' }] },
  {
    role: 'assistant',
    parts: [toolCallPart('call_a', 'read', { path: '/a' }), toolCallPart('call_b', 'bash', { cmd: 'ls' })],
  },
  {
    role: 'toolResult',
    parts: [toolResultPart('call_a', 'read', 'file contents'), toolResultPart('call_b', 'bash', 'boom', true)],
  },
])

// Both session keys are present because the collector emits both
// (`collector-request.json` in canon/fixtures/pi-collector). Only
// `gen_ai.session.id` is read back: it is the key in SESSION_ID_KEYS
// (adapters/copilot.ts), and `pi.session.id` alone resolves to no session.
const PI_USAGE = {
  'pi.session.id': 'sess-1',
  'gen_ai.session.id': 'sess-1',
  'gen_ai.usage.input_tokens': 500,
  'gen_ai.usage.output_tokens': 120,
} as const

const parentFor = (attributes: Record<string, unknown>, spanId = 'span-1') => {
  const raw = rawSpan({ spanId, attributes })
  return { raw, record: piAdapter.normalize(raw) }
}

// ---------------------------------------------------------------------------
// Extraction — the tolerant scan
// ---------------------------------------------------------------------------

describe('extractPiToolMessages', () => {
  it('reads every tool call and result out of a complete conversation', () => {
    const { calls, results, truncated } = extractPiToolMessages(CONVERSATION)

    expect(calls.map((call) => [call.id, call.name])).toEqual([
      ['call_a', 'read'],
      ['call_b', 'bash'],
    ])
    expect(calls[0]!.arguments).toEqual({ path: '/a' })
    expect(results.map((result) => [result.toolCallId, result.content, result.isError])).toEqual([
      ['call_a', 'file contents', undefined],
      ['call_b', 'boom', true],
    ])
    expect(truncated).toBe(false)
  })

  it('does not mistake a tool_call marker inside result text for a real call', () => {
    // The marker is only ever real at the part boundary (`{"type":"tool_call"`).
    // Inside a JSON string the quotes are escaped, so it cannot collide.
    const text = JSON.stringify([
      {
        role: 'toolResult',
        parts: [toolResultPart('call_a', 'read', 'literal "type":"tool_call" inside a file')],
      },
    ])
    const { calls } = extractPiToolMessages(text)
    expect(calls).toEqual([])
  })

  it('counts only the complete calls and reports truncation at the 262144-char cap', () => {
    // The conversation grew past the collector's cap and was cut mid-object:
    // the third call (call_partial) never closes. The scan must recover the
    // two complete calls, drop the half-written one, and say it is partial.
    const stem =
      '[{"role":"assistant","parts":[' +
      JSON.stringify(toolCallPart('call_a', 'read', { path: '/a' })) +
      ',' +
      JSON.stringify(toolCallPart('call_b', 'bash', { cmd: 'ls' })) +
      ',' +
      '{"type":"tool_call","raw":{"type":"toolCall","id":"call_partial","name":"read","arguments":{"path":"'
    const truncatedText = stem + 'A'.repeat(262144 - stem.length)

    expect(truncatedText.length).toBe(262144)

    const { calls, truncated } = extractPiToolMessages(truncatedText)
    expect(calls.map((call) => call.id)).toEqual(['call_a', 'call_b'])
    expect(truncated).toBe(true)
  })

  it('matches a part whose colons are followed by a space', () => {
    // The emitter may serialize with spaces. Matching only the tight spelling
    // dropped every tool call in the conversation without saying so — the worst
    // shape of failure, because the result looked like a complete empty one.
    const spaced = JSON.stringify([
      { role: 'assistant', parts: [toolCallPart('call_a', 'read', { path: '/a' })] },
    ]).replace(/"(\w+)":/g, '"$1": ')

    const { calls, truncated } = extractPiToolMessages(spaced)
    expect(calls.map((call) => call.id)).toEqual(['call_a'])
    expect(truncated).toBe(false)
  })

  it('reports a lower bound when the cap lands between two objects', () => {
    // Cut *after* a complete object: nothing is half-written, so the object
    // scan alone cannot tell this from a complete conversation. Only the
    // payload's own parse can, and without it the count reads as exact.
    const tail =
      '[{"role":"assistant","parts":[' + JSON.stringify(toolCallPart('call_a', 'read', { path: '/a' }))
    expect(() => JSON.parse(tail)).toThrow()

    const { calls, truncated } = extractPiToolMessages(tail)
    expect(calls.map((call) => call.id)).toEqual(['call_a'])
    expect(truncated).toBe(true)
  })
})

// ---------------------------------------------------------------------------
// Derivation — reuse of synthesizeToolCall
// ---------------------------------------------------------------------------

describe('derivePiToolRecords', () => {
  it('synthesizes a tool.invoke record per call, pairing results and status', () => {
    const { raw, record } = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION })
    const records = derivePiToolRecords(record, raw)

    expect(records).toHaveLength(2)
    expect(records.map((r) => r.op)).toEqual(['tool.invoke', 'tool.invoke'])
    expect(records.map((r) => r.harness)).toEqual(['pi', 'pi'])
    expect(records.map((r) => r.name)).toEqual(['read', 'bash'])
    expect(records.map((r) => r.status)).toEqual(['ok', 'error'])
    expect(records[0]!.parts?.[0]).toMatchObject({ part: 'tool_result_content', text: 'file contents' })
    expect(records[0]!.parentSpanId).toBe('span-1')
    expect(records[0]!.sessionId).toBe('sess-1')
    expect(records[0]!.attributes).toMatchObject({ 'gen_ai.tool.name': 'read', 'gen_ai.tool.call_id': 'call_a' })
  })

  it('keys identity on (session, call id) so a repeated prefix yields the same span ids', () => {
    // Every turn span of a session carries the same (capped) conversation
    // prefix, so the same call is derived once per turn. The identity must not
    // depend on the parent, or the store cannot collapse the copies (R2.5).
    const first = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION }, 'turn-1')
    const later = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION }, 'turn-9')

    const a = derivePiToolRecords(first.record, first.raw)
    const b = derivePiToolRecords(later.record, later.raw)

    expect(a.map((r) => r.spanId)).toEqual(b.map((r) => r.spanId))
    expect(a[0]!.spanId.startsWith(PI_TOOL_SPAN_PREFIX)).toBe(true)
  })

  it('keys an id-less call on what it is, not on the turn that carried it', () => {
    // An id-less call has no stable id to key on, and falling back to the
    // ordinal *within the parent* re-filed it once per turn — exactly the
    // duplication the id-ful case was fixed for.
    const noId = JSON.stringify([
      {
        role: 'assistant',
        parts: [{ type: 'tool_call', raw: { type: 'toolCall', name: 'read', arguments: { path: '/a' } } }],
      },
    ])
    const first = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': noId }, 'turn-1')
    const later = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': noId }, 'turn-9')

    const a = derivePiToolRecords(first.record, first.raw)
    const b = derivePiToolRecords(later.record, later.raw)

    expect(a).toHaveLength(1)
    expect(a[0]!.spanId).toBe(b[0]!.spanId)
  })

  it('stamps a lower-bound note on a truncated conversation and none on a complete one', () => {
    const complete = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION })
    expect(derivePiToolRecords(complete.record, complete.raw)[0]!.measurability?.tool_calls).toBeUndefined()

    const stem =
      '[{"role":"assistant","parts":[' +
      JSON.stringify(toolCallPart('call_a', 'read', { path: '/a' })) +
      ',' +
      '{"type":"tool_call","raw":{"type":"toolCall","id":"call_partial","name":"read","arguments":{"path":"'
    const truncatedText = stem + 'A'.repeat(262144 - stem.length)
    const cut = parentFor({ ...PI_USAGE, 'gen_ai.input.messages': truncatedText })
    const records = derivePiToolRecords(cut.record, cut.raw)

    expect(records).toHaveLength(1)
    expect(records[0]!.measurability?.tool_calls).toMatchObject({ availability: 'not_measurable' })
    const note = records[0]!.measurability?.tool_calls
    expect(typeof note === 'object' && note.reason).toMatch(/truncat/i)
  })

  it('derives nothing when the span carries no messages or no calls', () => {
    const empty = parentFor({ ...PI_USAGE })
    expect(derivePiToolRecords(empty.record, empty.raw)).toEqual([])

    const noCalls = parentFor({
      ...PI_USAGE,
      'gen_ai.input.messages': JSON.stringify([{ role: 'user', parts: [{ type: 'text', text: 'hi' }] }]),
    })
    expect(derivePiToolRecords(noCalls.record, noCalls.raw)).toEqual([])
  })
})

// ---------------------------------------------------------------------------
// The ingest path — the records actually land
// ---------------------------------------------------------------------------

function piSpan(attributes: Record<string, unknown>, over: Partial<OtlpSpan> = {}): OtlpSpan {
  return {
    spanId: 'span-1',
    traceId: 'trace-1',
    parentSpanId: null,
    name: 'pi.llm.request',
    kind: 'internal',
    timestamp: '2026-10-16T20:32:43.000Z',
    durationMs: 100,
    status: { code: 'ok' },
    resource: { 'service.name': 'pi-abc123' },
    attributes,
    ...over,
  } as OtlpSpan
}

describe('ingestBatch — Pi tool records', () => {
  it('stores a tool.invoke record for each inline tool call', () => {
    const store = new CanonStore(':memory:')
    try {
      ingestBatch(
        [piSpan({ ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION })],
        store,
      )
      const tools = store.recordsForSession('sess-1').filter((record) => record.op === 'tool.invoke')

      expect(tools.map((record) => record.name).sort()).toEqual(['bash', 'read'])
      expect(tools.every((record) => record.harness === 'pi')).toBe(true)
      expect(tools.every((record) => record.sessionId === 'sess-1')).toBe(true)
    } finally {
      store.close()
    }
  })

  it('does not inflate the count when later turns repeat the same prefix', () => {
    const store = new CanonStore(':memory:')
    try {
      const attributes = { ...PI_USAGE, 'gen_ai.input.messages': CONVERSATION }
      ingestBatch([piSpan(attributes, { spanId: 'turn-1' })], store)
      ingestBatch(
        [piSpan(attributes, { spanId: 'turn-2', timestamp: '2026-10-16T20:33:43.000Z' })],
        store,
      )

      const tools = store.recordsForSession('sess-1').filter((record) => record.op === 'tool.invoke')
      expect(tools).toHaveLength(2)
    } finally {
      store.close()
    }
  })
})
