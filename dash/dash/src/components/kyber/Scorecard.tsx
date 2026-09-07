import { cn, usd } from '../../lib/utils'
import { Card } from '../ui/card'
import type {
  ScorecardData,
  ScorecardDimensionKey,
  ScorecardDimensionValue,
} from '../../lib/kyberApi'

export interface ScorecardProps {
  data?: ScorecardData
  dimensions?: ScorecardData['dimensions']
  secondaryCost?: ScorecardData['secondaryCost']
  title?: string
  compact?: boolean
  className?: string
  onDimensionClick?: (key: ScorecardDimensionKey) => void
}

export const DIMENSION_METADATA: Record<
  ScorecardDimensionKey,
  {
    name: string
    description: string
    unitLabel?: string
    defaultUnmeasuredReason: string
  }
> = {
  contextHygiene: {
    name: 'Context Hygiene',
    description: 'Context pressure, compaction stability, and resident payload cleanliness',
    unitLabel: 'score',
    defaultUnmeasuredReason: 'Telemetry missing: context composition not recorded',
  },
  cacheEfficiency: {
    name: 'Cache Efficiency',
    description: 'Prompt cache read ratio and prefix boundary stability',
    unitLabel: 'hit rate',
    defaultUnmeasuredReason: 'Telemetry missing: harness does not export cache read/creation counters',
  },
  toolYield: {
    name: 'Tool Yield',
    description: 'Active tool invocation yield relative to resident schema definition size',
    unitLabel: 'yield',
    defaultUnmeasuredReason: 'Telemetry missing: tool call residency and schema tokens unobserved',
  },
  skillUtilisation: {
    name: 'Skill Utilisation',
    description: 'Observed activation and progressive disclosure of skill prompts',
    unitLabel: 'usage',
    defaultUnmeasuredReason: 'Telemetry missing: skill activation unobserved on this harness',
  },
  delegationOverhead: {
    name: 'Delegation Overhead',
    description: 'Token and latency overhead spent on delegation handoffs to child agents',
    unitLabel: 'ratio',
    defaultUnmeasuredReason: 'Telemetry missing: no delegation events recorded or single-agent run',
  },
  continuity: {
    name: 'Continuity',
    description: 'Session stability, user correction rate, and recovery integrity',
    unitLabel: 'continuity',
    defaultUnmeasuredReason: 'Telemetry missing: turn outcome or correction telemetry unobserved',
  },
}

export const ORDERED_DIMENSION_KEYS: ScorecardDimensionKey[] = [
  'contextHygiene',
  'cacheEfficiency',
  'toolYield',
  'skillUtilisation',
  'delegationOverhead',
  'continuity',
]

/**
 * Format dimension value into honest representation:
 * - If status is 'not_measurable' or value is null/undefined: returns '—' (em-dash).
 * - Never returns '0' or '100%' for absent/unmeasurable telemetry (ADR 0009, ADR 0011, D3).
 */
export function formatDimensionDisplay(dim?: ScorecardDimensionValue): {
  display: string
  isUnmeasurable: boolean
  reason: string
  badgeLabel?: string
} {
  if (!dim || dim.status === 'not_measurable' || dim.value === null || dim.value === undefined) {
    const reason =
      dim?.reason ||
      (dim?.key ? DIMENSION_METADATA[dim.key]?.defaultUnmeasuredReason : 'Telemetry unmeasurable') ||
      'Telemetry unmeasurable'
    return {
      display: '—',
      isUnmeasurable: true,
      reason,
      badgeLabel: 'unmeasured',
    }
  }

  if (dim.formatted) {
    return {
      display: dim.formatted,
      isUnmeasurable: false,
      reason: dim.reason || 'Measured',
      badgeLabel: dim.status,
    }
  }

  const v = dim.value
  let display = String(v)
  if (typeof v === 'number') {
    if (v >= 0 && v <= 1) {
      display = `${Math.round(v * 100)}%`
    } else {
      display = Number.isInteger(v) ? String(v) : v.toFixed(2)
    }
  }

  return {
    display,
    isUnmeasurable: false,
    reason: dim.reason || dim.status,
    badgeLabel: dim.status,
  }
}

/**
 * Scorecard Component:
 *
 * Implements Decision D3 & Decision D9:
 * 1. Renders the 6 individual diagnostic dimensions as separate independent vectors.
 * 2. STRICTLY COMPLIANT: NEVER calculates, synthesizes, or displays a composite single
 *    efficiency score, index, or overall grade.
 * 3. STRICTLY COMPLIANT with D9: Cost is displayed only as a secondary derived metric,
 *    never primary and never as an organizing/ranking principle.
 * 4. STRICTLY COMPLIANT with Acceptance Criteria: Unmeasurable telemetry renders dashes ('—')
 *    with tooltip explanations, never '0' or '100%'.
 */
