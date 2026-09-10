// Rate-table scoping and the unpriced cases (R5.3-R5.5). A published table
// prices only the harnesses its applicability list names, so two harnesses
// can run one model under different billing without one table bleeding into
// the other's turns. A model with no published rate renders in words, never
// as a plausible $0.00, and "not billed" stays a distinct answer from "no
// rate". The regression at the bottom reproduces the incident behind R5.3:
// 143 pi turns priced at GitHub's credit rate — $0.27 against the $1.57
// actually charged, wrong by 5.8x, understated, and plausible-looking.

import { describe, expect, it } from 'vitest'

import {
  createCostBlock,
  isHarnessInScope,
  priceWithTable,
  renderCost,
  sumCosts,
  type RateTable,
} from './cost.js'
import { type CostBlock, type TokenUsage } from './types.js'

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

// GitHub's credit-style table: one context tier, scoped to the one harness
// it may price (R5.3).
function githubCredits(overrides: Partial<RateTable> = {}): RateTable {
  return {
    name: 'github-copilot-credits',
    currency: 'USD',
    applicability: ['github-copilot'],
    tiers: [{ upTo: 200_000, inputRate: 2.7, outputRate: 10.8 }],
    ...overrides,
  }
}

// The value the table's arithmetic produces for one turn, written the way
// the implementation computes it so the doubles agree bit for bit.
const turnValue = (tokens: TokenUsage, table: RateTable): number =>
  (tokens.freshInput * table.tiers[0].inputRate + tokens.output * table.tiers[0].outputRate) /
  1_000_000

describe('isHarnessInScope', () => {
  it.each([
    { name: 'a table with no applicability list names no restriction', applicability: undefined, harness: 'pi', expected: true },
    { name: '…even for an unnamed harness', applicability: undefined, harness: undefined, expected: true },
    { name: 'a table prices a harness its applicability list names', applicability: ['github-copilot'], harness: 'github-copilot', expected: true },
    { name: 'a table does not price a harness its list omits', applicability: ['github-copilot'], harness: 'pi', expected: false },
    { name: 'an unknown harness is not in the list', applicability: ['github-copilot'], harness: undefined, expected: false },
  ])('$name: $name', ({ applicability, harness, expected }) => {
    expect(isHarnessInScope(githubCredits({ applicability }), harness)).toBe(expected)
  })
})

