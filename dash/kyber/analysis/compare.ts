// Cross-harness comparison table, ported from the Python pipeline's
// compare.py (spec: docs/specs/kyberdash, design.md "Analysis layer", R10).
//
// Three rules govern the table, and each encodes a failure the Python
// pipeline already measured rather than a style choice:
//
//   * Availability is declared per metric and per harness, independently of
//     the value (R10.1). A harness that exports no tool definitions is not
//     "zero schema cost" — it is schema cost not measurable, and the cell
//     says so in words (R10.2). Rendering an unreported metric as 0 makes
//     the harness that reports least look most efficient.
//   * Per-turn ratios lead and totals trail (R10.3). Totals measure how long
//     each harness was left running; corpora differ in size, and the
//     per-turn ratio is the only row that compares like with like.
//   * Cost is compared only through a declared basis (R10.4, carried down
//     from R5.1): harness-reported arithmetic and published-table prices are
//     different figures about the same turns, and blending them into one
//     comparison manufactures a number nobody published. When the corpora
//     sit on different bases the cost cells render not comparable and the
//     refusal is recorded as a problem; a caller that declares one basis
//     gets a comparison through exactly that basis and nothing else.
//
// The table's context rows are the per-turn faces of R7's composition work —
// input size, fresh input, and cache-read share. Window-relative context
// pressure needs the window capacity the canonical record does not carry;
// it lands with the context analysis (task 7.1) and joins this table then.

import {
  COST_BASIS_MISMATCH,
  COST_CURRENCY_MISMATCH,
  sumCosts,
} from '../canon/cost.js'
import { approximateO200kBase } from '../canon/tokens.js'
import type {
  CanonicalRecord,
  CostBasis,
  CostBlock,
  MetricAvailability,
  Problem,
} from '../canon/types.js'

/** The exact phrase R10.2 pins for a metric a harness cannot report. */
export const NOT_MEASURABLE = 'not measurable'

const NO_RECORDS = 'no records'
const NO_TURNS = 'no turns'
const NO_INPUT = 'no input'
const NO_TOOL_DEFINITIONS = 'no tool definitions reported'
const MIXED_BASES = 'mixed cost bases'
const MIXED_CURRENCIES = 'mixed currencies'

/** The op that makes a record a turn: one model request. */
const TURN_OP = 'llm.invoke'

export type MetricKind = 'per_turn' | 'total'

export type MetricUnit = 'tokens' | 'share' | 'currency' | 'count'

/**
 * One cell of the table: a metric for one harness. `measurable` is the
 * R10.1 declaration and is never inferred from `value` — a measurable
 * metric may still carry no figure (an unpriced cost, an empty corpus),
 * and an unmeasurable one never carries one, because absent is not zero
 * (R10.2). `availability` carries the canon vocabulary (`measured`,
 * `derived`, `not_measurable`) behind the boolean; `basis` and `currency`
 * travel with cost figures so no consumer can present one naked (R10.4).
 */
export type MetricCell = {
  /** Whether the metric is measurable for this harness (R10.1). */
  measurable: boolean
  /** Why it is or is not measurable — the canon `MetricAvailability`. */
  availability: MetricAvailability
  /** The figure; present only when a real measurement produced one. */
  value?: number
  /** Basis a cost figure was derived on; cost rows only (R10.4). */
  basis?: CostBasis
  /** ISO currency of a cost figure; cost rows only. */
  currency?: string
  /** What a surface renders for this cell — never a disguised zero. */
  render: string
}

/** One metric row: per-turn ratios first, totals after (R10.3). */
export type MetricRow = {
  metric: string
  kind: MetricKind
  /** Human label for surfaces; the machine key is `metric`. */
  label: string
  unit: MetricUnit
  /** One cell per harness in the comparison. */
  cells: Record<string, MetricCell>
}

export type ComparisonTable = {
  /** Harness names in column order, as passed to `compareHarnesses`. */
  harnesses: string[]
  /** Per-turn rows lead; total rows trail (R10.3). */
  rows: MetricRow[]
  /** Surfaced refusals — cost-basis mismatches today (R10.4). */
  problems: Problem[]
}

