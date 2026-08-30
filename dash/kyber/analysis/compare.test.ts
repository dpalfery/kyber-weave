// Unit tests for the cross-harness comparison table (spec:
// docs/specs/kyberdash, R10). The three behaviors the requirements pin:
// availability declared independently of value and rendered as not
// measurable rather than zero (R10.1, R10.2), per-turn ratios leading
// totals (R10.3), and cost compared only through a declared basis (R10.4).

import { describe, expect, it } from 'vitest'

import { COST_BASIS_MISMATCH, COST_CURRENCY_MISMATCH } from '../canon/cost.js'
import type { CanonicalRecord, CostBasis, CostBlock, TokenUsage } from '../canon/types.js'

import { compareHarnesses, type ComparisonTable, type MetricRow } from './compare.js'

let spanCounter = 0

/** Token classes whose reported-input identity holds by construction (R4.1). */
function usage(overrides: Partial<TokenUsage> = {}): TokenUsage {
  const tokens = {
    freshInput: 900,
    cacheRead: 100,
    cacheCreation: 0,
    output: 200,
    ...overrides,
  }
  return {
    ...tokens,
    reportedInput:
      overrides.reportedInput ??
      tokens.freshInput + tokens.cacheRead + tokens.cacheCreation,
    reportedOutput: overrides.reportedOutput ?? tokens.output,
  }
}

/** One model-request turn for `harness`, overridable field by field. */
function turn(harness: string, overrides: Partial<CanonicalRecord> = {}): CanonicalRecord {
  spanCounter += 1
  return {
    spanId: `span-${spanCounter}`,
    traceId: 'trace-1',
    parentSpanId: null,
    source: `${harness}-source`,
    harness,
    name: `${harness}.chat`,
    op: 'llm.invoke',
    kind: 'internal',
    timestamp: '2026-01-01T00:00:00Z',
    durationMs: 12,
    status: 'ok',
    tokens: usage(),
    content: {},
    cost: { basis: 'unknown', status: 'no_rate' },
    ...overrides,
  }
}

function priced(value: number, basis: CostBasis = 'published', currency = 'USD'): CostBlock {
  return { basis, status: 'priced', value, currency }
}

function times<T>(count: number, make: () => T): T[] {
  return Array.from({ length: count }, () => make())
}

function row(table: ComparisonTable, metric: string): MetricRow {
  const found = table.rows.find((candidate) => candidate.metric === metric)
  if (found === undefined) throw new Error(`no row for metric ${metric}`)
  return found
}

describe('per-turn ratios lead (R10.3)', () => {
  it('places every per-turn row before every total row', () => {
    const table = compareHarnesses([times(10, () => turn('pi'))], ['pi'])

    const kinds = table.rows.map((candidate) => candidate.kind)
    const firstTotal = kinds.indexOf('total')
    expect(firstTotal).toBeGreaterThan(0)
    for (const [index, kind] of kinds.entries()) {
      expect(kind).toBe(index < firstTotal ? 'per_turn' : 'total')
    }
  })

  it('compares ratios when totals are identical, because totals measure how long each harness ran', () => {
    // Ten turns of 1,200 tokens against one turn of 12,000: the totals
    // agree exactly while the per-turn figures differ by an order of
    // magnitude — the whole reason R10.3 leads with ratios.
    const long = times(10, () => turn('pi'))
    const short = [turn('copilot', { tokens: usage({ freshInput: 9900, output: 2000 }) })]

    const table = compareHarnesses([long, short], ['pi', 'copilot'])

    const totals = row(table, 'total_tokens').cells
    expect(totals.pi.value).toBe(12_000)
    expect(totals.copilot.value).toBe(12_000)

    const perTurn = row(table, 'tokens_per_turn').cells
    expect(perTurn.pi.value).toBe(1_200)
    expect(perTurn.pi.render).toBe('1,200')
    expect(perTurn.copilot.value).toBe(12_000)
    expect(perTurn.copilot.render).toBe('12,000')
  })
})

