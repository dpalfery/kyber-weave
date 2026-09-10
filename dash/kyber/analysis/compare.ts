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
import { isNotMeasurable, notMeasurable } from '../canon/types.js'
import type { OutcomeBlock } from '../canon/outcome.js'

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
      if (isNotMeasurable(availability)) unmeasurable.add(metric)
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
  return {
    measurable: false,
    availability: notMeasurable(NOT_MEASURABLE),
    render: NOT_MEASURABLE,
  }
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

// ---------------------------------------------------------------------------
// Run and Turn Comparison Workflow (Task G4 / Decision D11)
// ---------------------------------------------------------------------------

/**
 * Task phases in the software engineering lifecycle (D11, Task G4).
 * Runs and turns are aligned by semantic phase boundaries rather than naive turn index.
 */
export type TaskPhase = 'exploration' | 'implementation' | 'verification' | 'resolution'

export const TASK_PHASES: readonly TaskPhase[] = [
  'exploration',
  'implementation',
  'verification',
  'resolution',
] as const

/**
 * TaskFamily groups runs addressing the same task, benchmark instance, or problem.
 */
export type TaskFamily = {
  id: string
  name: string
  description?: string
  workingDirectory?: string
  repo?: string
}

/**
 * Representation of one turn within a run for comparison.
 */
export type RunTurn = {
  turnIndex: number
  spanId?: string
  phase?: TaskPhase
  tokens?: {
    freshInput?: number
    cacheRead?: number
    cacheCreation?: number
    output?: number
    reportedInput?: number
    reportedOutput?: number
    all?: number
  }
  cost?: CostBlock
  tools?: string[]
  commands?: string[]
  status?: string
  summary?: string
  measurability?: Record<string, MetricAvailability>
  raw?: CanonicalRecord
}

/**
 * Status of an individual signal comparison between two turns.
 */
export type SignalComparisonStatus =
  | 'compared'
  | 'not_comparable'
  | 'missing_in_a'
  | 'missing_in_b'

export type SignalComparison = {
  name: string
  label: string
  unit?: string
  runAValue?: number | string
  runBValue?: number | string
  delta?: number
  status: SignalComparisonStatus
  reason?: string
}

/**
 * One turn-level comparison pair aligned by semantic phase (Acceptance Criteria 1 & 2).
 */
export type PhaseAlignedTurnPair = {
  phase: TaskPhase
  phaseIndex: number
  runATurn: RunTurn | null
  runBTurn: RunTurn | null
  signals: SignalComparison[]
  reading: string
}

/**
 * Comparison verdict status enforcing Decision D11 and Acceptance Criterion 3.
 */
export type ComparisonVerdictStatus =
  | 'promoted'
  | 'candidate_only'
  | 'insufficient_history'
  | 'outcome_regression'
  | 'neutral'

export type ComparisonVerdict = {
  status: ComparisonVerdictStatus
  pairCount: number
  completedPairCount: number
  meetsSufficiencyThreshold: boolean
  outcomeRegression: boolean
  canPromote: boolean
  recommendation: string
  refusalReason?: string
  summary: string
}

export type PhaseSummary = {
  phase: TaskPhase
  turnsA: number
  turnsB: number
  tokensA: number
  tokensB: number
  tokenDelta: number
  costA?: number
  costB?: number
  costDelta?: number
  reading: string
}

export type RunComparisonInput = {
  runId: string
  harness: string
  label?: string
  taskFamily?: string | TaskFamily
  workingDirectory?: string | null
  outcome?: OutcomeBlock
  turns: readonly (RunTurn | CanonicalRecord)[]
}

export type RunComparisonOptions = {
  taskFamily?: string
  costBasis?: CostBasis
  completedPairCount?: number
}

