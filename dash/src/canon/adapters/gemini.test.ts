import { describe, expect, it } from 'vitest'

import { rawSpan } from './testing.js'
import { reconcileRequest } from './copilot.js'
import { geminiAdapter } from './gemini.js'
import { TOKEN_REASONING_EXCEEDS_OUTPUT } from '../types.js'

// Gemini's convention is carried by documented assumptions pinned to the
// session parser's recorded reading of its counters (dash/src/providers/
// gemini.ts): input INCLUDES cached tokens; there is no cache-creation
// counter; thoughts are a subset of output. The tables prove the convention
// applied correctly and — as with Copilot and pi — that the inverted reading
// fails loudly (design.md, "Testing Strategy"; R4.2).

/** Gemini-shaped counters: input INCLUDES cached tokens (documented assumption 1). */
const geminiCounts = (
  overrides: Partial<Record<'input' | 'cached' | 'output' | 'thoughts', number>> = {},
) => ({
  'gen_ai.usage.input_tokens': overrides.input ?? 1_200,
  'gen_ai.usage.output_tokens': overrides.output ?? 150,
  'gen_ai.usage.cached_tokens': overrides.cached ?? 1_000,
  'gemini.usage.thoughts_tokens': overrides.thoughts ?? 90,
  'gemini.session.id': 'g-77',
})

describe('geminiAdapter.detect', () => {
  it.each([
    ['full fingerprint', geminiCounts(), 1],
    ['gemini namespace vendor evidence', { 'gemini.session.id': 'g-77' }, 0.6],
    ['gen_ai.system vendor evidence', { 'gen_ai.system': 'gemini' }, 0.6],
    [
      'an explicit Antigravity agent identity outranks the Gemini vendor label',
      { 'gen_ai.system': 'gemini', 'gen_ai.agent.name': 'antigravity' },
      0,
    ],
    ['shared GenAI usage alone', { 'gen_ai.usage.input_tokens': 1_200 }, 0.4],
    ['no fingerprint', { 'pi.session.id': 's-9f2' }, 0],
  ])('scores %s as %s', (_label, attributes, score) => {
    expect(geminiAdapter.detect(rawSpan({ spanId: 'a', attributes }))).toBe(score)
  })
})

describe('geminiAdapter.normalize — the documented convention (R4.2)', () => {
  it.each([
    [
      'input includes cached; fresh is the subtraction',
      geminiCounts(),
      {
        freshInput: 200,
        cacheRead: 1_000,
        cacheCreation: 0,
        reportedInput: 1_200,
        output: 150,
        reasoning: 90,
      },
    ],
    [
      'cached via the dotted cache_read spelling',
      {
        'gen_ai.usage.input_tokens': 1_200,
        'gen_ai.usage.output_tokens': 150,
        'gen_ai.usage.cache_read.input_tokens': 1_000,
        'gemini.session.id': 'g-77',
      },
      { freshInput: 200, cacheRead: 1_000, cacheCreation: 0, reportedInput: 1_200 },
    ],
    [
      'no cache at all',
      { 'gen_ai.usage.input_tokens': 300, 'gen_ai.usage.output_tokens': 40 },
      { freshInput: 300, cacheRead: 0, cacheCreation: 0, reportedInput: 300, output: 40 },
    ],
  ])('applies %s', (_label, attributes, expected) => {
    const record = geminiAdapter.normalize(
      rawSpan({ spanId: 's1', traceId: 't1', source: 'gemini-cli-2', attributes }),
    )

    expect(record.tokens).toMatchObject(expected)
    expect(record.tokens.freshInput + record.tokens.cacheRead + record.tokens.cacheCreation).toBe(
      record.tokens.reportedInput,
    )
    expect(record.harness).toBe('gemini')
    expect(record.op).toBe('llm.invoke')
    expect(geminiAdapter.validate(record)).toBeUndefined()
  })

  it('declares cache_creation unexported — explicit caching has no write counter', () => {
    const record = geminiAdapter.normalize(rawSpan({ spanId: 's1', attributes: geminiCounts() }))
    expect(geminiAdapter.unexportedMetrics()).toEqual(['cache_creation'])
    // No creation counter is invented from an absent attribute; the absence
    // is stated per metric (R7.6, R10.2).
    expect(record.measurability).toEqual({
      cache_creation: expect.objectContaining({ availability: 'not_measurable' }),
    })
    expect(record.tokens.cacheCreation).toBe(0)
  })
})

describe('geminiAdapter — exclusive-shaped counters convert exclusively (issue #193)', () => {
  it('recovers exclusive-shaped counters instead of negative fresh', () => {
    // Cache cannot be a subset of a smaller input. Convert exclusively:
    // fresh as claimed, reported total reassembled. Clamping to 0 would
    // drop the 200 claimed tokens.
    const record = geminiAdapter.normalize(
      rawSpan({
        spanId: 's1',
        traceId: 't1',
        attributes: { 'gen_ai.usage.input_tokens': 200, 'gen_ai.usage.cached_tokens': 1_000 },
      }),
    )

    expect(record.tokens).toMatchObject({
      freshInput: 200,
      cacheRead: 1_000,
      cacheCreation: 0,
      reportedInput: 1_200,
    })
    expect(geminiAdapter.validate(record)).toBeUndefined()
  })

  it('rejects thoughts reported outside output (assumption 3)', () => {
    const record = geminiAdapter.normalize(
      rawSpan({
        spanId: 's1',
        attributes: geminiCounts({ output: 100, thoughts: 150 }),
      }),
    )
    expect(geminiAdapter.validate(record)).toMatchObject({
      severity: 'error',
      code: TOKEN_REASONING_EXCEEDS_OUTPUT,
    })
  })
})

describe('geminiAdapter — per-request reconciliation (R4.5)', () => {
  it('matches the per-turn sums against the harness-reported root total', () => {
    const records = [
      geminiAdapter.normalize(
        rawSpan({
          spanId: 'root',
          traceId: 't1',
          parentSpanId: null,
          attributes: geminiCounts({ input: 1_500, output: 190, thoughts: 90 }),
        }),
      ),
      geminiAdapter.normalize(
        rawSpan({
          spanId: 'turn1',
          traceId: 't1',
          parentSpanId: 'root',
          attributes: geminiCounts(),
        }),
      ),
      geminiAdapter.normalize(
        rawSpan({
          spanId: 'turn2',
          traceId: 't1',
          parentSpanId: 'root',
          attributes: geminiCounts({ input: 300, cached: 0, output: 40, thoughts: 0 }),
        }),
      ),
    ]

    expect(reconcileRequest('root', records)).toEqual({
      match: true,
      expected: 1_500,
      actual: 1_500,
      outputExpected: 190,
      outputActual: 190,
    })
  })
})
