import { describe, expect, it } from 'vitest'

import { dedupeTwinTurns } from './twin-dedupe.js'
import type { CanonicalRecord } from './types.js'

// Issue #182, Condition 1 (live evidence 2026-09-30): one Claude session key
// arrives twice — 6 `llm.invoke` rows under `claude-code` (OTLP source,
// bare-hex span ids) plus 6 under `claude-desktop` (file source, `synth:`
// span ids) — with byte-identical token payloads per turn (reportedInput
// 64075/65863/66314 under both harnesses, ~2-6 s apart). A bare harness fold
// would sum the same turn twice. Per ADR 0009 D4, counters come from the OTel
// row and values are never summed across sources for the same turn.

function counters(over: Partial<CanonicalRecord['tokens']> = {}): CanonicalRecord['tokens'] {
  return {
    freshInput: 2,
    cacheRead: 39096,
    cacheCreation: 24977,
    output: 563,
    reportedInput: 64075,
    reportedOutput: 563,
    ...over,
  }
}

function llmInvoke(spanId: string, over: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId,
    traceId: 'trace-twin',
    parentSpanId: null,
    source: 'claude-code-desktop',
    harness: 'claude-code',
    sessionId: 'twin-key',
    name: 'llm_request',
    op: 'llm.invoke',
    kind: 'client',
    timestamp: '2026-09-23T22:43:53.540Z',
    durationMs: 1000,
    status: 'ok',
    tokens: counters(),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...over,
  }
}

const otel = (spanId: string, over: Partial<CanonicalRecord> = {}) =>
  llmInvoke(spanId, { source: 'claude-code-desktop', harness: 'claude-code', ...over })

const file = (spanId: string, over: Partial<CanonicalRecord> = {}) =>
  llmInvoke(spanId, {
    source: 'codeburn/claude-desktop',
    harness: 'claude-desktop',
    timestamp: '2026-09-23T22:43:58.783Z',
    ...over,
  })

describe('dedupeTwinTurns (ADR 0009 D4 source precedence)', () => {
  it('collapses one turn observed by OTLP and file collectors to the OTLP row', () => {
    const out = dedupeTwinTurns([
      otel('otel-1'),
      file('synth:claude-desktop:twin-key:aaa'),
      file('synth:claude-desktop:twin-key:bbb'),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-1'])
  })

  it('keeps merged session totals equal to the OTLP-only totals', () => {
    const second = counters({
      freshInput: 2,
      cacheRead: 64073,
      cacheCreation: 1788,
      output: 245,
      reportedInput: 65863,
      reportedOutput: 245,
    })
    const out = dedupeTwinTurns([
      otel('otel-1'),
      file('synth:aaa', { timestamp: '2026-09-23T22:43:58.783Z' }),
      otel('otel-2', { tokens: second, timestamp: '2026-09-23T22:44:00.517Z' }),
      file('synth:bbb', { tokens: second, timestamp: '2026-09-23T22:44:01.960Z' }),
    ])

    expect(out).toHaveLength(2)
    const total = out.reduce((sum, r) => sum + r.tokens.reportedInput, 0)
    expect(total).toBe(64075 + 65863)
  })

  it('transplants file content onto a content-less OTLP turn', () => {
    const out = dedupeTwinTurns([
      otel('otel-1', { content: {} }),
      file('synth:aaa', {
        content: { system_prompt: 'hello' },
        parts: [{ part: 'system_prompt', text: 'hello' }],
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.content).toEqual({ system_prompt: 'hello' })
    expect(out[0]!.parts).toEqual([{ part: 'system_prompt', text: 'hello' }])
  })

  it('leaves disjoint turns from twin collectors untouched (cursor-style)', () => {
    // Live evidence: cursor file rows (17007/0, 0/45) and the cursor-agent row
    // (125/45) share a key but describe different observations — distinct
    // counters, so the fold sums the union without inflation.
    const out = dedupeTwinTurns([
      file('synth:cursor:1', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        tokens: counters({ freshInput: 17007, cacheRead: 0, cacheCreation: 0, output: 0, reportedInput: 17007, reportedOutput: 0 }),
      }),
      file('synth:cursor-agent:0', {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        timestamp: '2026-09-30T19:52:33.181Z',
        tokens: counters({ freshInput: 125, cacheRead: 0, cacheCreation: 0, output: 45, reportedInput: 125, reportedOutput: 45 }),
      }),
    ])

    expect(out).toHaveLength(2)
  })

  it('never drops same-kind rows: two identical OTLP turns are kept', () => {
    // Conservative by construction: only a file row paired with a non-file
    // row is provably the same turn observed twice (ADR 0009 D4). Two
    // identical OTLP rows could be genuine retries.
    const out = dedupeTwinTurns([otel('otel-1'), otel('otel-2', { timestamp: '2026-09-23T22:43:55.000Z' })])

    expect(out).toHaveLength(2)
  })

  it('ignores non-turn records and preserves input order', () => {
    const tool = llmInvoke('tool-1', { op: 'tool.invoke', source: 'codeburn/claude-desktop', harness: 'claude-desktop' })
    const out = dedupeTwinTurns([tool, otel('otel-1'), file('synth:aaa')])

    expect(out.map((r) => r.spanId)).toEqual(['tool-1', 'otel-1'])
  })
})

describe('dedupeTwinTurns repeated identical turns (review)', () => {
  const same = {
    freshInput: 2,
    cacheRead: 39096,
    cacheCreation: 24977,
    output: 563,
    reportedInput: 64075,
    reportedOutput: 563,
  }
  const otelAt = (spanId: string, timestamp: string) =>
    llmInvoke(spanId, {
      source: 'claude-code-desktop',
      harness: 'claude-code',
      timestamp,
      tokens: { ...same },
      content: {},
    })
  const fileAt = (spanId: string, timestamp: string, text?: string) =>
    llmInvoke(spanId, {
      source: 'codeburn/claude-desktop',
      harness: 'claude-desktop',
      timestamp,
      tokens: { ...same },
      ...(text === undefined ? { content: {} } : { content: { system_prompt: text }, parts: [{ part: 'system_prompt', text }] }),
    })

  it('collapses every repeated twin pair, not just the first timestamp cluster', () => {
    // Same counters observed twice, five minutes apart: two same-turn pairs,
    // not one pair plus two keepers.
    const out = dedupeTwinTurns([
      otelAt('otel-1', '2026-09-23T22:43:53.540Z'),
      fileAt('synth:aaa', '2026-09-23T22:43:58.783Z'),
      otelAt('otel-2', '2026-09-23T22:48:53.540Z'),
      fileAt('synth:bbb', '2026-09-23T22:48:58.783Z'),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-1', 'otel-2'])
  })

  it('pairs file content onto the nearest OTLP turn one-to-one', () => {
    // Two genuine OTLP turns with identical counters, each with its own file
    // observation: neither turn may lose the other's content.
    const out = dedupeTwinTurns([
      otelAt('otel-1', '2026-09-23T22:43:53.540Z'),
      fileAt('synth:aaa', '2026-09-23T22:43:58.000Z', 'first turn context'),
      otelAt('otel-2', '2026-09-23T22:44:03.540Z'),
      fileAt('synth:bbb', '2026-09-23T22:44:08.000Z', 'second turn context'),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-1', 'otel-2'])
    expect(out[0]!.parts).toEqual([{ part: 'system_prompt', text: 'first turn context' }])
    expect(out[1]!.parts).toEqual([{ part: 'system_prompt', text: 'second turn context' }])
  })
})