export type ComparisonSummary = {
  runA: {
    runId: string
    harness: string
    label?: string
    outcome?: OutcomeBlock
    totalTokens: number
    totalCost?: number
    turnCount: number
  }
  runB: {
    runId: string
    harness: string
    label?: string
    outcome?: OutcomeBlock
    totalTokens: number
    totalCost?: number
    turnCount: number
  }
  taskFamily?: string
  pairs: PhaseAlignedTurnPair[]
  phaseSummaries: Record<TaskPhase, PhaseSummary>
  totals: {
    tokensA: number
    tokensB: number
    tokenDelta: number
    turnCountA: number
    turnCountB: number
    turnDelta: number
    costA?: number
    costB?: number
    costDelta?: number
    costComparable: boolean
    costRefusalReason?: string
  }
  verdict: ComparisonVerdict
}

const EXPLORATION_TOOLS = new Set([
  'read_file',
  'view_file',
  'list_dir',
  'find_by_name',
  'grep_search',
  'glob',
  'search',
  'read',
  'cat',
  'ls',
  'find',
  'locate',
  'inspect',
  'fetch',
  'get',
])

const IMPLEMENTATION_TOOLS = new Set([
  'write_to_file',
  'replace_file_content',
  'edit_file',
  'create_file',
  'patch',
  'edit',
  'write',
  'apply_patch',
  'modify',
  'update',
])

const VERIFICATION_TOOLS = new Set([
  'run_tests',
  'test',
  'vitest',
  'pytest',
  'jest',
  'cypress',
  'playwright',
  'typecheck',
  'tsc',
  'lint',
  'eslint',
  'check',
  'validate',
])

const RESOLUTION_TOOLS = new Set([
  'git_commit',
  'git_push',
  'task_complete',
  'finish',
  'resolve',
  'complete',
  'submit',
])

const VERIFICATION_COMMAND_REGEX = /\b(test|vitest|pytest|jest|cypress|playwright|tsc|typecheck|eslint|lint|check)\b/i
const IMPLEMENTATION_COMMAND_REGEX = /\b(sed|awk|patch|apply|mkdir|npm\s+(?:i|install|add))\b/i
const RESOLUTION_COMMAND_REGEX = /\b(git\s+(?:commit|push|tag)|complete|finish)\b/i
const EXPLORATION_COMMAND_REGEX = /\b(find|grep|cat|ls|head|tail|view|diff|git\s+status|git\s+log|git\s+diff)\b/i

/**
 * Infer the task phase of a turn from its tool invocations, shell commands, or position.
 */
export function inferTurnPhase(
  turn: Partial<RunTurn>,
  context?: { turnIndex: number; totalTurns: number; previousPhase?: TaskPhase }
): TaskPhase {
  if (turn.phase && TASK_PHASES.includes(turn.phase)) {
    return turn.phase
  }

  const tools = (turn.tools ?? []).map((t) => t.toLowerCase())
  const commands = (turn.commands ?? []).join(' ')

  // 1. Verification: tests, linters, typecheckers
  if (
    tools.some((t) => VERIFICATION_TOOLS.has(t)) ||
    VERIFICATION_COMMAND_REGEX.test(commands)
  ) {
    return 'verification'
  }

  // 2. Resolution: git commit, git push, task_complete
  if (
    tools.some((t) => RESOLUTION_TOOLS.has(t)) ||
    RESOLUTION_COMMAND_REGEX.test(commands)
  ) {
    return 'resolution'
  }

  // 3. Implementation: writing, replacing, editing files
  if (
    tools.some((t) => IMPLEMENTATION_TOOLS.has(t)) ||
    IMPLEMENTATION_COMMAND_REGEX.test(commands)
  ) {
    return 'implementation'
  }

  // 4. Exploration: reading, searching, grepping, listing
  if (
    tools.some((t) => EXPLORATION_TOOLS.has(t)) ||
    EXPLORATION_COMMAND_REGEX.test(commands)
  ) {
    return 'exploration'
  }

  // 5. Position-based fallback
  const turnIndex = context?.turnIndex ?? turn.turnIndex ?? 0
  const totalTurns = context?.totalTurns ?? 1

  if (totalTurns > 1 && turnIndex === totalTurns - 1) {
    return 'resolution'
  }

  if (totalTurns > 2 && turnIndex < Math.max(1, Math.ceil(totalTurns * 0.35))) {
    return 'exploration'
  }

  if (context?.previousPhase) {
    return context.previousPhase
  }

  return 'exploration'
}

