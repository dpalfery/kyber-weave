import { readFileSync } from 'fs'
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

  it('counts #231 cursor twin overlap output once (file halves + agent)', () => {
    // Live evidence (issue #231, key 0f659701-…): cursor file halves
    // (17007/0, 0/45) plus cursor-agent (125/45) within skew. Exact-counter
    // matching never clusters them, so the share used to sum the overlapping
    // 45 twice (90). A3 join: time skew + complementary/subset counters under
    // the folded cursor share → per-dimension max / prefer-fuller-row; never
    // sum joined counters. Summed output/reportedOutput must be 45, not 90.
    const out = dedupeTwinTurns([
      file('synth:cursor:req', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:28.000Z',
        tokens: counters({
          freshInput: 17007,
          cacheRead: 0,
          cacheCreation: 0,
          output: 0,
          reportedInput: 17007,
          reportedOutput: 0,
        }),
      }),
      file('synth:cursor:res', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: counters({
          freshInput: 0,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 0,
          reportedOutput: 45,
        }),
      }),
      file('synth:cursor-agent:0', {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        timestamp: '2026-09-30T19:52:33.181Z',
        tokens: counters({
          freshInput: 125,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 125,
          reportedOutput: 45,
        }),
      }),
    ])

    const totalOutput = out.reduce((sum, r) => sum + r.tokens.output, 0)
    const totalReportedOutput = out.reduce((sum, r) => sum + r.tokens.reportedOutput, 0)
    expect(totalOutput).toBe(45)
    expect(totalReportedOutput).toBe(45)
  })

  it('does not overlap-join a single Cursor collector without twin evidence (review)', () => {
    // Complementary halves from cursor alone look joinable, but the overlap
    // pass requires evidenced cursor + cursor-agent in the session group.
    const out = dedupeTwinTurns([
      file('synth:cursor:req', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:28.000Z',
        tokens: counters({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
      }),
      file('synth:cursor:res', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: counters({
          freshInput: 200,
          cacheRead: 0,
          cacheCreation: 0,
          output: 20,
          reportedInput: 200,
          reportedOutput: 20,
        }),
      }),
    ])

    expect(out).toHaveLength(2)
    expect(out.reduce((sum, r) => sum + r.tokens.output, 0)).toBe(30)
    expect(out.reduce((sum, r) => sum + r.tokens.reportedInput, 0)).toBe(300)
  })

  it('does not overlap-join an unrelated file-backed harness (review)', () => {
    // buildSessions runs dedupeTwinTurns on every harness group; Claude
    // file-only complementary rows must stay separate (exact-counter OTel+file
    // path is unaffected — this is the #231 pass staying inert).
    const out = dedupeTwinTurns([
      file('synth:claude:a', {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        timestamp: '2026-09-30T19:52:28.000Z',
        tokens: counters({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 10,
          reportedInput: 100,
          reportedOutput: 10,
        }),
      }),
      file('synth:claude:b', {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: counters({
          freshInput: 200,
          cacheRead: 0,
          cacheCreation: 0,
          output: 20,
          reportedInput: 200,
          reportedOutput: 20,
        }),
      }),
    ])

    expect(out).toHaveLength(2)
    expect(out.reduce((sum, r) => sum + r.tokens.output, 0)).toBe(30)
  })

  it('does not let one partial bridge two complete Cursor Agent turns (review)', () => {
    // Two complete agent observations (100/60, 200/45) plus a response half
    // (0/45) that is a subset of both. Union-find would merge all three into
    // 200/60 and under-count output (105 → 60). Bounded matching assigns the
    // partial to one complete only.
    const out = dedupeTwinTurns([
      file('synth:cursor-agent:a', {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        timestamp: '2026-09-30T19:52:28.000Z',
        tokens: counters({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 60,
          reportedInput: 100,
          reportedOutput: 60,
        }),
      }),
      file('synth:cursor-agent:b', {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        timestamp: '2026-09-30T19:52:32.000Z',
        tokens: counters({
          freshInput: 200,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 200,
          reportedOutput: 45,
        }),
      }),
      file('synth:cursor:res', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: counters({
          freshInput: 0,
          cacheRead: 0,
          cacheCreation: 0,
          output: 45,
          reportedInput: 0,
          reportedOutput: 45,
        }),
      }),
    ])

    expect(out).toHaveLength(2)
    const totalOutput = out.reduce((sum, r) => sum + r.tokens.output, 0)
    expect(totalOutput).toBe(105)
    // Partials do not invent a 200/60 max across conflicting completes.
    expect(out.some((r) => r.tokens.freshInput === 200 && r.tokens.output === 60)).toBe(false)
  })

  it('retains merged reasoning when TOKEN_DIMS already match the keeper (review)', () => {
    // Keeper covers the six TOKEN_DIMS; a subset donor still contributes a
    // higher reasoning count that maxTokens merges — retention must compare it.
    const out = dedupeTwinTurns([
      file('synth:cursor-agent:0', {
        source: 'codeburn/cursor-agent',
        harness: 'cursor-agent',
        timestamp: '2026-09-30T19:52:33.000Z',
        tokens: counters({
          freshInput: 100,
          cacheRead: 0,
          cacheCreation: 0,
          output: 50,
          reportedInput: 100,
          reportedOutput: 50,
          reasoning: 5,
        }),
      }),
      file('synth:cursor:res', {
        source: 'codeburn/cursor',
        harness: 'cursor',
        timestamp: '2026-09-30T19:52:30.000Z',
        tokens: counters({
          freshInput: 0,
          cacheRead: 0,
          cacheCreation: 0,
          output: 50,
          reportedInput: 0,
          reportedOutput: 50,
          reasoning: 20,
        }),
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.tokens.reasoning).toBe(20)
    expect(out[0]!.tokens.freshInput).toBe(100)
    expect(out[0]!.tokens.output).toBe(50)
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

describe('dedupeTwinTurns unreported counters (review)', () => {
  it('keeps zero-counter turns from both collectors (review)', () => {
    // An `llm.invoke` that reports no usage stamps 0:0:0:0:0:0 — identical
    // for every such turn. Zero counters are nothing to match on, so the
    // file row is kept even beside an OTel row seconds apart.
    const zero = {
      freshInput: 0,
      cacheRead: 0,
      cacheCreation: 0,
      output: 0,
      reportedInput: 0,
      reportedOutput: 0,
    }
    const out = dedupeTwinTurns([
      llmInvoke('otel-zero', {
        source: 'claude-code-desktop',
        harness: 'claude-code',
        timestamp: '2026-09-23T22:43:53.540Z',
        tokens: { ...zero },
        content: {},
      }),
      llmInvoke('synth:zero', {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        timestamp: '2026-09-23T22:43:58.783Z',
        tokens: { ...zero },
        content: { system_prompt: 'hello' },
        parts: [{ part: 'system_prompt', text: 'hello' }],
      }),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-zero', 'synth:zero'])
  })
})

describe('dedupeTwinTurns surplus file content (review)', () => {
  const same = {
    freshInput: 2,
    cacheRead: 39096,
    cacheCreation: 24977,
    output: 563,
    reportedInput: 64075,
    reportedOutput: 563,
  }
  it('merges content keys from surplus file rows instead of dropping them (review)', () => {
    // The 2×-per-turn file shape (issue #232): one OTLP turn, two file rows
    // carrying complementary halves. Totals stay OTLP-only, but no content
    // key vanishes with the dropped duplicate.
    const out = dedupeTwinTurns([
      llmInvoke('otel-1', {
        source: 'claude-code-desktop',
        harness: 'claude-code',
        timestamp: '2026-09-23T22:43:53.540Z',
        tokens: { ...same },
        content: {},
      }),
      llmInvoke('synth:req', {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        timestamp: '2026-09-23T22:43:58.000Z',
        tokens: { ...same },
        content: { system_prompt: 'hello' },
        parts: [{ part: 'system_prompt', text: 'hello' }],
      }),
      llmInvoke('synth:res', {
        source: 'codeburn/claude-desktop',
        harness: 'claude-desktop',
        timestamp: '2026-09-23T22:43:59.000Z',
        tokens: { ...same },
        content: { tool_result_content: 'world' },
        parts: [{ part: 'tool_result_content', text: 'world' }],
      }),
    ])

    expect(out).toHaveLength(1)
    expect(out[0]!.content).toMatchObject({ system_prompt: 'hello', tool_result_content: 'world' })
  })

  it('bounds clusters by span, not just adjacency (review)', () => {
    // A file row chained more than the skew past the cluster start is not provably the same turn: kept rather than merged and dropped.
    const at = (spanId: string, source: string, harness: string, timestamp: string) =>
      llmInvoke(spanId, { source, harness, timestamp, tokens: { ...same }, content: {} })
    const out = dedupeTwinTurns([
      at('otel-0', 'claude-code-desktop', 'claude-code', '2026-09-23T22:43:00.000Z'),
      at('synth:mid', 'codeburn/claude-desktop', 'claude-desktop', '2026-09-23T22:43:50.000Z'),
      at('synth:far', 'codeburn/claude-desktop', 'claude-desktop', '2026-09-23T22:44:40.000Z'),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-0', 'synth:far'])
  })
})

describe('dedupeTwinTurns — matching must identify a turn (review round 2)', () => {
  it('never merges identical counters reported by two different sessions', () => {
    // Session b contributes a file row only — no OTel twin for it anywhere.
    // Keying on counters alone would drop it as the file half of a pair with
    // session a's OTel row, taking its content with it.
    const out = dedupeTwinTurns([
      llmInvoke('otel-a', { sessionId: 'session-a', content: {} }),
      llmInvoke('synth:b', {
        sessionId: 'session-b',
        source: 'codeburn/claude-desktop',
        content: { system_prompt: 'from-b' },
        parts: [{ part: 'system_prompt', text: 'from-b' }],
      }),
    ])

    expect(out).toHaveLength(2)
    const kept = out.find((r) => r.spanId === 'synth:b')
    expect(kept).toBeDefined()
    expect(kept!.content.system_prompt).toBe('from-b')
  })

  it('bounds a cluster by total span so adjacent pairs cannot chain', () => {
    // The file row is 50 s from the second OTel row but 100 s from the first.
    // Bounding only each step chains them into one cluster and hands the file's
    // content to the row 100 s away while dropping the file row itself.
    const at = (seconds: number) => new Date(Date.parse('2026-09-23T22:43:53.540Z') + seconds * 1000).toISOString()
    const out = dedupeTwinTurns([
      llmInvoke('otel-0', { timestamp: at(0), content: {} }),
      llmInvoke('otel-1', { timestamp: at(50), content: {} }),
      llmInvoke('synth:late', {
        timestamp: at(100),
        source: 'codeburn/claude-desktop',
        content: { system_prompt: 'late' },
        parts: [{ part: 'system_prompt', text: 'late' }],
      }),
    ])

    // No pair is provable, so nothing is dropped: all three rows survive with
    // their own content.
    expect(out).toHaveLength(3)
    expect(out.find((r) => r.spanId === 'synth:late')!.content.system_prompt).toBe('late')
    expect(out.find((r) => r.spanId === 'otel-0')!.content).toEqual({})
  })

  it('keeps the content of surplus file rows instead of discarding it', () => {
    // Issue #232: file rows arrive 2x per turn, so donors outnumber the
    // content-less OTel rows. Both file rows carry different text; neither may
    // be dropped on the floor.
    const out = dedupeTwinTurns([
      llmInvoke('otel-only', { content: {} }),
      llmInvoke('synth:one', {
        source: 'codeburn/claude-desktop',
        content: { system_prompt: 'first' },
        parts: [{ part: 'system_prompt', text: 'first' }],
      }),
      llmInvoke('synth:two', {
        source: 'codeburn/claude-desktop',
        timestamp: '2026-09-23T22:43:55.540Z',
        content: { conversation_history: 'second' },
        parts: [{ part: 'conversation_history', text: 'second' }],
      }),
    ])

    // Counters are still counted once — one keeper row survives.
    expect(out).toHaveLength(1)
    // But neither file row's content was thrown away with it.
    expect(out[0]!.content).toMatchObject({ system_prompt: 'first', conversation_history: 'second' })
  })
})

describe('dedupeTwinTurns nearest pairing (review M2)', () => {
  const turn = (
    spanId: string,
    source: string,
    timestamp: string,
    content: CanonicalRecord['content'],
    parts?: CanonicalRecord['parts'],
  ) =>
    llmInvoke(spanId, {
      source,
      harness: source.startsWith('codeburn/') ? 'claude-desktop' : 'claude-code',
      timestamp,
      tokens: {
        freshInput: 2,
        cacheRead: 39096,
        cacheCreation: 24977,
        output: 563,
        reportedInput: 64075,
        reportedOutput: 563,
      },
      content,
      ...(parts === undefined ? {} : { parts }),
    })

  it('pairs the interleaved case by time, not by index (review M2)', () => {
    // otelB must receive fileB — and only fileB — even though fileA comes
    // first in the donor order. Index pairing gave otelB fileA plus fileB.
    const out = dedupeTwinTurns([
      turn('otel-a', 'claude-code-desktop', '2026-09-23T22:43:00.000Z', { system_prompt: 'a' }, [
        { part: 'system_prompt', text: 'a' },
      ]),
      turn('file-a', 'codeburn/claude-desktop', '2026-09-23T22:43:02.000Z', { system_prompt: 'a' }, [
        { part: 'system_prompt', text: 'a' },
      ]),
      turn('otel-b', 'claude-code-desktop', '2026-09-23T22:43:30.000Z', {}),
      turn('file-b', 'codeburn/claude-desktop', '2026-09-23T22:43:32.000Z', { system_prompt: 'b' }, [
        { part: 'system_prompt', text: 'b' },
      ]),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-a', 'otel-b'])
    // otelA keeps its own content: the twin's row only fills what is absent.
    expect(out[0]!.content).toEqual({ system_prompt: 'a' })
    expect(out[1]!.content).toEqual({ system_prompt: 'b' })
    expect(out[1]!.parts).toEqual([{ part: 'system_prompt', text: 'b' }])
  })

  it('keeps the #232 shape on its own turn (review M2)', () => {
    // Two file rows per OTLP turn: otelB must not receive the other turn's
    // parts even though donors outnumber keepers two to one.
    const out = dedupeTwinTurns([
      turn('otel-a', 'claude-code-desktop', '2026-09-23T22:43:00.000Z', {}),
      turn('f-a1', 'codeburn/claude-desktop', '2026-09-23T22:43:02.000Z', { system_prompt: 'a1' }, [
        { part: 'system_prompt', text: 'a1' },
      ]),
      turn('f-a2', 'codeburn/claude-desktop', '2026-09-23T22:43:04.000Z', { tool_result_content: 'a2' }, [
        { part: 'tool_result_content', text: 'a2' },
      ]),
      turn('otel-b', 'claude-code-desktop', '2026-09-23T22:43:10.000Z', {}),
      turn('f-b1', 'codeburn/claude-desktop', '2026-09-23T22:43:12.000Z', { system_prompt: 'b1' }, [
        { part: 'system_prompt', text: 'b1' },
      ]),
      turn('f-b2', 'codeburn/claude-desktop', '2026-09-23T22:43:14.000Z', { tool_result_content: 'b2' }, [
        { part: 'tool_result_content', text: 'b2' },
      ]),
    ])

    expect(out.map((r) => r.spanId)).toEqual(['otel-a', 'otel-b'])
    const b = out[1]!
    const texts = [...(b.parts ?? [])].map((p) => p.text)
    expect(texts).toContain('b1')
    expect(texts).toContain('b2')
    expect(texts).not.toContain('a1')
    expect(texts).not.toContain('a2')
  })
})

describe('dedupeTwinTurns window boundary (review)', () => {
  it('pins the 60 s straddle: a twin pair past the cluster span is kept twice (review)', () => {
    // An earlier same-counter row anchors the cluster; a genuine twin pair
    // arriving more than the skew past that anchor starts its own cluster.
    // The pair still collapses within its own cluster — but a lone row past
    // the span has no provable mate and is kept, even if that double-counts
    // a coincidence. This test pins the boundary, not the wish.
    const same = {
      freshInput: 2,
      cacheRead: 39096,
      cacheCreation: 24977,
      output: 563,
      reportedInput: 64075,
      reportedOutput: 563,
    }
    const at = (spanId: string, source: string, timestamp: string) =>
      llmInvoke(spanId, {
        source,
        harness: source.startsWith('codeburn/') ? 'claude-desktop' : 'claude-code',
        timestamp,
        tokens: { ...same },
        content: {},
      })
    const out = dedupeTwinTurns([
      at('otel-old', 'claude-code-desktop', '2026-09-23T22:43:00.000Z'),
      at('otel-new', 'claude-code-desktop', '2026-09-23T22:44:05.000Z'),
      at('synth-new', 'codeburn/claude-desktop', '2026-09-23T22:44:08.000Z'),
    ])

    // otel-new + synth-new collapse (3 s apart, own cluster); otel-old stands
    // alone: nothing provable to merge it with.
    expect(out.map((r) => r.spanId).sort()).toEqual(['otel-new', 'otel-old'])
  })
})

describe('dedupeTwinTurns share-key fallback (review)', () => {
  it('collapses twins when one side omits the session attribute (review)', () => {
    // A share routinely mixes rows with session_id set and rows where only
    // the trace carried the key. Keying on the record attribute alone would
    // bucket the OTel row under '' and its file twin under the id — the
    // exact double count #182 exists to kill, invisible in every total.
    const same = {
      freshInput: 2,
      cacheRead: 39096,
      cacheCreation: 24977,
      output: 563,
      reportedInput: 64075,
      reportedOutput: 563,
    }
    const out = dedupeTwinTurns(
      [
        llmInvoke('otel-1', {
          source: 'claude-code-desktop',
          harness: 'claude-code',
          sessionId: undefined,
          timestamp: '2026-09-23T22:43:53.540Z',
          tokens: { ...same },
          content: {},
        }),
        llmInvoke('synth:aaa', {
          source: 'codeburn/claude-desktop',
          harness: 'claude-desktop',
          sessionId: 'twin-key',
          timestamp: '2026-09-23T22:43:58.783Z',
          tokens: { ...same },
          content: {},
        }),
      ],
      'twin-key',
    )

    expect(out.map((r) => r.spanId)).toEqual(['otel-1'])
  })
})

describe('dedupeTwinTurns merged content (review)', () => {
  it('derives fused content from fused parts, not first-wins (review)', () => {
    // Two surplus file rows sharing one bucket key with different text: the
    // fused donor's content must carry both, matching its fused parts —
    // content is documented as the collapsed form of exactly the parts.
    const same = {
      freshInput: 2,
      cacheRead: 39096,
      cacheCreation: 24977,
      output: 563,
      reportedInput: 64075,
      reportedOutput: 563,
    }
    const out = dedupeTwinTurns(
      [
        llmInvoke('otel-1', {
          source: 'claude-code-desktop',
          harness: 'claude-code',
          timestamp: '2026-09-23T22:43:53.540Z',
          tokens: { ...same },
          content: {},
        }),
        llmInvoke('synth:one', {
          source: 'codeburn/claude-desktop',
          harness: 'claude-desktop',
          timestamp: '2026-09-23T22:43:58.000Z',
          tokens: { ...same },
          content: { system_prompt: 'first' },
          parts: [{ part: 'system_prompt', text: 'first' }],
        }),
        llmInvoke('synth:two', {
          source: 'codeburn/claude-desktop',
          harness: 'claude-desktop',
          timestamp: '2026-09-23T22:43:59.000Z',
          tokens: { ...same },
          content: { system_prompt: 'second' },
          parts: [{ part: 'system_prompt', text: 'second' }],
        }),
      ],
      'twin-key',
    )

    expect(out).toHaveLength(1)
    expect(out[0]!.content.system_prompt).toContain('first')
    expect(out[0]!.content.system_prompt).toContain('second')
  })
})

// The clustering window is documented on the constant, not derived from the
// loop: only the total span is bounded (the step check was removed with
// a689157, since rows arrive time-ordered and the span always covers the
// neighbour gap). The module is read as text so the docblock cannot promise a
// bound the code no longer applies — a stale guarantee here is what a future
// reader would size their reasoning off.
describe('TWIN_TURN_MAX_SKEW_MS docblock states the bound that exists', () => {
  // Sliced to the constant's own docblock, not the whole module: a `total span`
  // elsewhere in the file would otherwise satisfy the positive half on wording
  // this docblock never carried. The slice starts at the nearest `/**` before
  // the declaration, so the preceding declaration's docblock stays out.
  const moduleText = readFileSync(new URL('./twin-dedupe.ts', import.meta.url), 'utf8')
  const marker = moduleText.indexOf('export const TWIN_TURN_MAX_SKEW_MS')
  const docblockStart = moduleText.lastIndexOf('/**', marker)
  // Docblock prefixes are stripped after slicing: the claim spans ` * `
  // continuations, so a raw match misses it and the guard would pass on exactly
  // the wording it exists to reject. That stripping is an extension of the
  // source-as-text guard in pricing/data/pricing-fallback-data.test.ts, which
  // reads a file as text but not through a docblock prefix.
  const docblock = moduleText
    .slice(docblockStart, moduleText.lastIndexOf('*/', marker))
    .replace(/\n\s*\*/g, ' ')
    .replace(/\s+/g, ' ')

  it('bounds a cluster by total span and never claims a per-step bound', () => {
    expect(docblock).toMatch(/total span/)
    // Broadened from the one retired phrasing: any per-step claim is the defect,
    // whatever words it is phrased in.
    expect(docblock).not.toMatch(/each step between/)
  })
})