export type ComparisonOptions = {
  /**
   * The one basis cost figures may be compared through (R10.4). Figures on
   * any other basis are excluded from the comparison; a harness priced only
   * elsewhere renders that fact in words rather than a figure. Without a
   * declared basis, cost is compared only when every priced corpus happens
   * to sit on the same one — a mismatch is refused, never blended.
   */
  costBasis?: CostBasis
}

// ---------------------------------------------------------------------------
// Per-harness aggregation
// ---------------------------------------------------------------------------

type TokenSums = {
  /** Measured input: fresh + cache-read + cache-creation (R4.1). */
  input: number
  freshInput: number
  cacheRead: number
  output: number
  /** Input plus output — the "tokens per turn" quantity. */
  all: number
}

type CostSummary = {
  /** The summed block; carries basis and status even without a figure. */
  total: CostBlock
  /** Whether the sum carries a finite figure. */
  hasValue: boolean
  /** Set when `sumCosts` refused to blend; the cell renders the reason. */
  refused?: Problem
}

type HarnessFigures = {
  harness: string
  recordCount: number
  turns: number
  tokens: TokenSums
  /** Derived token estimate over tool-definition content (R4.6). */
  schemaTokens: number
  schemaContentPresent: boolean
  /** Cost over every block, whatever its basis. */
  cost: CostSummary
  /** Cost over blocks on the declared basis only (equals `cost` when none). */
  costOnDeclaredBasis: CostSummary
  /** Metric keys any record declared not measurable — poison for aggregates. */
  unmeasurable: Set<string>
}

function summarizeCost(blocks: CostBlock[]): CostSummary {
  const summed = sumCosts(blocks)
  if (!summed.ok) {
    return {
      total: { basis: 'unknown', status: 'no_rate' },
      hasValue: false,
      refused: summed.problem,
    }
  }
  return {
    total: summed.total,
    hasValue:
      typeof summed.total.value === 'number' && Number.isFinite(summed.total.value),
  }
}

function aggregate(
  harness: string,
  records: CanonicalRecord[],
  costBasis: CostBasis | undefined
): HarnessFigures {
  const turnRecords = records.filter((record) => record.op === TURN_OP)

  let freshInput = 0
  let cacheRead = 0
  let cacheCreation = 0
  let output = 0
  let schemaTokens = 0
  let schemaContentPresent = false
  for (const record of turnRecords) {
    freshInput += record.tokens.freshInput
    cacheRead += record.tokens.cacheRead
    cacheCreation += record.tokens.cacheCreation
    output += record.tokens.output
    const definitions = record.content.tool_definitions
    if (typeof definitions === 'string' && definitions.length > 0) {
      schemaContentPresent = true
      schemaTokens += approximateO200kBase(definitions)
    }
  }
  const input = freshInput + cacheRead + cacheCreation

  // One record declaring a metric not measurable poisons the aggregate: an
  // average over partially measured data is not a measurement, it is a
  // guess wearing one. All classes ride along; the rows consult their own.
  const unmeasurable = new Set<string>()
  for (const record of records) {
    for (const [metric, availability] of Object.entries(record.measurability ?? {})) {
      if (availability === 'not_measurable') unmeasurable.add(metric)
    }
  }

  const blocks: CostBlock[] = []
  const blocksOnDeclaredBasis: CostBlock[] = []
  for (const record of records) {
    blocks.push(record.cost)
    if (costBasis !== undefined && record.cost.basis === costBasis) {
      blocksOnDeclaredBasis.push(record.cost)
    }
  }

  return {
    harness,
    recordCount: records.length,
    turns: turnRecords.length,
    tokens: { input, freshInput, cacheRead, output, all: input + output },
    schemaTokens,
    schemaContentPresent,
    cost: summarizeCost(blocks),
    costOnDeclaredBasis: summarizeCost(
      costBasis === undefined ? blocks : blocksOnDeclaredBasis
    ),
    unmeasurable,
  }
}

// ---------------------------------------------------------------------------
// Cell construction
// ---------------------------------------------------------------------------

const countFormat = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 })
const shareFormat = new Intl.NumberFormat('en-US', {
  style: 'percent',
  maximumFractionDigits: 1,
})