function extractToolsFromRecord(record: CanonicalRecord): string[] {
  const tools: string[] = []
  if (record.op === 'tool.invoke' && record.name) {
    tools.push(record.name)
  }
  if (record.raw && typeof record.raw === 'object' && !Array.isArray(record.raw)) {
    const raw = record.raw as Record<string, unknown>
    if (typeof raw.tool_name === 'string') tools.push(raw.tool_name)
    if (typeof raw.tool === 'string') tools.push(raw.tool)
    if (Array.isArray(raw.tools)) {
      for (const t of raw.tools) {
        if (typeof t === 'string') tools.push(t)
      }
    }
  }
  return [...new Set(tools)]
}

function extractCommandsFromRecord(record: CanonicalRecord): string[] {
  const commands: string[] = []
  if (record.raw && typeof record.raw === 'object' && !Array.isArray(record.raw)) {
    const raw = record.raw as Record<string, unknown>
    if (typeof raw.command === 'string') commands.push(raw.command)
    if (typeof raw.cmd === 'string') commands.push(raw.cmd)
  }
  if (record.content.instruction_context) {
    commands.push(record.content.instruction_context)
  }
  return commands
}

function toRunTurn(
  item: RunTurn | CanonicalRecord,
  index: number,
  total: number,
  previousPhase?: TaskPhase
): RunTurn {
  if ('spanId' in item && 'tokens' in item && 'op' in item) {
    const rec = item as CanonicalRecord
    const tools = extractToolsFromRecord(rec)
    const commands = extractCommandsFromRecord(rec)
    const input = rec.tokens.freshInput + rec.tokens.cacheRead + rec.tokens.cacheCreation
    const all = input + rec.tokens.output
    const turnTokens = {
      freshInput: rec.tokens.freshInput,
      cacheRead: rec.tokens.cacheRead,
      cacheCreation: rec.tokens.cacheCreation,
      output: rec.tokens.output,
      reportedInput: rec.tokens.reportedInput,
      reportedOutput: rec.tokens.reportedOutput,
      all,
    }
    const candidate: Partial<RunTurn> = {
      turnIndex: index,
      spanId: rec.spanId,
      tools,
      commands,
      tokens: turnTokens,
      cost: rec.cost,
      status: rec.status,
      measurability: rec.measurability,
      raw: rec,
    }
    const phase = inferTurnPhase(candidate, { turnIndex: index, totalTurns: total, previousPhase })
    return {
      turnIndex: index,
      spanId: rec.spanId,
      phase,
      tools,
      commands,
      tokens: turnTokens,
      cost: rec.cost,
      status: rec.status,
      measurability: rec.measurability,
      raw: rec,
    }
  }

  const t = item as RunTurn
  const phase = inferTurnPhase(t, {
    turnIndex: t.turnIndex ?? index,
    totalTurns: total,
    previousPhase,
  })
  return {
    ...t,
    turnIndex: t.turnIndex ?? index,
    phase,
  }
}

function getTurnTokens(turn: RunTurn | null): number {
  if (!turn || !turn.tokens) return 0
  if (typeof turn.tokens.all === 'number') return turn.tokens.all
  const inp = turn.tokens.reportedInput ?? (turn.tokens.freshInput ?? 0) + (turn.tokens.cacheRead ?? 0)
  return inp + (turn.tokens.output ?? 0)
}

function getTurnCost(turn: RunTurn | null): number | undefined {
  if (!turn || !turn.cost || typeof turn.cost.value !== 'number') return undefined
  return turn.cost.value
}

/**
 * Compare signals between two phase-aligned turns.
 * Unmeasurable signals render as 'not_comparable' rather than delta 0 (ADR 0009 / ADR 0011).
 */
