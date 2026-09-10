// Cost engine primitives for KyberDash (spec: docs/specs/kyberdash, design.md
// "CostBlock", "Error Handling"). A cost figure is never a bare number: it
// always travels with the basis it was derived on (R5.1), the harness's own
// arithmetic is carried verbatim in preference to any derived figure (R5.2),
// and context tiers are selected by the measured input size — the sum of the
// disjoint input classes, never an estimate (R5.6). A table prices only the
// harnesses its applicability list names (R5.3), and a missing rate, an
// explicitly-not-billed model, and an out-of-scope harness stay distinct
// answers in words rather than a plausible $0.00 (R5.4, R5.5).

import { type CostBasis, type CostBlock, type Problem, type TokenUsage } from './types.js'

/** Token count the per-million rates are quoted against. */
export const TOKENS_PER_MILLION = 1_000_000

/**
 * One context tier of a published rate table. `upTo` is the inclusive upper
 * bound on measured input tokens; a request belongs to the tightest tier
 * whose bound still covers it.
 */
export type RateTier = {
  upTo: number
  /** Price per million input tokens. */
  inputRate: number
  /** Price per million output tokens. */
  outputRate: number
}

/**
 * A per-model entry in a table's published rates. Either the publisher's
 * explicit statement that the model is not billed (R5.5) — a different
 * answer from an absent entry, which is a missing rate (R5.4) — or flat
 * per-million prices for that one model, with no context tiers.
 */
export type Rate = { billed: false } | { billed?: true; inputRate: number; outputRate: number }

/**
 * A published rate table. `applicability` names the harnesses the table may
 * price (R5.3); a harness the list omits is not priced by this table at all,
 * even when the model matches. Rates come from the per-model `publishedRates`
 * map when the table itemizes models, else from `model` scoping plus the
 * context `tiers`.
 */
export type RateTable = {
  name: string
  currency?: string
  tiers: RateTier[]
  /** Harnesses this table may price; absent means the table names no restriction. */
  applicability?: string[]
  /** The one model this table prices, when the table is model-specific. */
  model?: string
  /** Per-model published rates; an entry absent for a model is a missing rate (R5.4). */
  publishedRates?: Map<string, Rate>
}

/** What `createCostBlock` may price from. `harnessReported` always wins. */
export type CostInput = {
  /** The harness's own figure, carried verbatim (R5.2). */
  harnessReported?: { value: number; currency: string }
  /** Published table consulted when the harness reported nothing. */
  table?: RateTable
  /** Measured token decomposition; what tier selection reads (R5.6). */
  tokens?: TokenUsage
  /** Model name, used to label the derived by-model breakdown. */
  model?: string
  /** The harness attribution whose scope against `table` decides pricing (R5.3). */
  harness?: string
}

/**
 * Measured input size: the sum of the disjoint input classes (R4.1). Tier
 * selection reads this, not `reportedInput` and not an estimate — the point
 * of R5.6 is that the tier comes from what was actually measured inbound.
 */
export function measuredInput(tokens: TokenUsage): number {
  return tokens.freshInput + tokens.cacheRead + tokens.cacheCreation
}

/**
 * Select the tier for a measured input size (R5.6): the tightest bracket
 * whose `upTo` still covers the input. Returns `undefined` when the input
 * exceeds every tier — the table offers no rate at that size, which is a
 * missing rate (R5.4), not a zero price.
 */
export function selectTier(tiers: RateTier[], inputTokens: number): RateTier | undefined {
  let tightest: RateTier | undefined
  for (const tier of tiers) {
    if (inputTokens <= tier.upTo && (tightest === undefined || tier.upTo < tightest.upTo)) {
      tightest = tier
    }
  }
  return tightest
}

/**
 * Whether a table may price a harness (R5.3). A table with no applicability
 * list names no restriction and may price anyone; a table with one prices
 * only the harnesses it names. The model a turn ran is irrelevant to this
 * question — which is exactly how two harnesses can run one model under
 * different billing without one table bleeding into the other's turns.
 */
export function isHarnessInScope(table: RateTable, harness: string | undefined): boolean {
  if (table.applicability === undefined) {
    return true
  }
  return harness !== undefined && table.applicability.includes(harness)
}