function formatCurrencyValue(value: number, currency: string): string {
  return new Intl.NumberFormat('en-US', { style: 'currency', currency }).format(value)
}

/** R10.2's cell: no value, no zero, the pinned phrase. */
function notMeasurableCell(): MetricCell {
  return { measurable: false, availability: 'not_measurable', render: NOT_MEASURABLE }
}

/** A measurable metric that still carries no figure — the reason in words. */
function measurableWithoutValue(
  render: string,
  availability: MetricAvailability = 'measured'
): MetricCell {
  return { measurable: true, availability, render }
}

function measuredTokensCell(value: number): MetricCell {
  return { measurable: true, availability: 'measured', value, render: countFormat.format(value) }
}

function measuredShareCell(value: number): MetricCell {
  return { measurable: true, availability: 'measured', value, render: shareFormat.format(value) }
}

function ratio(numerator: number, denominator: number): number | undefined {
  return denominator === 0 ? undefined : numerator / denominator
}

type TokenMetricDef = {
  metric: string
  kind: MetricKind
  label: string
  unit: MetricUnit
  /**
   * Canon keys that must not be declared not measurable for the row to
   * carry a figure — content keys adapters stamp (`tool_definitions`) and
   * token-class keys (`cache_read`). A record declaring the row's own
   * metric key not measurable poisons it too, whatever else it exports.
   */
  requires?: string[]
  compute: (figures: HarnessFigures) => MetricCell
}

const TOKEN_METRICS: TokenMetricDef[] = [
  {
    metric: 'tokens_per_turn',
    kind: 'per_turn',
    label: 'Tokens per turn',
    unit: 'tokens',
    compute: (f) => measuredTokensCell(f.tokens.all / f.turns),
  },
  {
    metric: 'input_tokens_per_turn',
    kind: 'per_turn',
    label: 'Input tokens per turn',
    unit: 'tokens',
    compute: (f) => measuredTokensCell(f.tokens.input / f.turns),
  },
  {
    metric: 'output_tokens_per_turn',
    kind: 'per_turn',
    label: 'Output tokens per turn',
    unit: 'tokens',
    compute: (f) => measuredTokensCell(f.tokens.output / f.turns),
  },
  {
    metric: 'fresh_input_per_turn',
    kind: 'per_turn',
    label: 'Fresh input per turn',
    unit: 'tokens',
    // The comparison-table face of R7.5's sharp-rise signal: how much new
    // material each turn carries, cache aside.
    compute: (f) => measuredTokensCell(f.tokens.freshInput / f.turns),
  },
  {
    metric: 'cache_read_share_per_turn',
    kind: 'per_turn',
    label: 'Cache-read share of input',
    unit: 'share',
    requires: ['cache_read'],
    compute: (f) => {
      const share = ratio(f.tokens.cacheRead, f.tokens.input)
      return share === undefined
        ? measurableWithoutValue(NO_INPUT)
        : measuredShareCell(share)
    },
  },
  {
    metric: 'schema_cost_per_turn',
    kind: 'per_turn',
    label: 'Tool-schema tokens per turn',
    unit: 'tokens',
    requires: ['tool_definitions', 'schema_cost'],
    // Derived by tokenizing definition content (R4.6): a lower bound, never
    // a harness counter, and labeled as such. A harness that exports no
    // definitions — pi, measured at 14 tools invoked and none exported —
    // never reaches here; the availability check returns not measurable.
    compute: (f) => {
      if (!f.schemaContentPresent) {
        return measurableWithoutValue(NO_TOOL_DEFINITIONS, 'derived')
      }
      const perTurn = f.schemaTokens / f.turns
      return {
        measurable: true,
        availability: 'derived',
        value: perTurn,
        render: `~${countFormat.format(perTurn)} (derived, lower bound)`,
      }
    },
  },
  {
    metric: 'turns',
    kind: 'total',
    label: 'Turns',
    unit: 'count',
    compute: (f) => measuredTokensCell(f.turns),
  },
  {
    metric: 'total_tokens',
    kind: 'total',
    label: 'Total tokens',
    unit: 'tokens',
    compute: (f) => measuredTokensCell(f.tokens.all),
  },
]