function compareTurnSignals(
  turnA: RunTurn | null,
  turnB: RunTurn | null
): SignalComparison[] {
  const signals: SignalComparison[] = []

  // 1. Total tokens
  if (turnA !== null && turnB !== null) {
    const valA = getTurnTokens(turnA)
    const valB = getTurnTokens(turnB)
    signals.push({
      name: 'total_tokens',
      label: 'Total tokens',
      unit: 'tokens',
      runAValue: valA,
      runBValue: valB,
      delta: valB - valA,
      status: 'compared',
    })
  } else if (turnA !== null) {
    signals.push({
      name: 'total_tokens',
      label: 'Total tokens',
      unit: 'tokens',
      runAValue: getTurnTokens(turnA),
      status: 'missing_in_b',
      reason: 'Turn missing in Run B for this phase slot',
    })
  } else if (turnB !== null) {
    signals.push({
      name: 'total_tokens',
      label: 'Total tokens',
      unit: 'tokens',
      runBValue: getTurnTokens(turnB),
      status: 'missing_in_a',
      reason: 'Turn missing in Run A for this phase slot',
    })
  }

  // 2. Fresh input tokens
  if (turnA !== null && turnB !== null) {
    const freshA = turnA.tokens?.freshInput
    const freshB = turnB.tokens?.freshInput
    if (freshA !== undefined && freshB !== undefined) {
      signals.push({
        name: 'fresh_input',
        label: 'Fresh input',
        unit: 'tokens',
        runAValue: freshA,
        runBValue: freshB,
        delta: freshB - freshA,
        status: 'compared',
      })
    } else {
      signals.push({
        name: 'fresh_input',
        label: 'Fresh input',
        unit: 'tokens',
        status: 'not_comparable',
        reason: 'Fresh input telemetry unavailable in one of the paired turns',
      })
    }
  }

  // 3. Cache read share
  if (turnA !== null && turnB !== null) {
    const isCacheReadUnmeasurableA =
      turnA.measurability?.['cache_read'] !== undefined &&
      isNotMeasurable(turnA.measurability['cache_read'])
    const isCacheReadUnmeasurableB =
      turnB.measurability?.['cache_read'] !== undefined &&
      isNotMeasurable(turnB.measurability['cache_read'])

    if (isCacheReadUnmeasurableA || isCacheReadUnmeasurableB) {
      const reasonA = isCacheReadUnmeasurableA
        ? 'cache_read is not measurable in Run A'
        : ''
      const reasonB = isCacheReadUnmeasurableB
        ? 'cache_read is not measurable in Run B'
        : ''
      signals.push({
        name: 'cache_read',
        label: 'Cache-read tokens',
        unit: 'tokens',
        status: 'not_comparable',
        reason: [reasonA, reasonB].filter(Boolean).join('; '),
      })
    } else {
      const crA = turnA.tokens?.cacheRead
      const crB = turnB.tokens?.cacheRead
      if (typeof crA === 'number' && typeof crB === 'number') {
        signals.push({
          name: 'cache_read',
          label: 'Cache-read tokens',
          unit: 'tokens',
          runAValue: crA,
          runBValue: crB,
          delta: crB - crA,
          status: 'compared',
        })
      } else {
        signals.push({
          name: 'cache_read',
          label: 'Cache-read tokens',
          unit: 'tokens',
          status: 'not_comparable',
          reason: 'Cache-read tokens not reported in turn telemetry',
        })
      }
    }
  }

  // 4. Cost
  if (turnA !== null && turnB !== null) {
    const costA = turnA.cost
    const costB = turnB.cost
    if (
      costA?.status === 'priced' &&
      costB?.status === 'priced' &&
      typeof costA.value === 'number' &&
      typeof costB.value === 'number'
    ) {
      if (costA.basis === costB.basis && costA.currency === costB.currency) {
        signals.push({
          name: 'cost',
          label: 'Cost',
          unit: costA.currency ?? 'USD',
          runAValue: costA.value,
          runBValue: costB.value,
          delta: costB.value - costA.value,
          status: 'compared',
        })
      } else {
        signals.push({
          name: 'cost',
          label: 'Cost',
          status: 'not_comparable',
          reason: `Cost bases or currencies differ (${costA.basis ?? 'unknown'} vs ${costB.basis ?? 'unknown'})`,
        })
      }
    }
  }

  return signals
}

