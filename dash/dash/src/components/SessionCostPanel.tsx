import { useMemo } from 'react'
import { cn, fmtNum, fmtTokens } from '../lib/utils'
import { renderCost, sumCosts, COST_BASIS_MISMATCH } from '../../../kyber/canon/cost.js'
import type { CostBasis, CostBlock, CostStatus } from '../../../kyber/canon/types.js'

export type { CostBasis, CostBlock, CostStatus }

export interface SessionCostPanelProps {
  session?: any
  cost?: CostBlock | CostBlock[] | any
  costs?: CostBlock[]
  className?: string
}

export const STATUS_WORDS: Record<CostStatus, string> = {
  priced: 'priced',
  partial: 'partially priced',
  no_rate: 'no published rate',
  out_of_scope: 'out of scope',
  not_billed: 'not billed',
}

/**
 * Format a cost block for display according to KyberDash cost rules:
 * - 'priced' displays formatted money via renderCost
 * - 'partial' displays "partially priced"
 * - 'not_billed' displays "not billed"
 * - 'out_of_scope' displays "out of scope"
 * - 'no_rate' displays "no published rate"
 * No status other than 'priced' ever displays a currency figure or $0.00.
 */
export function formatCostFigure(block: CostBlock): string {
  if (
    block.status === 'priced' &&
    typeof block.value === 'number' &&
    Number.isFinite(block.value) &&
    typeof block.currency === 'string'
  ) {
    return renderCost(block)
  }
  if (block.status === 'partial') {
    return STATUS_WORDS.partial
  }
  if (block.status === 'not_billed') {
    return renderCost({ basis: block.basis, status: 'not_billed' })
  }
  if (block.status === 'out_of_scope') {
    return renderCost({ basis: block.basis, status: 'out_of_scope' })
  }
  return renderCost({ basis: block.basis, status: 'no_rate' })
}

/**
 * Normalizes an arbitrary cost block or legacy object into canonical CostBlock shape.
 */
export function normalizeCostBlock(raw: any): CostBlock {
  if (!raw || typeof raw !== 'object') {
    return { basis: 'unknown', status: 'no_rate' }
  }

  let basis: CostBasis = 'unknown'
  if (raw.basis === 'published' || raw.basis === 'published_rates') {
    basis = 'published'
  } else if (raw.basis === 'harness' || raw.basis === 'harness_reported') {
    basis = 'harness'
  } else if (raw.basis === 'unknown') {
    basis = 'unknown'
  }

  let status: CostStatus = 'no_rate'
  if (
    raw.status === 'priced' ||
    raw.status === 'partial' ||
    raw.status === 'no_rate' ||
    raw.status === 'out_of_scope' ||
    raw.status === 'not_billed'
  ) {
    status = raw.status
  } else if (raw.status === 'ok' || raw.status === 'success') {
    status = 'priced'
  }

  const value =
    typeof raw.value === 'number' && Number.isFinite(raw.value)
      ? raw.value
      : typeof raw.usd === 'number' && Number.isFinite(raw.usd)
        ? raw.usd
        : undefined

  const currency =
    typeof raw.currency === 'string'
      ? raw.currency
      : value !== undefined
        ? 'USD'
        : undefined

  let byModel: Record<string, number> | undefined
  if (raw.byModel && typeof raw.byModel === 'object' && Object.keys(raw.byModel).length > 0) {
    byModel = raw.byModel
  } else if (raw.by_model && Array.isArray(raw.by_model) && raw.by_model.length > 0) {
    const entries = raw.by_model
      .filter((m: any) => m && m.model && typeof (m.usd ?? m.value) === 'number')
      .map((m: any) => [m.model, m.usd ?? m.value] as [string, number])
    if (entries.length > 0) {
      byModel = Object.fromEntries(entries)
    }
  }

  return {
    basis,
    status,
    ...(status === 'priced' && value !== undefined ? { value } : {}),
    ...(currency !== undefined ? { currency } : {}),
    ...(byModel ? { byModel } : {}),
  }
}

/**
 * Extracts all cost blocks from props or session.
 */