function tokenCell(figures: HarnessFigures, def: TokenMetricDef): MetricCell {
  const requires = [def.metric, ...(def.requires ?? [])]
  if (requires.some((key) => figures.unmeasurable.has(key))) {
    return notMeasurableCell()
  }
  if (figures.recordCount === 0) {
    return measurableWithoutValue(NO_RECORDS)
  }
  if (def.kind === 'per_turn' && figures.turns === 0) {
    return measurableWithoutValue(NO_TURNS)
  }
  return def.compute(figures)
}

// ---------------------------------------------------------------------------
// Cost rows — compared only through a declared basis (R10.4)
// ---------------------------------------------------------------------------

function renderUnpriced(block: CostBlock): string {
  switch (block.status) {
    case 'not_billed':
      return 'not billed'
    case 'out_of_scope':
      return 'out of scope'
    case 'partial':
      return 'not fully priced'
    default:
      return 'no published rate'
  }
}

function pricedCell(value: number, block: CostBlock): MetricCell {
  const currency = block.currency ?? 'USD'
  return {
    measurable: true,
    availability: 'measured',
    value,
    basis: block.basis,
    currency,
    render:
      formatCurrencyValue(value, currency) +
      (block.status === 'partial' ? ' (partial)' : ''),
  }
}

type CostRefusal = { render: string; problem: Problem }

/**
 * Whether the priced corpora can be compared at all (R10.4). A refusal
 * needs at least two harnesses carrying figures — one figure is a report,
 * not a comparison — and is triggered by differing bases (never blended)
 * or, under one basis, differing currencies. The refusal renders on the
 * participants' cells and is recorded as a problem for the surfaces.
 */
function costRefusal(
  participants: HarnessFigures[],
  declared: CostBasis | undefined
): CostRefusal | undefined {
  if (participants.length < 2) return undefined

  const bases = new Set(participants.map((f) => f.costOnDeclaredBasis.total.basis))
  if (bases.size > 1) {
    const list = [...bases].sort().join(', ')
    return {
      render: `not comparable: cost bases differ (${list}); declare one basis to compare through`,
      problem: {
        severity: 'warning',
        code: COST_BASIS_MISMATCH,
        message:
          `cost figures sit on more than one basis (${list}); refusing to compare them directly` +
          (declared === undefined ? ' — declare options.costBasis to compare through one' : ''),
      },
    }
  }

  const currencies = new Set(
    participants
      .map((f) => f.costOnDeclaredBasis.total.currency)
      .filter((currency): currency is string => typeof currency === 'string')
  )
  if (currencies.size > 1) {
    const list = [...currencies].sort().join(', ')
    return {
      render: `not comparable: currencies differ (${list})`,
      problem: {
        severity: 'warning',
        code: COST_CURRENCY_MISMATCH,
        message: `priced figures are in different currencies (${list}); refusing to compare them directly`,
      },
    }
  }

  return undefined
}

function costCell(
  figures: HarnessFigures,
  declared: CostBasis | undefined,
  kind: 'per_turn' | 'total',
  refusal: CostRefusal | undefined
): MetricCell {
  if (figures.recordCount === 0) {
    return measurableWithoutValue(NO_RECORDS)
  }

  const summary = figures.costOnDeclaredBasis
  if (summary.refused !== undefined) {
    return measurableWithoutValue(
      summary.refused.code === COST_CURRENCY_MISMATCH ? MIXED_CURRENCIES : MIXED_BASES
    )
  }

  if (!summary.hasValue) {
    // Figures exist, just not on the basis the caller declared: that is a
    // scoping statement, not a missing rate, and it names the basis.
    if (declared !== undefined && figures.cost.hasValue) {
      return measurableWithoutValue(`no figures on declared basis "${declared}"`)
    }
    return measurableWithoutValue(renderUnpriced(summary.total))
  }

  // The harness carries a figure, but the comparison itself is refused —
  // present the refusal rather than a number that would read as a score.
  if (refusal !== undefined) {
    return { measurable: true, availability: 'measured', render: refusal.render }
  }

  const total = summary.total.value
  if (typeof total !== 'number' || !Number.isFinite(total)) {
    return measurableWithoutValue(renderUnpriced(summary.total))
  }

  if (kind === 'per_turn') {
    if (figures.turns === 0) {
      return measurableWithoutValue(NO_TURNS)
    }
    return pricedCell(total / figures.turns, summary.total)
  }
  return pricedCell(total, summary.total)
}