describe('priceWithTable', () => {
  const turn = usage({ freshInput: 700, reportedInput: 700 })

  it.each([
    {
      name: 'an in-scope harness prices from the tier its measured input selects',
      harness: 'github-copilot',
      model: 'gpt-4o',
      table: githubCredits(),
      tokens: turn,
      expected: {
        basis: 'published',
        status: 'priced',
        value: turnValue(turn, githubCredits()),
        currency: 'USD',
        byModel: { 'gpt-4o': turnValue(turn, githubCredits()) },
      },
    },
    {
      // R5.3, the whole point: the model matches what the table prices, the
      // tokens are identical, and still nothing is priced — scope is judged
      // on the harness, before any rate is read.
      name: 'a harness the applicability list omits is not priced at all, even though the model matches (R5.3)',
      harness: 'pi',
      model: 'gpt-4o',
      table: githubCredits(),
      tokens: turn,
      expected: { basis: 'published', status: 'out_of_scope' },
    },
    {
      // Scope outranks the rate lookup: even a table that itemizes models
      // says "out of scope" first, not "no rate".
      name: 'scope is judged before the model lookup',
      harness: 'pi',
      model: 'gpt-4o',
      table: githubCredits({ publishedRates: new Map() }),
      tokens: turn,
      expected: { basis: 'published', status: 'out_of_scope' },
    },
    {
      // R5.4: the table itemizes models and this one is absent — a missing
      // rate, not a zero price.
      name: 'a model with no entry in the table rates is no_rate (R5.4)',
      harness: 'github-copilot',
      model: 'o3',
      table: githubCredits({ publishedRates: new Map([['gpt-4o', { inputRate: 5, outputRate: 15 }]]) }),
      tokens: turn,
      expected: { basis: 'published', status: 'no_rate' },
    },
    {
      // R5.5: the publisher explicitly lists the model as not billed — a
      // distinct answer from an absent entry.
      name: 'a model explicitly marked not billed is not_billed, distinct from no_rate (R5.5)',
      harness: 'github-copilot',
      model: 'gpt-4o',
      table: githubCredits({ publishedRates: new Map([['gpt-4o', { billed: false }]]) }),
      tokens: turn,
      expected: { basis: 'published', status: 'not_billed' },
    },
    {
      name: 'a per-model flat entry prices at its own rates',
      harness: 'github-copilot',
      model: 'gpt-4o',
      table: githubCredits({ publishedRates: new Map([['gpt-4o', { inputRate: 5, outputRate: 15 }]]) }),
      tokens: usage({ freshInput: 700, output: 100, reportedInput: 700, reportedOutput: 100 }),
      expected: {
        basis: 'published',
        status: 'priced',
        value: 0.005,
        currency: 'USD',
        byModel: { 'gpt-4o': 0.005 },
      },
    },
    {
      name: 'a model-specific table prices only its model',
      harness: 'github-copilot',
      model: 'o3',
      table: githubCredits({ model: 'gpt-4o' }),
      tokens: turn,
      expected: { basis: 'published', status: 'no_rate' },
    },
    {
      name: 'a model-specific table prices its named model from the tiers',
      harness: 'github-copilot',
      model: 'gpt-4o',
      table: githubCredits({ model: 'gpt-4o' }),
      tokens: turn,
      expected: {
        basis: 'published',
        status: 'priced',
        value: turnValue(turn, githubCredits()),
        currency: 'USD',
        byModel: { 'gpt-4o': turnValue(turn, githubCredits()) },
      },
    },
    {
      // Beyond every bracket the table offers no rate at that size — still a
      // missing rate (R5.4), never a zero price, even in scope.
      name: 'measured input beyond every tier is no_rate',
      harness: 'github-copilot',
      model: 'gpt-4o',
      table: githubCredits(),
      tokens: usage({ freshInput: 250_000, reportedInput: 250_000 }),
      expected: { basis: 'published', status: 'no_rate' },
    },
  ])('$name', ({ harness, model, table, tokens, expected }) => {
    const block = priceWithTable(tokens, model, harness, table)
    expect(block).toEqual(expected)
    // Whatever the unpriced reason, no figure exists to render as $0.00.
    if (expected.value === undefined) {
      expect(block.value).toBeUndefined()
    }
  })
})

describe('renderCost', () => {
  it.each([
    { name: 'a missing rate renders in words (R5.4)', block: { basis: 'published', status: 'no_rate' } as CostBlock, expected: 'no published rate' },
    { name: 'an explicitly-not-billed model renders distinctly (R5.5)', block: { basis: 'published', status: 'not_billed' } as CostBlock, expected: 'not billed' },
    { name: 'a harness outside the table renders distinctly (R5.3)', block: { basis: 'published', status: 'out_of_scope' } as CostBlock, expected: 'out of scope' },
    { name: 'a genuine zero renders as money — priced, not missing', block: { basis: 'published', status: 'priced', value: 0, currency: 'USD' } as CostBlock, expected: '$0.00' },
    { name: 'a priced figure renders as a currency amount', block: { basis: 'harness', status: 'priced', value: 1.57, currency: 'USD' } as CostBlock, expected: '$1.57' },
    { name: 'a non-USD figure renders in its own currency', block: { basis: 'harness', status: 'priced', value: 2.25, currency: 'EUR' } as CostBlock, expected: '€2.25' },
  ])('$name', ({ block, expected }) => {
    expect(renderCost(block)).toBe(expected)
  })

  // R5.4, stated from the other side: no unpriced status may ever come back
  // looking like a figure.
  it.each(['no_rate', 'not_billed', 'out_of_scope'] as const)('never renders %s as a figure', (status) => {
    expect(renderCost({ basis: 'published', status })).not.toContain('$')
  })
})

describe('createCostBlock', () => {
  const turn = usage({ freshInput: 700, reportedInput: 700 })

  // R5.2 outranks R5.3: the harness's own figure is not a table price, so
  // the table's scope is never even consulted when one is present.
  it('carries a harness-reported figure verbatim even when the table is out of scope', () => {
    expect(
      createCostBlock({
        harnessReported: { value: 1.57, currency: 'USD' },
        table: githubCredits(),
        tokens: turn,
        model: 'gpt-4o',
        harness: 'pi',
      })
    ).toEqual({ basis: 'harness', status: 'priced', value: 1.57, currency: 'USD' })
  })

  it('leaves a turn unpriced when the only table is out of scope — no fallback figure', () => {
    const block = createCostBlock({
      table: githubCredits(),
      tokens: turn,
      model: 'gpt-4o',
      harness: 'pi',
    })
    expect(block).toEqual({ basis: 'published', status: 'out_of_scope' })
    expect(block.value).toBeUndefined()
  })

  it('renders the absent case in words rather than as $0.00 (R5.4)', () => {
    const block = createCostBlock({
      table: githubCredits({ publishedRates: new Map() }),
      tokens: usage(),
      model: 'o3',
      harness: 'github-copilot',
    })
    expect(block).toEqual({ basis: 'published', status: 'no_rate' })
    expect(renderCost(block)).toBe('no published rate')
  })
})

