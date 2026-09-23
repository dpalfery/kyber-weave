import { describe, expect, it } from 'vitest'

import {
  CANONICAL_CONTENT_KEYS,
  TOKEN_NEGATIVE_CLASS,
  TOKEN_NEGATIVE_FRESH,
  TOKEN_REASONING_EXCEEDS_OUTPUT,
  TOKEN_SUM_MISMATCH,
  type CanonicalContent,
  type CanonicalRecord,
  type TokenUsage,
  validateTokens,
} from './types.js'

function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  return {
    freshInput: 100,
    cacheRead: 0,
    cacheCreation: 0,
    output: 50,
    reportedInput: 100,
    reportedOutput: 50,
    ...overrides,
  }
}

function record(tokens: TokenUsage, overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  return {
    spanId: 'span-1',
    traceId: null,
    parentSpanId: null,
    source: 'pi:agent-7f3',
    harness: 'pi',
    name: 'chat turn',
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-08-29T12:00:00.000Z',
    durationMs: 1250,
    status: 'ok',
    tokens,
    content: { system_prompt: 'You are a coding agent.' },
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

describe('validateTokens', () => {
  it.each([
    // All three input classes present and disjoint; the sum reconciles.
    {
      name: 'disjoint decomposition across all three input classes',
      tokens: usage({ freshInput: 120, cacheRead: 800, cacheCreation: 45, output: 50, reasoning: 12, reportedInput: 965 }),
    },
    // Copilot's inclusive input counter, correctly converted: its reported
    // input already contains the cache classes, so fresh input is the
    // remainder and the identity holds (R4.2).
    {
      name: "Copilot's convention applied correctly (reported input includes cache)",
      tokens: usage({ freshInput: 120, cacheRead: 800, cacheCreation: 45, reportedInput: 965 }),
    },
    // pi's exclusive input counter, correctly converted: its reported input
    // excludes cache, so the canonical reported input is the true total and
    // the identity still holds (R4.2).
    {
      name: "pi's convention applied correctly (reported input excludes cache)",
      tokens: usage({ freshInput: 100, cacheRead: 800, cacheCreation: 45, reportedInput: 945 }),
    },
    { name: 'zero cache', tokens: usage() },
    { name: 'all input served from cache', tokens: usage({ freshInput: 0, cacheRead: 1000, reportedInput: 1000 }) },
    { name: 'all input written to cache', tokens: usage({ freshInput: 0, cacheCreation: 900, reportedInput: 900 }) },
    // reasoning is a subset of output, never an addition; equality is the boundary.
    { name: 'reasoning equal to output at the subset boundary', tokens: usage({ output: 90, reasoning: 90, reportedOutput: 90 }) },
    { name: 'reasoning absent', tokens: usage() },
    { name: 'zero-token record', tokens: usage({ freshInput: 0, output: 0, reportedInput: 0, reportedOutput: 0 }) },
  ])('accepts a valid decomposition: $name', ({ tokens }) => {
    expect(validateTokens(tokens)).toEqual({ valid: true })
  })

  it.each([
    // pi's exclusive counter read with Copilot's convention computes fresh
    // input as the remainder of a total that never contained the cache
    // classes: negative on 293 of 307 measured spans. The sum here still
    // reconciles, proving negativity is caught on its own (R4.2, R4.4).
    {
      name: 'negative fresh input (pi counters read with Copilot convention)',
      tokens: usage({ freshInput: -293, cacheRead: 1040, reportedInput: 747 }),
      code: TOKEN_NEGATIVE_FRESH,
    },
    // The opposite inversion: Copilot's inclusive counter stored as fresh
    // input alongside its cache classes double-counts by up to 2x, which the
    // reported-input identity rejects (R4.2, R4.4).
    {
      name: 'inclusive input double-counted as fresh alongside cache classes',
      tokens: usage({ freshInput: 965, cacheRead: 800, cacheCreation: 45, reportedInput: 965 }),
      code: TOKEN_SUM_MISMATCH,
    },
    {
      name: 'sum off by a single token',
      tokens: usage({ freshInput: 100, cacheRead: 50, reportedInput: 151 }),
      code: TOKEN_SUM_MISMATCH,
    },
    {
      name: 'negative cache read',
      tokens: usage({ freshInput: 110, cacheRead: -10, reportedInput: 100 }),
      code: TOKEN_NEGATIVE_CLASS,
    },
    {
      name: 'negative cache creation',
      tokens: usage({ freshInput: 90, cacheCreation: -10, reportedInput: 80 }),
      code: TOKEN_NEGATIVE_CLASS,
    },
    {
      name: 'negative output',
      tokens: usage({ output: -5, reportedOutput: -5 }),
      code: TOKEN_NEGATIVE_CLASS,
    },
    {
      name: 'reasoning above output',
      tokens: usage({ output: 10, reasoning: 11, reportedOutput: 10 }),
      code: TOKEN_REASONING_EXCEEDS_OUTPUT,
    },
    {
      name: 'negative reasoning',
      tokens: usage({ reasoning: -1 }),
      code: TOKEN_REASONING_EXCEEDS_OUTPUT,
    },
  ])(
    'rejects an invalid decomposition: $name',
    ({ tokens, code }) => {
      const result = validateTokens(tokens, 'span-1')
      expect(result.valid).toBe(false)
      if (!result.valid) {
        expect(result.problem.code).toBe(code)
        expect(result.problem.severity).toBe('error')
        expect(result.problem.location).toBe('span-1')
        expect(result.problem.message.length).toBeGreaterThan(0)
      }
    }
  )

  it('rejection is observable, not a boolean detail: the problem names the mismatch', () => {
    const result = validateTokens(usage({ freshInput: 100, cacheRead: 50, reportedInput: 151 }))
    if (!result.valid) {
      expect(result.problem.code).toBe(TOKEN_SUM_MISMATCH)
      expect(result.problem.message).toContain('150')
      expect(result.problem.message).toContain('151')
    } else {
      throw new Error('expected the decomposition to be rejected')
    }
  })

  // R4.3: validation is a property of the record, not of its position in a
  // trace. An orphan with no resolvable parent is validated identically to a
  // rooted one — orphanage never exempts a record from the identity.
  it.each([
    { position: 'orphan with no resolvable parent', traceId: null, parentSpanId: null },
    { position: 'rooted span', traceId: 'trace-1', parentSpanId: 'span-0' },
  ])('validates a canonical record regardless of position: $position', ({ traceId, parentSpanId }) => {
    const orphan = record(usage(), { traceId, parentSpanId })
    expect(validateTokens(orphan.tokens, orphan.spanId)).toEqual({ valid: true })

    const broken = record(usage({ freshInput: -293, cacheRead: 1040, reportedInput: 747 }), { traceId, parentSpanId })
    const result = validateTokens(broken.tokens, broken.spanId)
    expect(result.valid).toBe(false)
    if (!result.valid) {
      expect(result.problem.code).toBe(TOKEN_NEGATIVE_FRESH)
      expect(result.problem.location).toBe(broken.spanId)
    }
  })
})

describe('CANONICAL_CONTENT_KEYS', () => {
  it('enumerates the analysis layer context buckets (R7.1)', () => {
    expect([...CANONICAL_CONTENT_KEYS]).toEqual([
      'system_prompt',
      'tool_definitions',
      'instruction_context',
      'conversation_history',
      'tool_result_content',
    ])
  })

  it('contains no duplicate buckets', () => {
    expect(new Set(CANONICAL_CONTENT_KEYS).size).toBe(CANONICAL_CONTENT_KEYS.length)
  })

  it('rejects a harness attribute name as a content key', () => {
    // Addresses content only through canonical keys: a harness attribute name
    // must not typecheck (design.md, "Canonical record").
    // @ts-expect-error - gen_ai.prompt is not a canonical key
    const content: CanonicalContent = { 'gen_ai.prompt': 'system prompt text' }
    expect(content).toBeTruthy()
  })
})