function buildTurnReading(
  phase: TaskPhase,
  turnA: RunTurn | null,
  turnB: RunTurn | null
): string {
  if (turnA !== null && turnB !== null) {
    const tokensA = getTurnTokens(turnA)
    const tokensB = getTurnTokens(turnB)
    const delta = tokensB - tokensA
    const deltaStr = delta >= 0 ? `+${delta.toLocaleString()}` : delta.toLocaleString()
    const toolsA = (turnA.tools ?? []).length > 0 ? ` [tools: ${turnA.tools!.join(', ')}]` : ''
    const toolsB = (turnB.tools ?? []).length > 0 ? ` [tools: ${turnB.tools!.join(', ')}]` : ''
    return `Phase ${phase} (turn ${turnA.turnIndex + 1} vs ${turnB.turnIndex + 1}): Run A used ${tokensA.toLocaleString()} tokens${toolsA}; Run B used ${tokensB.toLocaleString()} tokens${toolsB}. Delta: ${deltaStr} tokens.`
  }

  if (turnA !== null) {
    const tokensA = getTurnTokens(turnA)
    const toolsA = (turnA.tools ?? []).length > 0 ? ` [tools: ${turnA.tools!.join(', ')}]` : ''
    return `Phase ${phase} (turn ${turnA.turnIndex + 1}): Run A executed ${tokensA.toLocaleString()} tokens${toolsA}; Run B required no turns in this phase slot (completed phase faster).`
  }

  if (turnB !== null) {
    const tokensB = getTurnTokens(turnB)
    const toolsB = (turnB.tools ?? []).length > 0 ? ` [tools: ${turnB.tools!.join(', ')}]` : ''
    return `Phase ${phase} (turn ${turnB.turnIndex + 1}): Run B executed ${tokensB.toLocaleString()} tokens${toolsB}; Run A had no corresponding turn in this phase slot (omitted or completed faster).`
  }

  return `Phase ${phase}: No turns recorded in either run.`
}

/**
 * Aligns two runs by task phase (exploration, implementation, verification, resolution)
 * rather than naive turn index (Acceptance Criterion 1 & 2).
 * Robust to mismatched turn lengths and missing phases.
 */
export function alignByPhase(
  turnsA: readonly (RunTurn | CanonicalRecord)[],
  turnsB: readonly (RunTurn | CanonicalRecord)[]
): PhaseAlignedTurnPair[] {
  // Normalize inputs to RunTurn with resolved phases
  const normalizedA: RunTurn[] = []
  let prevPhaseA: TaskPhase | undefined
  for (let i = 0; i < turnsA.length; i++) {
    const t = toRunTurn(turnsA[i]!, i, turnsA.length, prevPhaseA)
    normalizedA.push(t)
    prevPhaseA = t.phase
  }

  const normalizedB: RunTurn[] = []
  let prevPhaseB: TaskPhase | undefined
  for (let i = 0; i < turnsB.length; i++) {
    const t = toRunTurn(turnsB[i]!, i, turnsB.length, prevPhaseB)
    normalizedB.push(t)
    prevPhaseB = t.phase
  }

  const pairs: PhaseAlignedTurnPair[] = []

  // Align turns phase by phase in semantic order
  for (const phase of TASK_PHASES) {
    const phaseTurnsA = normalizedA.filter((t) => t.phase === phase)
    const phaseTurnsB = normalizedB.filter((t) => t.phase === phase)

    const maxCount = Math.max(phaseTurnsA.length, phaseTurnsB.length)
    if (maxCount === 0) {
      continue
    }

    for (let i = 0; i < maxCount; i++) {
      const turnA = phaseTurnsA[i] ?? null
      const turnB = phaseTurnsB[i] ?? null
      const signals = compareTurnSignals(turnA, turnB)
      const reading = buildTurnReading(phase, turnA, turnB)

      pairs.push({
        phase,
        phaseIndex: i,
        runATurn: turnA,
        runBTurn: turnB,
        signals,
        reading,
      })
    }
  }

  return pairs
}