type CostCells = {
  cells: Record<string, MetricCell>
  problem?: Problem
}

function costCells(
  figures: HarnessFigures[],
  declared: CostBasis | undefined,
  kind: 'per_turn' | 'total'
): CostCells {
  const participants = figures.filter((f) => f.costOnDeclaredBasis.hasValue)
  const refusal = costRefusal(participants, declared)
  const cells: Record<string, MetricCell> = {}
  for (const f of figures) {
    cells[f.harness] = costCell(f, declared, kind, refusal)
  }
  return { cells, problem: refusal?.problem }
}

// ---------------------------------------------------------------------------
// The table
// ---------------------------------------------------------------------------

type RowDef = { metric: string; kind: MetricKind; label: string; unit: MetricUnit }

const COST_PER_TURN_ROW: RowDef = {
  metric: 'cost_per_turn',
  kind: 'per_turn',
  label: 'Cost per turn',
  unit: 'currency',
}

const TOTAL_COST_ROW: RowDef = {
  metric: 'total_cost',
  kind: 'total',
  label: 'Total cost',
  unit: 'currency',
}

/** Row order is the R10.3 contract: per-turn rows, then the cost ratio, then totals. */
const ROW_ORDER: RowDef[] = [
  ...TOKEN_METRICS.filter((def) => def.kind === 'per_turn'),
  COST_PER_TURN_ROW,
  ...TOKEN_METRICS.filter((def) => def.kind === 'total'),
  TOTAL_COST_ROW,
]

/**
 * Build the cross-harness metric table (R10). `sessions[i]` is the record
 * corpus attributed to `harnesses[i]` — every canonical record the store
 * holds for that harness, from either ingest path; a turn is a record whose
 * op is a model request (`llm.invoke`), and token totals are summed over
 * turns so a trace's root and children never double-count (R4.5).
 *
 * Every cell declares availability independently of its value (R10.1); a
 * metric a harness cannot report renders `not measurable` and carries no
 * figure, never zero (R10.2). Per-turn rows lead (R10.3). Cost cells carry
 * their basis and are compared only through one — declared via
 * `options.costBasis` or agreed by the data; a mismatch renders not
 * comparable and is recorded as a problem (R10.4).
 */
export function compareHarnesses(
  sessions: CanonicalRecord[][],
  harnesses: string[],
  options: ComparisonOptions = {}
): ComparisonTable {
  if (sessions.length !== harnesses.length) {
    throw new TypeError(
      `sessions (${sessions.length}) and harnesses (${harnesses.length}) must align: one record corpus per harness`
    )
  }

  const figures = harnesses.map((harness, index) =>
    aggregate(harness, sessions[index] ?? [], options.costBasis)
  )

  const cellsByMetric = new Map<string, Record<string, MetricCell>>()
  for (const def of TOKEN_METRICS) {
    const cells: Record<string, MetricCell> = {}
    for (const f of figures) {
      cells[f.harness] = tokenCell(f, def)
    }
    cellsByMetric.set(def.metric, cells)
  }

  const perTurnCost = costCells(figures, options.costBasis, 'per_turn')
  const totalCost = costCells(figures, options.costBasis, 'total')
  cellsByMetric.set(COST_PER_TURN_ROW.metric, perTurnCost.cells)
  cellsByMetric.set(TOTAL_COST_ROW.metric, totalCost.cells)

  const rows: MetricRow[] = ROW_ORDER.map((row) => ({
    metric: row.metric,
    kind: row.kind,
    label: row.label,
    unit: row.unit,
    cells: cellsByMetric.get(row.metric) ?? {},
  }))

  const problems: Problem[] = []
  for (const result of [perTurnCost, totalCost]) {
    const problem = result.problem
    if (problem === undefined) continue
    if (
      !problems.some(
        (existing) => existing.code === problem.code && existing.message === problem.message
      )
    ) {
      problems.push(problem)
    }
  }

  return { harnesses: [...harnesses], rows, problems }
}