type ModelRateResolution =
  | { kind: 'tiers' }
  | { kind: 'flat'; rate: { inputRate: number; outputRate: number } }
  | { kind: 'absent' }
  | { kind: 'not_billed' }

/**
 * Resolve what a table publishes for one model. A `publishedRates` map is
 * consulted entry by entry — an absent entry is a missing rate (R5.4), an
 * entry marked `billed: false` is explicitly not billed (R5.5) — and takes
 * precedence over the single-model `model` field. With neither, the table's
 * context `tiers` price every model the table covers.
 */
function resolveModelRate(table: RateTable, model: string | undefined): ModelRateResolution {
  if (table.publishedRates !== undefined) {
    const rate = model !== undefined ? table.publishedRates.get(model) : undefined
    if (rate === undefined) {
      return { kind: 'absent' }
    }
    if (rate.billed === false) {
      return { kind: 'not_billed' }
    }
    return { kind: 'flat', rate }
  }
  if (table.model !== undefined && table.model !== model) {
    return { kind: 'absent' }
  }
  return { kind: 'tiers' }
}

/**
 * Price one (harness, model) pair against a published table. Scope is judged
 * before any rate is read: a harness the applicability list does not name is
 * `out_of_scope` and the table contributes nothing to it (R5.3) — no figure,
 * no fallback, the turn is left for another table to price. A model the
 * table does not publish is `no_rate` (R5.4); a model the publisher
 * explicitly does not bill is `not_billed` (R5.5), a third answer. Only a
 * harness in scope under a published rate is priced — from the model's flat
 * entry, or from the context tier the measured input selects (R5.6). An
 * input beyond every tier is a missing rate (R5.4), never a zero price.
 */
export function priceWithTable(
  tokens: TokenUsage,
  model: string | undefined,
  harness: string | undefined,
  table: RateTable
): CostBlock {
  if (!isHarnessInScope(table, harness)) {
    return { basis: 'published', status: 'out_of_scope' }
  }

  const resolved = resolveModelRate(table, model)
  if (resolved.kind === 'absent') {
    return { basis: 'published', status: 'no_rate' }
  }
  if (resolved.kind === 'not_billed') {
    return { basis: 'published', status: 'not_billed' }
  }

  const inputTokens = measuredInput(tokens)
  const rate =
    resolved.kind === 'flat' ? resolved.rate : selectTier(table.tiers, inputTokens)
  if (rate === undefined) {
    return { basis: 'published', status: 'no_rate' }
  }

  const value = (inputTokens * rate.inputRate + tokens.output * rate.outputRate) / TOKENS_PER_MILLION
  return {
    basis: 'published',
    status: 'priced',
    value,
    currency: table.currency ?? 'USD',
    ...(model !== undefined ? { byModel: { [model]: value } } : {}),
  }
}

/**
 * Produce a cost block carrying its basis (R5.1). A harness-reported figure
 * wins outright and is carried verbatim — no recomputation, no rounding, and
 * the table's scope is never even consulted, because the harness's own
 * figure is not a table price (R5.2 over R5.3). Otherwise the figure is
 * derived by `priceWithTable`, which prices only a harness the table's
 * applicability list names and only a model the table publishes. Anything
 * else stays unpriced rather than guessed: an absent table, an absent
 * decomposition (tier selection has nothing to read), a harness out of the
 * table's scope, a model without a published rate, or an input beyond the
 * table's largest tier all yield a block with no value, so a consumer
 * renders the reason in words instead of a plausible $0.00 (R5.4, design.md
 * "Error Handling").
 */
export function createCostBlock(input: CostInput): CostBlock {
  if (input.harnessReported) {
    const { value, currency } = input.harnessReported
    return { basis: 'harness', status: 'priced', value, currency }
  }

  if (input.table && input.tokens) {
    return priceWithTable(input.tokens, input.model, input.harness, input.table)
  }

  return { basis: 'unknown', status: 'no_rate' }
}

