import { describe, expect, it } from 'vitest'

import {
  COST_BASIS_MISMATCH,
  COST_CURRENCY_MISMATCH,
  createCostBlock,
  measuredInput,
  selectTier,
  sumCosts,
  type RateTable,
} from './cost.js'
import { type CostBasis, type CostBlock, type TokenUsage } from './types.js'

function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  return {
    freshInput: 100,
    cacheRead: 0,
    cacheCreation: 0,
    output: 0,
    reportedInput: 100,
    reportedOutput: 0,
    ...overrides,
  }
}

// Two context tiers bracketing 100k and 200k measured input, at per-million
// rates chosen so the derived values read plainly (150k at rate 2 → $0.30).
function tiered(overrides: Partial<RateTable> = {}): RateTable {
  return {
    name: 'published-2026-08',
    currency: 'USD',
    tiers: [
      { upTo: 100_000, inputRate: 1, outputRate: 2 },
      { upTo: 200_000, inputRate: 2, outputRate: 4 },
    ],
    ...overrides,
  }
}

function pricedBlock(basis: CostBasis, value: number, overrides: Partial<CostBlock> = {}): CostBlock {
  return { basis, status: 'priced', value, currency: 'USD', ...overrides }
}

// A block that carries a basis and a reason, but no figure (R5.4).
function unpricedBlock(basis: CostBasis, status: CostBlock['status']): CostBlock {
  return { basis, status }
}

describe('measuredInput', () => {
  it.each([
    { name: 'fresh input only', tokens: usage({ freshInput: 150_000, reportedInput: 150_000 }), expected: 150_000 },
    // Cache classes are input: they steer tier selection exactly as fresh
    // tokens do (R4.1, R5.6).
    {
      name: 'cache read and cache creation count toward the measured total',
      tokens: usage({ freshInput: 50_000, cacheRead: 80_000, cacheCreation: 20_000, reportedInput: 150_000 }),
      expected: 150_000,
    },
    { name: 'all input served from cache', tokens: usage({ freshInput: 0, cacheRead: 100_000, reportedInput: 100_000 }), expected: 100_000 },
    // Output is priced, but it must never pick the input tier.
    { name: 'output is not input', tokens: usage({ freshInput: 100_000, output: 40_000, reportedOutput: 40_000 }), expected: 100_000 },
  ])('measures input as the sum of the disjoint classes: $name', ({ tokens, expected }) => {
    expect(measuredInput(tokens)).toBe(expected)
  })
})

describe('selectTier', () => {
  it.each([
    // The motivating case: 150k measured input sits between the brackets and
    // must price at the 200k tier's rate (R5.6).
    { name: 'input between brackets selects the second tier', inputTokens: 150_000, expectedRate: 2 },
    { name: 'small input selects the first tier', inputTokens: 50_000, expectedRate: 1 },
    // The bracket bound is inclusive — exactly 100k stays in the first tier.
    { name: 'the bound is inclusive: exactly 100k stays in the first tier', inputTokens: 100_000, expectedRate: 1 },
    { name: 'one token past the bound moves up', inputTokens: 100_001, expectedRate: 2 },
    { name: 'zero input selects the first tier', inputTokens: 0, expectedRate: 1 },
  ])('selects the tier by measured input size: $name', ({ inputTokens, expectedRate }) => {
    expect(selectTier(tiered().tiers, inputTokens)?.inputRate).toBe(expectedRate)
  })

  it('selects the tightest covering bracket regardless of declaration order', () => {
    const shuffled = tiered({ tiers: [
      { upTo: 200_000, inputRate: 2, outputRate: 4 },
      { upTo: 100_000, inputRate: 1, outputRate: 2 },
    ] })
    expect(selectTier(shuffled.tiers, 90_000)?.upTo).toBe(100_000)
  })

  // Beyond every bracket the table offers no rate at that size — a missing
  // rate (R5.4), not a zero price.
  it('returns undefined when the input exceeds every tier', () => {
    expect(selectTier(tiered().tiers, 250_000)).toBeUndefined()
  })
})