function detectOutcomeRegression(
  outcomeA?: OutcomeBlock,
  outcomeB?: OutcomeBlock
): boolean {
  if (!outcomeA || !outcomeB) return false

  // Direct status regression: success -> failure or abandoned
  if (
    outcomeA.status === 'success' &&
    (outcomeB.status === 'failure' || outcomeB.status === 'abandoned')
  ) {
    return true
  }

  // Test suite regression: tests failed in B that didn't in A
  const failedA = outcomeA.testDeltas?.after?.failed ?? 0
  const failedB = outcomeB.testDeltas?.after?.failed ?? 0
  if (failedB > failedA && failedB > 0) {
    return true
  }

  // Exit code regression: 0 in A, non-zero in B
  const exitA = outcomeA.termination?.exitCode
  const exitB = outcomeB.termination?.exitCode
  if (exitA === 0 && typeof exitB === 'number' && exitB !== 0) {
    return true
  }

  return false
}

/**
 * Compare two runs aligned by phase with outcome guard and sufficiency threshold (Task G4).
 * Enforces the candidate pairing rule: auto-pairing is proposed only, requiring n >= 5
 * completed pairs before promoting recommendations (Acceptance Criterion 3).
 */
export function compareRuns(
  runA: RunComparisonInput,
  runB: RunComparisonInput,
  options?: RunComparisonOptions
): ComparisonSummary {
  const pairs = alignByPhase(runA.turns, runB.turns)

  // Compute summaries per phase
  const phaseSummaries = {} as Record<TaskPhase, PhaseSummary>
  for (const phase of TASK_PHASES) {
    const phasePairs = pairs.filter((p) => p.phase === phase)
    const turnsA = phasePairs.filter((p) => p.runATurn !== null).length
    const turnsB = phasePairs.filter((p) => p.runBTurn !== null).length
    let tokensA = 0
    let tokensB = 0
    let costA: number | undefined
    let costB: number | undefined

    for (const p of phasePairs) {
      if (p.runATurn) {
        tokensA += getTurnTokens(p.runATurn)
        const c = getTurnCost(p.runATurn)
        if (c !== undefined) costA = (costA ?? 0) + c
      }
      if (p.runBTurn) {
        tokensB += getTurnTokens(p.runBTurn)
        const c = getTurnCost(p.runBTurn)
        if (c !== undefined) costB = (costB ?? 0) + c
      }
    }

    const tokenDelta = tokensB - tokensA
    const costDelta =
      costA !== undefined && costB !== undefined ? costB - costA : undefined

    let reading = `Phase ${phase}: ${turnsA} turns in Run A (${tokensA.toLocaleString()} tokens); ${turnsB} turns in Run B (${tokensB.toLocaleString()} tokens).`
    if (turnsA === 0 && turnsB > 0) {
      reading = `Phase ${phase}: Omitted in Run A; Run B executed ${turnsB} turns (${tokensB.toLocaleString()} tokens).`
    } else if (turnsB === 0 && turnsA > 0) {
      reading = `Phase ${phase}: Executed ${turnsA} turns in Run A (${tokensA.toLocaleString()} tokens); omitted in Run B.`
    }

    phaseSummaries[phase] = {
      phase,
      turnsA,
      turnsB,
      tokensA,
      tokensB,
      tokenDelta,
      costA,
      costB,
      costDelta,
      reading,
    }
  }

  // Compute total aggregates
  let totalTokensA = 0
  let totalTokensB = 0
  let totalCostA: number | undefined
  let totalCostB: number | undefined

  for (const p of pairs) {
    if (p.runATurn) {
      totalTokensA += getTurnTokens(p.runATurn)
      const c = getTurnCost(p.runATurn)
      if (c !== undefined) totalCostA = (totalCostA ?? 0) + c
    }
    if (p.runBTurn) {
      totalTokensB += getTurnTokens(p.runBTurn)
      const c = getTurnCost(p.runBTurn)
      if (c !== undefined) totalCostB = (totalCostB ?? 0) + c
    }
  }

  const turnCountA = runA.turns.length
  const turnCountB = runB.turns.length
  const tokenDelta = totalTokensB - totalTokensA
  const turnDelta = turnCountB - turnCountA

  // Check cost comparability
  let costComparable = true
  let costRefusalReason: string | undefined
  if (totalCostA !== undefined && totalCostB !== undefined) {
    const basisA = runA.turns[0]?.cost?.basis
    const basisB = runB.turns[0]?.cost?.basis
    if (basisA && basisB && basisA !== basisB) {
      costComparable = false
      costRefusalReason = `Cost bases differ (${basisA} vs ${basisB}); refusing to compare costs directly`
    }
  }

  // Outcome comparison & verdict (Acceptance Criterion 3)
  const outcomeRegression = detectOutcomeRegression(runA.outcome, runB.outcome)
  const completedPairCount =
    options?.completedPairCount ??
    (runA.outcome?.status === 'success' && runB.outcome?.status === 'success' ? 1 : 0)
  const meetsSufficiencyThreshold = completedPairCount >= 5

  let status: ComparisonVerdictStatus = 'insufficient_history'
  let canPromote = false
  let recommendation = ''
  let refusalReason: string | undefined

  if (outcomeRegression) {
    status = 'outcome_regression'
    canPromote = false
    refusalReason =
      'Promotion refused: outcome regression detected between paired runs. Modifications cannot be recommended when correctness or test outcomes regress.'
    recommendation =
      'Do not adopt changes from Run B: task outcome regressed compared to baseline Run A.'
  } else if (!meetsSufficiencyThreshold) {
    status = 'insufficient_history'
    canPromote = false
    refusalReason = `Auto-promotion refused: observed ${completedPairCount} completed pair(s), but minimum threshold is n >= 5 completed pairs. Automatic pairing is proposed only and requires manual confirmation.`
    recommendation =
      'Pairing proposed for manual review. At least 5 completed pairs without outcome regression are required before promoting automated recommendations.'
  } else {
    status = 'promoted'
    canPromote = true
    recommendation =
      'Sufficiency threshold satisfied (n >= 5 completed pairs) with no outcome regression. Run comparison recommendation promoted.'
  }

  const summary = `Compared Run ${runA.runId} against Run ${runB.runId}: ${pairs.length} phase-aligned pairs across ${TASK_PHASES.length} phases. Outcome: ${runA.outcome?.status ?? 'unobserved'} -> ${runB.outcome?.status ?? 'unobserved'}.`

  const verdict: ComparisonVerdict = {
    status,
    pairCount: pairs.length,
    completedPairCount,
    meetsSufficiencyThreshold,
    outcomeRegression,
    canPromote,
    recommendation,
    refusalReason,
    summary,
  }

  const taskFamily =
    typeof runA.taskFamily === 'string'
      ? runA.taskFamily
      : typeof runB.taskFamily === 'string'
        ? runB.taskFamily
        : runA.taskFamily?.id ?? runB.taskFamily?.id ?? options?.taskFamily

  return {
    runA: {
      runId: runA.runId,
      harness: runA.harness,
      label: runA.label,
      outcome: runA.outcome,
      totalTokens: totalTokensA,
      totalCost: totalCostA,
      turnCount: turnCountA,
    },
    runB: {
      runId: runB.runId,
      harness: runB.harness,
      label: runB.label,
      outcome: runB.outcome,
      totalTokens: totalTokensB,
      totalCost: totalCostB,
      turnCount: turnCountB,
    },
    taskFamily,
    pairs,
    phaseSummaries,
    totals: {
      tokensA: totalTokensA,
      tokensB: totalTokensB,
      tokenDelta,
      turnCountA,
      turnCountB,
      turnDelta,
      costA: totalCostA,
      costB: totalCostB,
      costDelta:
        totalCostA !== undefined && totalCostB !== undefined
          ? totalCostB - totalCostA
          : undefined,
      costComparable,
      costRefusalReason,
    },
    verdict,
  }
}
