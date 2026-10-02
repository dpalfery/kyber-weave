import { describe, expect, it } from 'vitest'

import { rawSpan } from './testing.js'
import { antigravityAdapter } from './antigravity.js'
import antigravitySpan from './__fixtures__/antigravity-span.json' with { type: 'json' }

// Antigravity speaks Gemini's telemetry vocabulary (`gen_ai.system:
// 'gemini' on the wire) but is a distinct surveyed harness with its own
// agent identity (`gen_ai.agent.name: 'antigravity'`). The tables prove the
// adapter claims the agent identity — never the vendor label alone — so live
// `agy` spans stop landing on the excluded `gemini` identity (issue #195).

const agentAttributes = (overrides: Record<string, unknown> = {}) => ({
  'gen_ai.agent.name': 'antigravity',
  ...overrides,
})

describe('antigravityAdapter.detect', () => {
  it.each([
    ['full fingerprint', agentAttributes({ 'gen_ai.usage.input_tokens': 1_200 }), 1],
    ['agent identity alone', agentAttributes(), 0.6],
    ['agent identity with the Gemini vendor label', agentAttributes({ 'gen_ai.system': 'gemini' }), 0.6],
    ['shared GenAI usage alone', { 'gen_ai.usage.input_tokens': 1_200 }, 0.4],
    ['Gemini vendor evidence without the agent identity', { 'gemini.session.id': 'g-77' }, 0],
    ['no fingerprint', { 'pi.session.id': 's-9f2' }, 0],
  ])('scores %s as %s', (_label, attributes, score) => {
    expect(antigravityAdapter.detect(rawSpan({ spanId: 'a', attributes }))).toBe(score)
  })
})

describe('antigravityAdapter.normalize — the Gemini-vocabulary convention (R4.2)', () => {
  it('converts the live corpus shape with inclusive subtraction', () => {
    const attributes = antigravitySpan as Record<string, unknown>
    const record = antigravityAdapter.normalize(
      rawSpan({ spanId: 's1', traceId: 't1', source: 'agy', attributes }),
    )

    // Fresh is recovered by SUBTRACTING the cache classes; the reported
    // total is the harness's own figure, unchanged.
    expect(record.tokens).toMatchObject({
      freshInput: 251_976 - 243_910,
      cacheRead: 243_910,
      cacheCreation: 0,
      reportedInput: 251_976,
      output: 208_802,
    })
    expect(record.tokens.freshInput + record.tokens.cacheRead + record.tokens.cacheCreation).toBe(
      record.tokens.reportedInput,
    )
    expect(record.harness).toBe('antigravity')
    expect(record.op).toBe('llm.invoke')
    expect(antigravityAdapter.validate(record)).toBeUndefined()
  })

  it('declares cache_creation unexported — no write counter, same as the file side', () => {
    const record = antigravityAdapter.normalize(
      rawSpan({ spanId: 's1', attributes: agentAttributes({ 'gen_ai.usage.input_tokens': 300 }) }),
    )
    expect(antigravityAdapter.unexportedMetrics()).toEqual(['cache_creation'])
    expect(record.measurability).toEqual({
      cache_creation: expect.objectContaining({ availability: 'not_measurable' }),
    })
    expect(record.tokens.cacheCreation).toBe(0)
  })

  it('recovers exclusive-shaped counters instead of negative fresh (issue #193)', () => {
    const record = antigravityAdapter.normalize(
      rawSpan({
        spanId: 's1',
        traceId: 't1',
        attributes: agentAttributes({
          'gen_ai.usage.input_tokens': 200,
          'gen_ai.usage.cached_tokens': 1_000,
        }),
      }),
    )

    expect(record.tokens).toMatchObject({
      freshInput: 200,
      cacheRead: 1_000,
      cacheCreation: 0,
      reportedInput: 1_200,
    })
    expect(antigravityAdapter.validate(record)).toBeUndefined()
  })
})