describe('createCostBlock', () => {
  it.each([
    { name: 'USD figure', value: 1.57, currency: 'USD' },
    { name: 'non-USD figure carried in its own currency', value: 2.25, currency: 'EUR' },
  ])('carries a harness-reported figure verbatim: $name', ({ value, currency }) => {
    expect(createCostBlock({ harnessReported: { value, currency } })).toEqual({
      basis: 'harness',
      status: 'priced',
      value,
      currency,
    })
  })

  // R5.2: both a harness figure and everything needed to derive one are
  // supplied. The harness's own arithmetic wins outright — the block is the
  // harness figure, not 0.30, and it is not decorated with a derived
  // by-model breakdown it never came from.
  it('prefers the harness figure over a derived one when both are supplied (R5.2)', () => {
    const block = createCostBlock({
      harnessReported: { value: 1.57, currency: 'USD' },
      table: tiered(),
      tokens: usage({ freshInput: 150_000, reportedInput: 150_000 }),
      model: 'claude-sonnet-4-5',
    })
    expect(block).toEqual({ basis: 'harness', status: 'priced', value: 1.57, currency: 'USD' })
  })

  it.each([
    {
      name: '150k measured input prices at the second tier (150k × 2 / 1M)',
      tokens: usage({ freshInput: 150_000, reportedInput: 150_000 }),
      expected: 0.3,
    },
    // Same 150k measured input, assembled from cache classes: the tier is
    // chosen by the measured total, not by the fresh count alone.
    {
      name: 'cache classes steer tier selection',
      tokens: usage({ freshInput: 50_000, cacheRead: 80_000, cacheCreation: 20_000, reportedInput: 150_000 }),
      expected: 0.3,
    },
    {
      name: 'the 100k bound is inclusive (100k × 1 / 1M)',
      tokens: usage({ freshInput: 100_000, reportedInput: 100_000 }),
      expected: 0.1,
    },
    {
      name: 'one token past the bound prices at the second tier',
      tokens: usage({ freshInput: 100_001, reportedInput: 100_001 }),
      expected: 0.200002,
    },
    // (50k × 1 + 40k × 2) / 1M — output priced at the same tier's rate.
    {
      name: 'output prices at the selected tier output rate',
      tokens: usage({ freshInput: 50_000, output: 40_000, reportedInput: 50_000, reportedOutput: 40_000 }),
      expected: 0.13,
    },
  ])('derives a published figure from the tier the measured input selects: $name', ({ tokens, expected }) => {
    expect(createCostBlock({ table: tiered(), tokens, model: 'claude-sonnet-4-5' })).toEqual({
      basis: 'published',
      status: 'priced',
      value: expected,
      currency: 'USD',
      byModel: { 'claude-sonnet-4-5': expected },
    })
  })

  // A table's own currency is carried when it declares one.
  it('derives in the table currency when it is not USD', () => {
    const block = createCostBlock({
      table: tiered({ currency: 'EUR' }),
      tokens: usage({ freshInput: 50_000, reportedInput: 50_000 }),
    })
    expect(block.currency).toBe('EUR')
  })

  it.each([
    {
      name: 'no harness figure and no table',
      input: {},
      expected: { basis: 'unknown', status: 'no_rate' },
    },
    {
      // Tier selection reads measured input (R5.6); with no decomposition
      // there is nothing to read, and the system does not guess.
      name: 'table without a measured token decomposition',
      input: { table: tiered() },
      expected: { basis: 'unknown', status: 'no_rate' },
    },
    {
      name: 'measured input beyond the largest tier (R5.4)',
      input: { table: tiered(), tokens: usage({ freshInput: 250_000, reportedInput: 250_000 }) },
      expected: { basis: 'published', status: 'no_rate' },
    },
  ])('leaves the block unpriced rather than guessing: $name', ({ input, expected }) => {
    const block = createCostBlock(input)
    expect(block).toEqual(expected)
    // The R5.4 point, exactly: no rate is not zero, so no value is present to
    // render as $0.00.
    expect(block.value).toBeUndefined()
  })
})

