import { barPercent, formatPercentRatio } from '../../lib/labels.js'
import { cn, fmtTokens } from '../../lib/utils.js'

export interface ContextPressureStripProps {
  medianTokens?: number | null
  p95Tokens?: number | null
  maxContextTokens?: number | null
  pressureMedian?: number | null
  pressureP95?: number | null
  isUnmeasurable?: boolean
  unmeasuredReason?: string
  label?: string
  className?: string
}

export function ContextPressureStrip({
  medianTokens,
  p95Tokens,
  maxContextTokens,
  pressureMedian,
  pressureP95,
  isUnmeasurable = false,
  unmeasuredReason = 'Telemetry missing: context pressure unrecorded',
  label = 'Context Pressure',
  className,
}: ContextPressureStripProps) {
  if (isUnmeasurable || (pressureMedian === null && pressureP95 === null && !medianTokens && !p95Tokens)) {
    return (
      <div
        className={cn('rounded border border-border/70 bg-card p-3 text-xs', className)}
        data-testid="context-pressure-strip-unmeasurable"
      >
        <div className="flex items-center justify-between text-[11px] text-tertiary-foreground">
          <span className="font-medium uppercase tracking-wider">{label}</span>
          <span className="font-mono">—</span>
        </div>
        <div className="mt-2 h-2 w-full rounded-full bg-interactive-secondary overflow-hidden">
          <div className="h-full w-0" />
        </div>
        <p className="mt-1 text-[10.5px] italic text-tertiary-foreground truncate" title={unmeasuredReason}>
          {unmeasuredReason}
        </p>
      </div>
    )
  }

  const medRatio =
    typeof pressureMedian === 'number'
      ? pressureMedian
      : typeof medianTokens === 'number' && typeof maxContextTokens === 'number' && maxContextTokens > 0
        ? medianTokens / maxContextTokens
        : 0

  const p95Ratio =
    typeof pressureP95 === 'number'
      ? pressureP95
      : typeof p95Tokens === 'number' && typeof maxContextTokens === 'number' && maxContextTokens > 0
        ? p95Tokens / maxContextTokens
        : medRatio

  const medLabel = formatPercentRatio(medRatio)
  const p95Label = formatPercentRatio(p95Ratio)
  const medPercent = barPercent(medRatio)
  const p95Percent = barPercent(p95Ratio)
  const overflow = medRatio > 1 || p95Ratio > 1

  return (
    <div
      className={cn('rounded border border-border/70 bg-card p-3 text-xs', className)}
      data-testid="context-pressure-strip"
    >
      <div className="flex items-baseline justify-between text-[11px]">
        <span className="font-medium uppercase tracking-wider text-tertiary-foreground">
          {label}
        </span>
        <div className="flex items-center gap-2 font-mono text-xs tabular-nums text-foreground">
          <span title="Median context pressure">
            med: <span className="font-semibold">{medLabel}</span>
            {medianTokens !== undefined && medianTokens !== null && (
              <span className="text-tertiary-foreground text-[10.5px]"> ({fmtTokens(medianTokens)})</span>
            )}
          </span>
          <span className="text-border">|</span>
          <span title="P95 context pressure">
            p95: <span className="font-semibold">{p95Label}</span>
            {p95Tokens !== undefined && p95Tokens !== null && (
              <span className="text-tertiary-foreground text-[10.5px]"> ({fmtTokens(p95Tokens)})</span>
            )}
          </span>
        </div>
      </div>

      {/* Progress track */}
      <div className="relative mt-2 h-2.5 w-full rounded-full bg-interactive-secondary overflow-hidden">
        {/* P95 Bar */}
        <div
          className={cn(
            'absolute inset-y-0 left-0 rounded-full transition-all duration-500',
            p95Percent > 80
              ? 'bg-red-500/50'
              : p95Percent > 60
                ? 'bg-amber-500/50'
                : 'bg-primary/30',
          )}
          style={{ width: `${p95Percent}%` }}
        />
        {/* Median Bar */}
        <div
          className={cn(
            'absolute inset-y-0 left-0 rounded-full transition-all duration-500',
            medPercent > 80
              ? 'bg-red-500'
              : medPercent > 60
                ? 'bg-amber-500'
                : 'bg-primary',
          )}
          style={{ width: `${medPercent}%` }}
        />
      </div>

      <div className="mt-1 flex items-center justify-between text-[10px] text-tertiary-foreground">
        <span>0%</span>
        <span>Compaction threshold (~85%)</span>
        <span>100% {maxContextTokens ? `(${fmtTokens(maxContextTokens)})` : ''}</span>
      </div>
      {overflow && (
        <p className="mt-1 text-[10.5px] text-amber-700 dark:text-amber-400" data-testid="context-pressure-overflow">
          Window overflow: med {medLabel}, p95 {p95Label}. The bar caps at 100%; the figures do not.
        </p>
      )}
    </div>
  )
}