// The incident behind R5.3: one session ran pi and github-copilot turns over
// the same model, gpt-4o, and the only published table priced GitHub's
// credit rates. Unguarded, it priced the 143 pi turns at that credit rate —
// same model, same tokens, plausible-looking — showing $0.27 against the
// $1.57 actually charged: wrong by 5.8x, entirely in the understating
// direction.
describe('regression (R5.3): two harnesses, one model, one table', () => {
  const perTurn = usage({ freshInput: 700, reportedInput: 700 })
  const model = 'gpt-4o'
  const githubTable = githubCredits()
  // pi's own billing: a different table, scoped to pi, at pi's real rates.
  const piTable: RateTable = {
    name: 'pi-published',
    currency: 'USD',
    applicability: ['pi'],
    tiers: [{ upTo: 200_000, inputRate: 15.7, outputRate: 62.8 }],
  }

  const piTurns = Array.from({ length: 143 }, () =>
    createCostBlock({ table: githubTable, tokens: perTurn, model, harness: 'pi' })
  )
  const copilotTurn = createCostBlock({
    table: githubTable,
    tokens: perTurn,
    model,
    harness: 'github-copilot',
  })

  it('does not price the out-of-scope harness even though the model matches', () => {
    for (const block of piTurns) {
      expect(block).toEqual({ basis: 'published', status: 'out_of_scope' })
    }
  })

  it('prices the in-scope harness from the same table and the same tokens', () => {
    expect(copilotTurn).toEqual({
      basis: 'published',
      status: 'priced',
      value: turnValue(perTurn, githubTable),
      currency: 'USD',
      byModel: { 'gpt-4o': turnValue(perTurn, githubTable) },
    })
  })

  it('totals the pi turns as out of scope with no figure, not as a silent $0', () => {
    const result = sumCosts(piTurns)
    if (!result.ok) throw new Error(`expected a total, got a problem: ${result.problem.code}`)
    expect(result.total).toEqual({ basis: 'published', status: 'out_of_scope' })
  })

  it('keeps the session total free of the fabricated figure', () => {
    const result = sumCosts([...piTurns, copilotTurn])
    if (!result.ok) throw new Error(`expected a total, got a problem: ${result.problem.code}`)
    // The unpriced pi turns keep the total partial — visible, not zeroed.
    expect(result.total.status).toBe('partial')
    expect(result.total.value).toBeCloseTo(turnValue(perTurn, githubTable), 10)
  })

  // What the guard prevents, priced out loud: the same 143 turns through the
  // same table with the applicability list removed. The figure is computable,
  // plausible, and wrong — $0.27 of GitHub credits for pi turns.
  it('shows what the unguarded table would have fabricated: ≈$0.27', () => {
    const unguarded: RateTable = { ...githubTable, applicability: undefined }
    const fabricated = sumCosts(
      Array.from({ length: 143 }, () =>
        createCostBlock({ table: unguarded, tokens: perTurn, model, harness: 'pi' })
      )
    )
    if (!fabricated.ok) throw new Error(`expected a total, got a problem: ${fabricated.problem.code}`)
    expect(fabricated.total.status).toBe('priced')
    expect(fabricated.total.value).toBeCloseTo(0.27, 2)
  })

  // The charge actually owed: pi's own scoped table prices the same turns at
  // pi's rates — $1.57, not $0.27, so the 5.8x understatement never happens.
  it('prices the same turns from pi own scoped table at the actually-charged ≈$1.57', () => {
    const actual = sumCosts(
      Array.from({ length: 143 }, () =>
        createCostBlock({ table: piTable, tokens: perTurn, model, harness: 'pi' })
      )
    )
    if (!actual.ok) throw new Error(`expected a total, got a problem: ${actual.problem.code}`)
    expect(actual.total.status).toBe('priced')
    expect(actual.total.value).toBeCloseTo(1.57, 2)
  })
})