describe('sumCosts', () => {
  it.each([
    {
      name: 'harness figures total under one basis',
      blocks: [pricedBlock('harness', 0.27), pricedBlock('harness', 1.3)],
      basis: 'harness',
      status: 'priced',
      value: 1.57,
    },
    {
      name: 'published figures total under one basis',
      blocks: [pricedBlock('published', 0.1), pricedBlock('published', 0.2)],
      basis: 'published',
      status: 'priced',
      value: 0.3,
    },
  ])('totals figures of one basis: $name', ({ blocks, basis, status, value }) => {
    const result = sumCosts(blocks)
    if (!result.ok) throw new Error(`expected a total, got a problem: ${result.problem.code}`)
    expect(result.total.basis).toBe(basis)
    expect(result.total.status).toBe(status)
    expect(result.total.currency).toBe('USD')
    expect(result.total.value).toBeCloseTo(value, 10)
  })

  // R5.1, the refusal that matters: figures of different bases are never
  // blended into one total. harness 1.57 + published 0.30 must not come back
  // as a plausible 1.87 — it must come back as a problem naming the bases.
  it.each([
    {
      name: 'a harness figure next to a published one',
      blocks: [pricedBlock('harness', 1.57), pricedBlock('published', 0.3)],
    },
    {
      name: 'order does not excuse the blend',
      blocks: [pricedBlock('published', 0.3), pricedBlock('harness', 1.57)],
    },
    {
      // An unpriced block still names a basis, and a partial total under one
      // basis is still a blended claim — the refusal holds without a value.
      name: 'an unpriced published block beside a priced harness one',
      blocks: [pricedBlock('harness', 1.57), unpricedBlock('published', 'no_rate')],
    },
    {
      name: 'three bases at once',
      blocks: [pricedBlock('harness', 1), pricedBlock('published', 2), unpricedBlock('unknown', 'no_rate')],
    },
  ])('refuses to total figures of different bases: $name', ({ blocks }) => {
    const result = sumCosts(blocks)
    if (result.ok) throw new Error('expected the sum to be refused, got a blended total')
    expect(result.problem.code).toBe(COST_BASIS_MISMATCH)
    expect(result.problem.severity).toBe('error')
    expect(result.problem.message).toContain('harness')
    expect(result.problem.message).toContain('published')
  })

  it('merges the by-model breakdown of the blocks it totals', () => {
    const result = sumCosts([
      pricedBlock('published', 0.1, { byModel: { 'claude-sonnet-4-5': 0.1 } }),
      pricedBlock('published', 0.2, { byModel: { 'gpt-5.2-mini': 0.2 } }),
    ])
    if (!result.ok) throw new Error('expected a total')
    expect(result.total.byModel).toEqual({ 'claude-sonnet-4-5': 0.1, 'gpt-5.2-mini': 0.2 })
  })

  it.each([
    {
      // The unpriced block contributes nothing — not zero — and the total
      // says so by being partial (R5.4).
      name: 'priced beside unpriced of the same basis is partial',
      blocks: [pricedBlock('harness', 0.27), unpricedBlock('harness', 'no_rate')],
      basis: 'harness',
      status: 'partial',
      value: 0.27,
    },
    {
      name: 'a block already partial keeps the total partial',
      blocks: [pricedBlock('harness', 0.27), pricedBlock('harness', 0.5, { status: 'partial' })],
      basis: 'harness',
      status: 'partial',
      value: 0.77,
    },
    {
      name: 'all unpriced, one shared reason',
      blocks: [unpricedBlock('unknown', 'no_rate'), unpricedBlock('unknown', 'no_rate')],
      basis: 'unknown',
      status: 'no_rate',
      value: undefined,
    },
    {
      name: 'all unpriced, mixed reasons render as an incomplete picture',
      blocks: [unpricedBlock('published', 'no_rate'), unpricedBlock('published', 'out_of_scope')],
      basis: 'published',
      status: 'partial',
      value: undefined,
    },
  ])('marks a total that does not cover every block: $name', ({ blocks, basis, status, value }) => {
    const result = sumCosts(blocks)
    if (!result.ok) throw new Error(`expected a total, got a problem: ${result.problem.code}`)
    expect(result.total.basis).toBe(basis)
    expect(result.total.status).toBe(status)
    if (value === undefined) {
      expect(result.total.value).toBeUndefined()
    } else {
      expect(result.total.value).toBeCloseTo(value, 10)
    }
  })

  // A corrupt figure is not a price: it counts as absent and the total says
  // partial instead of poisoning the sum with NaN.
  it('treats a non-finite value as absent rather than summing it', () => {
    const result = sumCosts([pricedBlock('harness', Number.NaN), pricedBlock('harness', 0.27)])
    if (!result.ok) throw new Error('expected a total')
    expect(result.total.status).toBe('partial')
    expect(result.total.value).toBeCloseTo(0.27, 10)
  })

  it('refuses figures of one basis in different currencies', () => {
    const result = sumCosts([pricedBlock('harness', 1.57), pricedBlock('harness', 0.3, { currency: 'EUR' })])
    if (result.ok) throw new Error('expected the sum to be refused, got a blended total')
    expect(result.problem.code).toBe(COST_CURRENCY_MISMATCH)
    expect(result.problem.severity).toBe('error')
  })

  // The design's anti-requirement: a total of nothing is unpriced, never a
  // plausible-looking $0.00.
  it('totals an empty collection as unpriced, not as zero', () => {
    const result = sumCosts([])
    if (!result.ok) throw new Error('expected a total')
    expect(result.total).toEqual({ basis: 'unknown', status: 'no_rate' })
    expect(result.total.value).toBeUndefined()
  })
})