/**
 * Render a cost block for display (R5.4, R5.5). A block that carries a
 * figure renders as a currency amount — including a genuine $0.00, which is
 * a priced zero, not a missing rate. A block without one renders its reason
 * in words: "no published rate" for a missing rate, "not billed" for a model
 * the publisher explicitly does not bill, "out of scope" for a harness the
 * table does not name. An absent rate never renders as $0.00 — that is the
 * whole point of carrying a status alongside the value.
 */
export function renderCost(block: CostBlock): string {
  if (
    typeof block.value === 'number' &&
    Number.isFinite(block.value) &&
    typeof block.currency === 'string'
  ) {
    return new Intl.NumberFormat('en-US', { style: 'currency', currency: block.currency }).format(
      block.value
    )
  }
  if (block.status === 'not_billed') {
    return 'not billed'
  }
  if (block.status === 'out_of_scope') {
    return 'out of scope'
  }
  return 'no published rate'
}

/** Problem codes emitted by `sumCosts`. */
export const COST_BASIS_MISMATCH = 'COST_BASIS_MISMATCH'
export const COST_CURRENCY_MISMATCH = 'CURRENCY_MISMATCH'

export type CostTotal =
  | { ok: true; total: CostBlock }
  | { ok: false; problem: Problem }

/**
 * Total a collection of cost blocks under one basis (R5.1). Figures of
 * different bases are never blended: the bases (and, within them, the
 * currencies) must agree or the sum is refused with a problem naming what
 * differed, and no total is produced. The refusal is a union rather than a
 * throw to match `validateTokens` — a failure is surfaced, never guessed at.
 *
 * A total over blocks that are not all `priced` is marked `partial` and
 * carries the sum of the priced portion only; unpriced blocks contribute
 * nothing, not zero, so the missing rates stay visible (R5.4).
 */
export function sumCosts(blocks: CostBlock[]): CostTotal {
  if (blocks.length === 0) {
    // Nothing was summed, so no figure exists to present — unpriced, not $0.
    return { ok: true, total: { basis: 'unknown', status: 'no_rate' } }
  }

  const bases = new Set<CostBasis>(blocks.map((block) => block.basis))
  if (bases.size > 1) {
    return {
      ok: false,
      problem: {
        severity: 'error',
        code: COST_BASIS_MISMATCH,
        message: `cost bases differ across blocks (${[...bases].sort().join(', ')}); refusing to blend them into one total`,
      },
    }
  }
  const basis = blocks[0].basis

  // A non-finite "figure" is corruption, not a price; it counts as absent.
  const valued = blocks.filter((block) => typeof block.value === 'number' && Number.isFinite(block.value))

  const currencies = new Set(
    valued.map((block) => block.currency).filter((currency): currency is string => typeof currency === 'string')
  )
  if (currencies.size > 1) {
    return {
      ok: false,
      problem: {
        severity: 'error',
        code: COST_CURRENCY_MISMATCH,
        message: `cost figures under basis "${basis}" are in different currencies (${[...currencies].sort().join(', ')}); refusing to blend them into one total`,
      },
    }
  }

  if (valued.length === 0) {
    // No block carries a figure. One shared status keeps the reason ("no
    // published rate", "out of scope"); a mix has no single reason and
    // renders as an incomplete picture.
    const statuses = new Set(blocks.map((block) => block.status))
    const status = statuses.size === 1 ? blocks[0].status : 'partial'
    return { ok: true, total: { basis, status } }
  }

  const byModel: Record<string, number> = {}
  for (const block of valued) {
    for (const [model, amount] of Object.entries(block.byModel ?? {})) {
      byModel[model] = (byModel[model] ?? 0) + amount
    }
  }

  const fullyPriced = blocks.every(
    (block) => block.status === 'priced' && typeof block.value === 'number' && Number.isFinite(block.value)
  )

  return {
    ok: true,
    total: {
      basis,
      status: fullyPriced ? 'priced' : 'partial',
      value: valued.reduce((sum, block) => sum + (block.value as number), 0),
      ...(currencies.size === 1 ? { currency: [...currencies][0] } : {}),
      ...(Object.keys(byModel).length > 0 ? { byModel } : {}),
    },
  }
}