export function extractCostBlocks(props: SessionCostPanelProps): CostBlock[] {
  if (props.costs && Array.isArray(props.costs) && props.costs.length > 0) {
    return props.costs.map(normalizeCostBlock)
  }
  if (props.cost) {
    if (Array.isArray(props.cost)) {
      return props.cost.map(normalizeCostBlock)
    }
    return [normalizeCostBlock(props.cost)]
  }

  const session = props.session
  if (!session) {
    return [{ basis: 'unknown', status: 'no_rate' }]
  }

  if (Array.isArray(session.costs) && session.costs.length > 0) {
    return session.costs.map(normalizeCostBlock)
  }
  if (session.summary?.costs && Array.isArray(session.summary.costs) && session.summary.costs.length > 0) {
    return session.summary.costs.map(normalizeCostBlock)
  }
  if (session.summary?.cost) {
    if (Array.isArray(session.summary.cost)) {
      return session.summary.cost.map(normalizeCostBlock)
    }
    return [normalizeCostBlock(session.summary.cost)]
  }
  if (session.cost) {
    if (Array.isArray(session.cost)) {
      return session.cost.map(normalizeCostBlock)
    }
    return [normalizeCostBlock(session.cost)]
  }

  return [{ basis: 'unknown', status: 'no_rate' }]
}