describe('availability independent of value (R10.1, R10.2)', () => {
  it('renders a metric the harness cannot report as not measurable, never zero', () => {
    // pi exports no tool definitions — measured at 14 tools invoked across
    // 368 calls with none exported — so schema cost is a stated limitation,
    // not a perfect score of zero.
    const pi = times(3, () =>
      turn('pi', { measurability: { tool_definitions: 'not_measurable' } })
    )
    const copilot = times(3, () =>
      turn('copilot', { content: { tool_definitions: 'x'.repeat(400) } })
    )

    const table = compareHarnesses([pi, copilot], ['pi', 'copilot'])
    const cells = row(table, 'schema_cost_per_turn').cells

    expect(cells.pi.measurable).toBe(false)
    expect(cells.pi.value).toBeUndefined()
    expect(cells.pi.value).not.toBe(0)
    expect(cells.pi.render).toBe('not measurable')
    expect(cells.pi.render).not.toMatch(/0/)

    // Same row, same table: the harness that does export definitions is
    // measured — availability is a per-harness declaration, not a row
    // property.
    expect(cells.copilot.measurable).toBe(true)
    expect(cells.copilot.value).toBe(100)
    expect(cells.copilot.render).toContain('derived')
  })

  it('poisons an aggregate when any record declares the metric not measurable', () => {
    const corpus = [
      turn('pi'),
      turn('pi', { measurability: { cache_read: 'not_measurable' } }),
      turn('pi'),
    ]

    const table = compareHarnesses([corpus, times(3, () => turn('copilot'))], ['pi', 'copilot'])
    const cells = row(table, 'cache_read_share_per_turn').cells

    // An average over partially measured data is a guess wearing one, so
    // one declaration withholds the figure for the whole corpus.
    expect(cells.pi.measurable).toBe(false)
    expect(cells.pi.value).toBeUndefined()
    expect(cells.pi.render).toBe('not measurable')

    expect(cells.copilot.measurable).toBe(true)
    expect(cells.copilot.value).toBe(0.1)
    expect(cells.copilot.render).toBe('10%')
  })

  it('keeps an unpriced metric measurable while withholding its figure', () => {
    // No published rate is a reason, not a capability gap: cost is
    // measurable for the harness, merely unpriced for the model. The
    // distinction is exactly R10.1's "separately from its value".
    const table = compareHarnesses([[turn('pi')]], ['pi'])

    const costCell = row(table, 'cost_per_turn').cells.pi
    expect(costCell.measurable).toBe(true)
    expect(costCell.value).toBeUndefined()
    expect(costCell.render).toBe('no published rate')

    // The neighboring count row carries its value: availability is about
    // the metric, not about the corpus size.
    expect(row(table, 'turns').cells.pi.value).toBe(1)
  })

  it('renders stated reasons rather than zero for corpora without turns or records', () => {
    const toolOnly = [turn('pi', { op: 'tool.invoke', name: 'read_file' })]

    const table = compareHarnesses([toolOnly, []], ['pi', 'gemini'])

    const tokens = row(table, 'tokens_per_turn').cells
    expect(tokens.pi.measurable).toBe(true)
    expect(tokens.pi.value).toBeUndefined()
    expect(tokens.pi.render).toBe('no turns')
    expect(tokens.gemini.render).toBe('no records')

    const cost = row(table, 'cost_per_turn').cells.gemini
    expect(cost.measurable).toBe(true)
    expect(cost.value).toBeUndefined()
    expect(cost.render).toBe('no records')
  })

  it('reports a measurable harness with no definitions as a stated reason, not zero', () => {
    const table = compareHarnesses([[turn('copilot')]], ['copilot'])

    const cell = row(table, 'schema_cost_per_turn').cells.copilot
    expect(cell.measurable).toBe(true)
    expect(cell.value).toBeUndefined()
    expect(cell.render).toBe('no tool definitions reported')
  })
})