export function Scorecard({
  data,
  dimensions: explicitDimensions,
  secondaryCost: explicitSecondaryCost,
  title = 'Diagnostic Scorecard (6 Dimensions)',
  compact = false,
  className,
  onDimensionClick,
}: ScorecardProps) {
  const dimensions = explicitDimensions ?? data?.dimensions
  const secondaryCost = explicitSecondaryCost ?? data?.secondaryCost

  // Build values for all 6 dimensions ensuring none are omitted
  const dimensionCards = ORDERED_DIMENSION_KEYS.map((key) => {
    const meta = DIMENSION_METADATA[key]
    const dim = dimensions?.[key] ?? {
      key,
      name: meta.name,
      status: 'not_measurable' as const,
      reason: meta.defaultUnmeasuredReason,
    }
    const formatted = formatDimensionDisplay(dim)

    return {
      key,
      meta,
      dim,
      formatted,
    }
  })

  return (
    <Card
      className={cn('overflow-hidden p-4', className)}
      data-testid="scorecard"
    >
      <div className="flex flex-wrap items-center justify-between gap-2 border-b border-border/60 pb-3">
        <div>
          <div className="flex items-center gap-2">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              {title}
            </h3>
            <span
              className="rounded bg-interactive-secondary px-1.5 py-0.5 text-[10px] font-medium text-tertiary-foreground"
              title="Per Decision D3: each diagnostic dimension is evaluated independently. No combined efficiency metric."
            >
              Independent Vectors (D3)
            </span>
          </div>
          {!compact && (
            <p className="mt-0.5 text-[11px] text-muted-foreground">
              Six-dimension diagnostic posture. Dashes (<span className="font-mono">—</span>) indicate unmeasurable telemetry, never zero.
            </p>
          )}
        </div>

        {/* Decision D9: Secondary derived cost (muted/subordinate indicator) */}
        {secondaryCost?.costUsd !== undefined && secondaryCost.costUsd !== null && (
          <div
            className="flex items-center gap-1.5 rounded-md border border-border/60 bg-interactive-secondary/40 px-2 py-1 text-right text-xs text-muted-foreground"
            data-testid="secondary-cost"
            title={`Cost: ${usd(secondaryCost.costUsd)} (${secondaryCost.basis || 'derived estimate'}) — Derived secondary metric per Decision D9`}
          >
            <span className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
              Secondary Cost:
            </span>
            <span className="font-mono text-xs font-normal text-muted-foreground tabular-nums">
              {usd(secondaryCost.costUsd)}
            </span>
            <span className="text-[9.5px] italic text-tertiary-foreground/80">
              (derived)
            </span>
          </div>
        )}
      </div>

      <div
        className={cn(
          'grid gap-3 pt-3.5',
          compact
            ? 'grid-cols-2 sm:grid-cols-3 lg:grid-cols-6'
            : 'grid-cols-1 sm:grid-cols-2 md:grid-cols-3 lg:grid-cols-6',
        )}
      >
        {dimensionCards.map(({ key, meta, dim, formatted }) => {
          const isClickable = Boolean(onDimensionClick)
          const measurementClass = dim.measurementClass || (formatted.isUnmeasurable ? 'coverage-gap' : dim.status)

          return (
            <div
              key={key}
              data-testid={`dimension-${key}`}
              onClick={() => isClickable && onDimensionClick?.(key)}
              tabIndex={isClickable ? 0 : undefined}
              role={isClickable ? 'button' : undefined}
              onKeyDown={(e) => {
                if (isClickable && (e.key === 'Enter' || e.key === ' ')) {
                  e.preventDefault()
                  onDimensionClick?.(key)
                }
              }}
              title={`${meta.name}: ${formatted.display} (${formatted.reason})`}
              className={cn(
                'flex flex-col justify-between rounded-lg border border-border/70 bg-card p-3 transition-colors',
                isClickable && 'cursor-pointer hover:border-primary/40 hover:bg-interactive-secondary/30',
                formatted.isUnmeasurable && 'bg-interactive-secondary/15',
              )}
            >
              <div>
                <div className="flex items-center justify-between gap-1">
                  <span className="truncate text-[11px] font-medium text-tertiary-foreground">
                    {meta.name}
                  </span>
                  <span
                    className={cn(
                      'rounded px-1.5 py-0.2 text-[9px] font-medium tracking-wide uppercase',
                      formatted.isUnmeasurable
                        ? 'bg-interactive-secondary text-tertiary-foreground'
                        : dim.status === 'derived'
                          ? 'bg-amber-500/10 text-amber-500 dark:text-amber-400'
                          : 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400',
                    )}
                    data-testid={`dimension-status-${key}`}
                  >
                    {formatted.badgeLabel}
                  </span>
                </div>

                <div className="mt-2.5 flex items-baseline gap-1">
                  <span
                    className={cn(
                      'font-display text-2xl tracking-tight tabular-nums',
                      formatted.isUnmeasurable
                        ? 'font-mono text-tertiary-foreground'
                        : 'text-foreground font-semibold',
                    )}
                    data-testid={formatted.isUnmeasurable ? 'dimension-unmeasurable' : 'dimension-value'}
                  >
                    {formatted.display}
                  </span>
                </div>
              </div>

              <div className="mt-3 border-t border-border/40 pt-2 text-[10.5px]">
                {formatted.isUnmeasurable ? (
                  <p
                    className="truncate text-tertiary-foreground/90 italic"
                    title={formatted.reason}
                    data-testid={`dimension-reason-${key}`}
                  >
                    {formatted.reason}
                  </p>
                ) : (
                  <div className="flex items-center justify-between gap-1 text-tertiary-foreground">
                    <span className="truncate" title={dim.detail || meta.description}>
                      {dim.detail || meta.unitLabel || 'diagnostic'}
                    </span>
                    {measurementClass && (
                      <span className="text-[9.5px] font-mono text-tertiary-foreground/75">
                        {measurementClass}
                      </span>
                    )}
                  </div>
                )}
              </div>
            </div>
          )
        })}
      </div>

      {/* Decision D9 & D3 Note Footer */}
      <div className="mt-3.5 flex flex-wrap items-center justify-between gap-2 border-t border-border/40 pt-2 text-[10px] text-tertiary-foreground">
        <span>
          Diagnostic integrity: D3 requires independent dimensions; D9 keeps cost derived & secondary.
        </span>
        <span>
          Telemetry classes: deterministic · inferred · coverage-gap
        </span>
      </div>
    </Card>
  )
}