export function SessionCostPanel({ session, cost, costs, className }: SessionCostPanelProps) {
  const blocks = useMemo(() => extractCostBlocks({ session, cost, costs }), [session, cost, costs])
  const summary = session?.summary ?? {}

  // Detect mismatch between cost bases:
  // 1. If multiple blocks have differing bases, sumCosts will refuse to blend them.
  // 2. Or if session records a problem with code COST_BASIS_MISMATCH.
  const mismatchMessage = useMemo(() => {
    if (blocks.length > 1) {
      const sumResult = sumCosts(blocks)
      if (!sumResult.ok && sumResult.problem?.code === COST_BASIS_MISMATCH) {
        return sumResult.problem.message
      }
    }

    if (session?.problems && Array.isArray(session.problems)) {
      const problem = session.problems.find((p: any) => p.code === COST_BASIS_MISMATCH)
      if (problem) {
        return problem.message
      }
    }

    if (session?.cost_basis_mismatch || summary?.cost_basis_mismatch) {
      return typeof session?.cost_basis_mismatch === 'string'
        ? session.cost_basis_mismatch
        : typeof summary?.cost_basis_mismatch === 'string'
          ? summary.cost_basis_mismatch
          : 'Cost bases differ across session records; refusing to blend them into one total.'
    }

    return null
  }, [blocks, session, summary])

  // Token totals from payload.summary
  const totalInput = typeof summary.total_input === 'number' ? summary.total_input : null
  const totalOutput = typeof summary.total_output === 'number' ? summary.total_output : null
  const totalCacheRead = typeof summary.total_cache_read === 'number' ? summary.total_cache_read : null
  const totalCacheCreation = typeof summary.total_cache_creation === 'number' ? summary.total_cache_creation : null

  // Cache hit ratio: compute honestly from total_cache_read / total_input if total_input > 0,
  // or use explicit summary.cache_hit_ratio if present. Never fabricate 0% if unmeasured.
  const cacheHitRatio = useMemo(() => {
    if (typeof summary.cache_hit_ratio === 'number' && Number.isFinite(summary.cache_hit_ratio)) {
      return summary.cache_hit_ratio
    }
    if (totalInput !== null && totalCacheRead !== null && totalInput > 0) {
      return totalCacheRead / totalInput
    }
    return null
  }, [summary.cache_hit_ratio, totalInput, totalCacheRead])

  // Per-model breakdown: only present if at least one CostBlock has a non-empty byModel
  const modelEntries = useMemo(() => {
    const entries: Array<{ model: string; amount: number; currency: string; basis: string }> = []
    for (const block of blocks) {
      if (block.byModel && typeof block.byModel === 'object') {
        const cur = block.currency ?? 'USD'
        for (const [model, amount] of Object.entries(block.byModel)) {
          if (typeof amount === 'number' && Number.isFinite(amount)) {
            entries.push({ model, amount, currency: cur, basis: block.basis })
          }
        }
      }
    }
    return entries
  }, [blocks])

  const hasByModel = modelEntries.length > 0

  return (
    <div
      data-testid="session-cost-panel"
      className={cn(
        'rounded-lg border border-border bg-card p-4 text-card-foreground shadow-xs mt-3',
        className
      )}
    >
      {/* Header */}
      <div className="flex flex-wrap items-start justify-between gap-4 border-b border-border pb-3">
        <div>
          <h3 className="text-sm font-semibold text-foreground">Session Cost & Token Accounting</h3>
          <p className="mt-0.5 text-xs text-muted-foreground">
            Cost figures strictly categorized by basis. Different bases are never blended.
          </p>
        </div>
      </div>

      {/* Basis Mismatch Warning */}
      {mismatchMessage && (
        <div
          data-testid="cost-basis-mismatch-warning"
          className="mt-3 rounded-md border border-amber-500/30 bg-amber-500/10 px-3.5 py-2.5 text-xs text-amber-800 dark:text-amber-200"
        >
          <div className="font-semibold flex items-center gap-1.5">
            <span>⚠️ Cost Basis Warning</span>
          </div>
          <p className="mt-0.5">{mismatchMessage}</p>
        </div>
      )}

      {/* Cost Cards by Basis */}
      <div
        data-testid="cost-bases-container"
        className={cn(
          'mt-3 grid gap-3',
          blocks.length > 1 ? 'grid-cols-1 sm:grid-cols-2' : 'grid-cols-1'
        )}
      >
        {blocks.map((block, idx) => {
          const isPriced = block.status === 'priced' && block.value !== undefined
          const basisLabel =
            block.basis === 'published'
              ? 'Published Rates'
              : block.basis === 'harness'
                ? 'Harness Reported'
                : 'Unknown Basis'

          return (
            <div
              key={`${block.basis}-${idx}`}
              data-testid={`cost-basis-card-${block.basis}`}
              className="rounded-md border border-border bg-interactive-secondary/30 p-3.5 flex flex-col justify-between"
            >
              <div className="flex items-center justify-between">
                <span className="text-[11px] uppercase tracking-wider text-tertiary-foreground font-medium">
                  {basisLabel}
                </span>
                <span
                  data-testid={`cost-basis-badge-${block.basis}`}
                  className={cn(
                    'rounded px-1.5 py-0.5 font-mono text-[10px] uppercase font-semibold',
                    block.basis === 'harness'
                      ? 'bg-primary/15 text-primary'
                      : block.basis === 'published'
                        ? 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400'
                        : 'bg-muted text-muted-foreground'
                  )}
                >
                  {block.basis}
                </span>
              </div>

              <div className="mt-2 flex items-baseline justify-between gap-2">
                <div
                  data-testid={`cost-value-${block.basis}`}
                  className={cn(
                    'text-2xl font-semibold tabular-nums tracking-tight',
                    isPriced ? 'text-foreground' : 'text-muted-foreground text-base'
                  )}
                >
                  {formatCostFigure(block)}
                </div>
                <span
                  data-testid={`cost-status-${block.basis}`}
                  className="text-xs text-tertiary-foreground font-medium"
                >
                  {STATUS_WORDS[block.status] ?? block.status}
                </span>
              </div>

              {block.status !== 'priced' && (
                <div className="mt-1 text-[11px] text-tertiary-foreground">
                  {block.status === 'not_billed' && 'Model is explicitly not billed by provider.'}
                  {block.status === 'out_of_scope' && 'Harness is outside rate table applicability.'}
                  {block.status === 'no_rate' && 'No published rate available for this model.'}
                  {block.status === 'partial' && 'Only a portion of turns could be priced.'}
                </div>
              )}
            </div>
          )
        })}
      </div>

      {/* Per-model Breakdown (strictly omitted if byModel absent) */}
      {hasByModel && (
        <div data-testid="cost-by-model" className="mt-4 border-t border-border pt-3">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-heading mb-2">
            Per-Model Breakdown
          </h4>
          <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-3 gap-2">
            {modelEntries.map(({ model, amount, currency, basis }, index) => {
              const formatted = new Intl.NumberFormat('en-US', {
                style: 'currency',
                currency,
              }).format(amount)
              return (
                <div
                  key={`${basis}-${model}-${index}`}
                  data-testid={`cost-model-row-${model}`}
                  className="rounded border border-border bg-interactive-secondary/20 px-3 py-2 text-xs flex items-center justify-between"
                >
                  <span className="font-mono text-foreground truncate mr-2" title={model}>
                    {model}
                  </span>
                  <span className="font-semibold tabular-nums text-foreground">
                    {formatted}
                  </span>
                </div>
              )
            })}
          </div>
        </div>
      )}

      {/* Token Totals Behind Cost */}
      <div data-testid="cost-tokens-section" className="mt-4 border-t border-border pt-3">
        <div className="flex items-center justify-between mb-2">
          <h4 className="text-xs font-semibold uppercase tracking-wider text-heading">
            Underlying Token Totals
          </h4>
          <span className="text-[11px] text-tertiary-foreground">
            Measured tokens behind pricing
          </span>
        </div>

        <div className="grid grid-cols-2 sm:grid-cols-5 gap-2.5 text-xs">
          {/* Total Input */}
          <div className="rounded border border-border bg-card p-2.5">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Total Input
            </div>
            <div
              data-testid="token-total-input"
              className="mt-1 text-base font-semibold tabular-nums text-foreground"
            >
              {totalInput !== null ? fmtTokens(totalInput) : 'not measurable'}
            </div>
            <div className="text-[10px] text-tertiary-foreground truncate">
              {totalInput !== null ? `${fmtNum(totalInput)} tokens` : 'not measurable'}
            </div>
          </div>

          {/* Total Output */}
          <div className="rounded border border-border bg-card p-2.5">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Total Output
            </div>
            <div
              data-testid="token-total-output"
              className="mt-1 text-base font-semibold tabular-nums text-chart-5"
            >
              {totalOutput !== null ? fmtTokens(totalOutput) : 'not measurable'}
            </div>
            <div className="text-[10px] text-tertiary-foreground truncate">
              {totalOutput !== null ? `${fmtNum(totalOutput)} tokens` : 'not measurable'}
            </div>
          </div>

          {/* Cache Read */}
          <div className="rounded border border-border bg-card p-2.5">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Cache Read
            </div>
            <div
              data-testid="token-total-cache-read"
              className="mt-1 text-base font-semibold tabular-nums text-chart-2"
            >
              {totalCacheRead !== null ? fmtTokens(totalCacheRead) : 'not measurable'}
            </div>
            <div className="text-[10px] text-tertiary-foreground truncate">
              {totalCacheRead !== null ? `${fmtNum(totalCacheRead)} tokens` : 'not measurable'}
            </div>
          </div>

          {/* Cache Creation */}
          <div className="rounded border border-border bg-card p-2.5">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Cache Creation
            </div>
            <div
              data-testid="token-total-cache-creation"
              className="mt-1 text-base font-semibold tabular-nums text-chart-6"
            >
              {totalCacheCreation !== null ? fmtTokens(totalCacheCreation) : 'not measurable'}
            </div>
            <div className="text-[10px] text-tertiary-foreground truncate">
              {totalCacheCreation !== null ? `${fmtNum(totalCacheCreation)} tokens` : 'not measurable'}
            </div>
          </div>

          {/* Cache Hit Ratio */}
          <div className="rounded border border-border bg-card p-2.5 col-span-2 sm:col-span-1">
            <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Cache Hit Ratio
            </div>
            <div
              data-testid="token-cache-hit-ratio"
              className="mt-1 text-base font-semibold tabular-nums text-foreground"
            >
              {cacheHitRatio !== null ? `${(cacheHitRatio * 100).toFixed(1)}%` : 'not measurable'}
            </div>
            <div className="text-[10px] text-tertiary-foreground truncate">
              {cacheHitRatio !== null ? 'cache_read ÷ input' : 'not measurable'}
            </div>
          </div>
        </div>
      </div>
    </div>
  )
}
