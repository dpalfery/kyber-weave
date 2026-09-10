import { cn } from '../../lib/utils'
import { Card } from '../ui/card'
import type { FindingConfidenceLevel, MeasurementClassification } from '../../lib/kyberApi'

export interface ConfidencePanelProps {
  confidence: FindingConfidenceLevel
  measurementClass?: MeasurementClassification
  confidenceBasis?: string
  whatWouldRaiseIt?: string
  className?: string
}

const CONFIDENCE_TIER_CONFIG: Record<
  FindingConfidenceLevel,
  {
    label: string
    symbol: string
    badgeText: string
    multiplier: number
    rankPriority: string
    borderStyle: string
    badgeClasses: string
    containerClasses: string
    defaultBasis: string
    defaultWhatWouldRaiseIt: string
  }
> = {
  deterministic: {
    label: 'Deterministic',
    symbol: '✓',
    badgeText: '[Ground Truth — Exact]',
    multiplier: 1.0,
    rankPriority: 'Full Rank Weight (1.0x)',
    borderStyle: 'border-solid border-2 border-emerald-500/40',
    badgeClasses: 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border-emerald-500/30',
    containerClasses: 'bg-emerald-500/[0.02]',
    defaultBasis:
      'Observed directly from byte-exact token accounting and confirmed telemetry event logs without heuristic approximation.',
    defaultWhatWouldRaiseIt:
      'Maximum confidence tier reached. Telemetry provides verified ground-truth input composition and span records.',
  },
  calibrated_statistical: {
    label: 'Calibrated Statistical',
    symbol: '📊',
    badgeText: '[Statistical Model — Bounded]',
    multiplier: 0.45,
    rankPriority: 'Discounted Weight (0.45x)',
    borderStyle: 'border-dashed border-2 border-amber-500/40',
    badgeClasses: 'bg-amber-500/10 text-amber-600 dark:text-amber-400 border-amber-500/30',
    containerClasses: 'bg-amber-500/[0.02]',
    defaultBasis:
      'Derived from cross-session regression model and empirical turn distributions across comparable runs.',
    defaultWhatWouldRaiseIt:
      'To elevate to Deterministic: Export exact byte-level cache read/creation counters and unclipped prompt trace spans.',
  },
  heuristic: {
    label: 'Heuristic',
    symbol: '~',
    badgeText: '[Inferred Heuristic — Approximate]',
    multiplier: 0.1,
    rankPriority: 'Heavy Discount (0.10x)',
    borderStyle: 'border-dotted border-2 border-purple-500/40',
    badgeClasses: 'bg-purple-500/10 text-purple-600 dark:text-purple-400 border-purple-500/30',
    containerClasses: 'bg-purple-500/[0.02]',
    defaultBasis:
      'Identified via structural pattern matching and threshold heuristics without ground-truth byte verification.',
    defaultWhatWouldRaiseIt:
      'To elevate to Calibrated Statistical: Gather ≥5 completed task comparison pairs without outcome regressions. To elevate to Deterministic: Provide exact payload telemetry.',
  },
}

/**
 * Confidence rationale and measurement basis panel per Decision D5 and D6.
 * The confidence basis and what-would-raise-it notes are always shown, never collapsed behind interaction.
 * Inferred findings are visually distinguishable from deterministic ones without relying on colour alone
 * via explicit text badges, distinctive border styles (solid vs dashed vs dotted), and symbols.
 */
export function ConfidencePanel({
  confidence,
  measurementClass,
  confidenceBasis,
  whatWouldRaiseIt,
  className,
}: ConfidencePanelProps) {
  const config = CONFIDENCE_TIER_CONFIG[confidence] ?? CONFIDENCE_TIER_CONFIG.heuristic
  const basisText = confidenceBasis || config.defaultBasis
  const raiseText = whatWouldRaiseIt || config.defaultWhatWouldRaiseIt
  const isDeterministic = confidence === 'deterministic'

  return (
    <Card
      className={cn('p-5 space-y-4', config.borderStyle, config.containerClasses, className)}
      data-testid="confidence-panel"
    >
      {/* Header & Confidence Tier Badge */}
      <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border/60 pb-3">
        <div>
          <div className="flex items-center gap-2 flex-wrap">
            <h3 className="text-xs font-semibold uppercase tracking-wider text-heading">
              Confidence & Measurement Tier
            </h3>

            {/* Visual distinction without color alone: symbol + tier + explicit bracketed badge */}
            <span
              data-testid="confidence-tier-badge"
              className={cn(
                'rounded border px-2.5 py-0.5 text-xs font-semibold tracking-wide flex items-center gap-1.5',
                config.badgeClasses,
              )}
            >
              <span className="font-mono text-xs" aria-hidden="true">
                {config.symbol}
              </span>
              <span>{config.label}</span>
              <span className="text-[10px] opacity-80 uppercase tracking-wider font-normal">
                {config.badgeText}
              </span>
            </span>

            {/* Measurement Class badge if different */}
            {measurementClass && (
              <span
                data-testid="measurement-class-badge"
                className="rounded bg-interactive-secondary border border-border px-2 py-0.5 font-mono text-[10.5px] text-tertiary-foreground"
              >
                Class: {measurementClass}
              </span>
            )}
          </div>
          <p className="mt-1 text-[11px] text-muted-foreground">
            {config.rankPriority}. Deterministic observations outrank larger inferred claims.
          </p>
        </div>

        <div className="text-right shrink-0">
          <div className="text-[10px] uppercase tracking-wider text-tertiary-foreground">
            Ranking Multiplier
          </div>
          <div
            className="font-mono text-sm font-semibold tabular-nums text-foreground"
            data-testid="confidence-multiplier"
          >
            {config.multiplier.toFixed(2)}x
          </div>
        </div>
      </div>

      {/* Measurement Basis (Always shown, never collapsed per Acceptance Criteria) */}
      <div
        className="rounded-md border border-border/80 bg-card p-3 space-y-1"
        data-testid="confidence-basis"
      >
        <div className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-foreground">
          <span className="text-primary font-mono text-[11px]">§1</span>
          <span>Measurement Basis & Rationale</span>
        </div>
        <p className="text-xs text-muted-foreground leading-relaxed">
          {basisText}
        </p>
      </div>

      {/* What Would Raise Confidence (Always shown, never collapsed per Acceptance Criteria) */}
      <div
        className="rounded-md border border-border/80 bg-card p-3 space-y-1"
        data-testid="what-would-raise-it"
      >
        <div className="flex items-center gap-1.5 text-xs font-semibold uppercase tracking-wider text-foreground">
          <span className="text-primary font-mono text-[11px]">§2</span>
          <span>What Would Raise Confidence</span>
        </div>
        <p className="text-xs text-muted-foreground leading-relaxed">
          {raiseText}
        </p>
      </div>

      {/* Visual Non-Color Distinction Banner */}
      {!isDeterministic && (
        <div
          data-testid="inferred-warning-note"
          className="rounded border border-interactive-secondary bg-interactive-secondary/40 px-3 py-2 text-[11px] text-tertiary-foreground flex items-center gap-2"
        >
          <span className="font-mono text-xs font-bold text-foreground">NOTE:</span>
          <span>
            This finding is inferred or heuristic. Before modifying prompt architecture, verify against raw span telemetry or capture reproducible task runs.
          </span>
        </div>
      )}
    </Card>
  )
}