describe('cost compared only through a declared basis (R10.4)', () => {
  const harnessReported = () => times(2, () => turn('pi', { cost: priced(10, 'harness') }))
  const publishedTable = () => times(2, () => turn('copilot', { cost: priced(2, 'published') }))

  it('refuses to compare across differing bases and records the refusal', () => {
    const table = compareHarnesses([harnessReported(), publishedTable()], ['pi', 'copilot'])

    for (const metric of ['cost_per_turn', 'total_cost']) {
      const cells = row(table, metric).cells
      for (const harness of ['pi', 'copilot']) {
        expect(cells[harness].measurable).toBe(true)
        expect(cells[harness].value).toBeUndefined()
        expect(cells[harness].render).toContain('not comparable')
        expect(cells[harness].render).toContain('harness')
        expect(cells[harness].render).toContain('published')
      }
    }

    const problem = table.problems.find((candidate) => candidate.code === COST_BASIS_MISMATCH)
    expect(problem?.message).toContain('more than one basis')
  })

  it('compares through the declared basis alone, excluding figures on any other', () => {
    const table = compareHarnesses([harnessReported(), publishedTable()], ['pi', 'copilot'], {
      costBasis: 'published',
    })

    const perTurn = row(table, 'cost_per_turn').cells
    expect(perTurn.copilot.value).toBe(2)
    expect(perTurn.copilot.basis).toBe('published')
    expect(perTurn.copilot.render).toBe('$2.00')

    // pi's figures exist but sit on the harness basis; through "published"
    // they are out of the comparison, and the cell says so instead of
    // contributing a blended number.
    expect(perTurn.pi.value).toBeUndefined()
    expect(perTurn.pi.render).toBe('no figures on declared basis "published"')

    expect(row(table, 'total_cost').cells.copilot.value).toBe(4)
    expect(table.problems).toHaveLength(0)
  })

  it('compares when every priced corpus sits on one basis', () => {
    const table = compareHarnesses(
      [times(2, () => turn('pi', { cost: priced(10) })), [turn('copilot', { cost: priced(6) })]],
      ['pi', 'copilot']
    )

    const cells = row(table, 'cost_per_turn').cells
    expect(cells.pi.value).toBe(10)
    expect(cells.pi.render).toBe('$10.00')
    expect(cells.pi.basis).toBe('published')
    expect(cells.pi.currency).toBe('USD')
    expect(cells.copilot.value).toBe(6)

    expect(table.problems).toHaveLength(0)
  })

  it('marks cost not comparable when currencies differ under one basis', () => {
    const table = compareHarnesses(
      [
        times(2, () => turn('pi', { cost: priced(10, 'published', 'USD') })),
        times(2, () => turn('copilot', { cost: priced(9, 'published', 'EUR') })),
      ],
      ['pi', 'copilot']
    )

    const cells = row(table, 'cost_per_turn').cells
    expect(cells.pi.value).toBeUndefined()
    expect(cells.pi.render).toContain('not comparable')
    expect(cells.pi.render).toContain('EUR')
    expect(cells.pi.render).toContain('USD')

    const problem = table.problems.find((candidate) => candidate.code === COST_CURRENCY_MISMATCH)
    expect(problem?.message).toContain('different currencies')
  })

  it('renders a partially priced total as the priced portion only', () => {
    const corpus = [
      turn('pi', { cost: priced(5) }),
      turn('pi', { cost: { basis: 'published', status: 'no_rate' } }),
    ]

    const table = compareHarnesses([corpus], ['pi'])

    const total = row(table, 'total_cost').cells.pi
    expect(total.value).toBe(5)
    expect(total.render).toBe('$5.00 (partial)')
  })
})

describe('inputs', () => {
  it('refuses parallel arrays that do not align', () => {
    expect(() => compareHarnesses([[]], ['pi', 'copilot'])).toThrow(TypeError)
  })
})
