import { cn, fmtTokens } from '../../lib/utils'
import { Card } from '../ui/card'

export interface RelocationAction {
  type?: 'relocate' | 'progressive_disclosure' | 'on_demand' | 'cache_reorder' | 'defer' | string
  strategy?: string
  suggestedAction?: string
  targetBlock?: string
}

export interface RecommendationPanelProps {
  recommendation: string
  estimatedWasteTokens: number
  errorBar?: {
    lower: number
    upper: number
  }
  relocationAction?: RelocationAction
  className?: string
}

/**
 * Derives a structured D8 relocation strategy from recommendation prose if not explicitly provided.
 */
function deriveRelocationStrategy(recommendation: string, actionProp?: RelocationAction): {
  strategyName: string
  badgeLabel: string
  suggestedAction: string
  reversibilityNote: string
} {
  if (actionProp?.strategy && actionProp?.suggestedAction) {
    return {
      strategyName: actionProp.strategy,
      badgeLabel: actionProp.type?.replace('_', ' ') || 'relocation action',
      suggestedAction: actionProp.suggestedAction,
      reversibilityNote: 'Reversible modification: preserves prompt material while deferring resident token overhead.',
    }
  }

  const lower = recommendation.toLowerCase()

  if (lower.includes('progressive disclosure') || lower.includes('skill') || lower.includes('on-demand')) {
    return {
      strategyName: 'Progressive Disclosure & On-Demand Loading',
      badgeLabel: 'progressive disclosure',
      suggestedAction: 'Expose context or tool definitions on demand when activated rather than loading unconditionally in system prompts.',
      reversibilityNote: 'Zero token destruction: context remains available on demand without resident overhead.',
    }
  }

  if (lower.includes('cache') || lower.includes('prefix') || lower.includes('order') || lower.includes('position')) {
    return {
      strategyName: 'Cache Breakpoint Repositioning',
      badgeLabel: 'cache reordering',
      suggestedAction: 'Relocate dynamic context after stable prompt prefixes to maximize cache hit ratio across turns.',
      reversibilityNote: 'Fully reversible: moves prompt material below the cache breakpoint without removing instructions.',
    }
  }

  return {
    strategyName: 'Context Relocation & Deferral',
    badgeLabel: 'relocate, do not delete',
    suggestedAction: 'Move volatile context items into dedicated subagent turns or lazy tool schemas rather than removing content.',
    reversibilityNote: 'Decision D8 adherence: avoids destructive deletion by preserving latent instructions.',
  }
}

/**
 * Recommendation panel rendering Decision D8 relocation/progressive disclosure actions,
 * estimated recoverable waste tokens, and calibrated error bars.
 */
export function RecommendationPanel({
  recommendation,
  estimatedWasteTokens,
  errorBar,
  relocationAction,
  className,
}: RecommendationPanelProps) {
  const strategy = deriveRelocationStrategy(recommendation, relocationAction)
  const hasErrorBar = errorBar !== undefined && errorBar !== null
  const lowerBound = hasErrorBar ? errorBar.lower : estimatedWasteTokens
  const upperBound = hasErrorBar ? errorBar.upper : estimatedWasteTokens

  // Normalized position calculation for visual error-bar range
  const maxRange = Math.max(upperBound * 1.15, estimatedWasteTokens * 1.25, 1)
  const lowerPct = Math.min(100, Math.max(0, (lowerBound / maxRange) * 100))
  const estimatePct = Math.min(100, Math.max(0, (estimatedWasteTokens / maxRange) * 100))
  const upperPct = Math.min(100, Math.max(0, (upperBound / maxRange) * 100))
  const barWidthPct = Math.max(2, upperPct - lowerPct)

  return (
    <Card className={cn('p-5 space-y-4', className)} data-testid="recommendation-panel">
      {/* Header with D8 Banner */}
      <div className="flex flex-wrap items-baseline justify-between gap-3 border-b border-border/60 pb-3">
        <div>
          <div className="flex items-center gap-2 flex-wrap">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Remediation Plan (Decision D8)
            </h3>
            <span
              className="rounded bg-primary/10 border border-primary/20 px-2 py-0.5 text-[10.5px] font-semibold text-primary uppercase tracking-wider flex items-center gap-1"
              data-testid="d8-relocation-badge"
            >
              <span>D8: Relocate, Do Not Delete</span>
            </span>
          </div>
          <p className="mt-1 text-[11px] text-muted-foreground">
            Recommendations prefer moving context over deleting it. Progressive disclosure, cache reordering, and on-demand schemas are reversible.
          </p>
        </div>

        {/* Recoverable Waste Highlight */}
        <div className="text-right shrink-0">
          <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
            Recoverable Waste Estimate
          </div>
          <div
            className="font-display text-xl font-bold text-amber-500 dark:text-amber-400 tabular-nums"
            data-testid="recoverable-waste-tokens"
          >
            {fmtTokens(estimatedWasteTokens)} tokens
          </div>
          {hasErrorBar && (
            <div
              className="text-[10.5px] font-mono text-tertiary-foreground tabular-nums"
              data-testid="recommendation-error-bar"
            >
              [{fmtTokens(lowerBound)} – {fmtTokens(upperBound)}]
            </div>
          )}
        </div>
      </div>

      {/* Primary Recommendation Text */}
      <div className="rounded-md border border-primary/20 bg-primary/5 p-3.5 text-xs text-foreground space-y-1.5">
        <div className="flex items-center gap-1.5 font-semibold text-[11px] text-primary uppercase tracking-wider">
          <span aria-hidden="true">💡</span>
          <span>Prescriptive Guidance</span>
        </div>
        <p
          className="text-foreground/90 leading-relaxed font-medium"
          data-testid="recommendation-text"
        >
          {recommendation}
        </p>
      </div>

      {/* Calibrated Error Bar Visual Gauge (Decision D5) */}
      {hasErrorBar && (
        <div
          className="rounded-md border border-border/80 bg-card p-3 space-y-2"
          data-testid="error-bar-visual"
        >
          <div className="flex items-center justify-between text-xs">
            <span className="font-semibold uppercase tracking-wider text-muted-foreground text-[10.5px]">
              Calibrated Error Bar Bounds (D5)
            </span>
            <span className="font-mono text-[11px] text-tertiary-foreground tabular-nums">
              Lower: {fmtTokens(lowerBound)} · Upper: {fmtTokens(upperBound)}
            </span>
          </div>

          {/* Visual Range Track */}
          <div className="relative h-4 bg-interactive-secondary rounded-full overflow-hidden border border-border/60">
            {/* Range Span */}
            <div
              className="absolute top-0 bottom-0 bg-amber-500/25 border-x border-amber-500/50"
              style={{
                left: `${lowerPct}%`,
                width: `${barWidthPct}%`,
              }}
              title={`Confidence Interval: ${fmtTokens(lowerBound)} to ${fmtTokens(upperBound)} tokens`}
            />
            {/* Estimate Marker */}
            <div
              className="absolute top-0 bottom-0 w-1 bg-primary z-10 -ml-0.5"
              style={{ left: `${estimatePct}%` }}
              title={`Median Recoverable Waste: ${fmtTokens(estimatedWasteTokens)} tokens`}
            />
          </div>

          <div className="flex justify-between text-[10px] font-mono text-tertiary-foreground">
            <span>0</span>
            <span className="text-primary font-semibold">Estimate: {fmtTokens(estimatedWasteTokens)}</span>
            <span>{fmtTokens(maxRange)} max</span>
          </div>
        </div>
      )}

      {/* D8 Relocation & Progressive Disclosure Action Card */}
      <div
        className="rounded-md border border-border bg-interactive-secondary/30 p-3 space-y-2"
        data-testid="relocation-action-card"
      >
        <div className="flex items-center justify-between flex-wrap gap-2">
          <div className="flex items-center gap-1.5">
            <span className="text-sm" aria-hidden="true">🔄</span>
            <span className="font-semibold text-xs text-foreground">
              {strategy.strategyName}
            </span>
          </div>
          <span className="rounded bg-interactive-secondary border border-border px-1.5 py-0.5 text-[10px] font-mono uppercase text-muted-foreground">
            {strategy.badgeLabel}
          </span>
        </div>

        <p className="text-xs text-muted-foreground leading-relaxed">
          {strategy.suggestedAction}
        </p>

        <div className="border-t border-border/50 pt-2 flex items-center justify-between text-[11px] text-tertiary-foreground">
          <span>{strategy.reversibilityNote}</span>
          <span className="rounded bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 px-1.5 py-0.5 text-[10px] font-medium">
            Reversible
          </span>
        </div>
      </div>
    </Card>
  )
}
